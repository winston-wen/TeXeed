using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using Block = System.Windows.Documents.Block;

namespace TeXeed.App.Controls;

/// <summary>Selectable native Markdown with folding and image previews.</summary>
public sealed partial class MarkdownView : RichTextBox
{
    internal static readonly RoutedEvent AsyncLayoutChangingEvent = EventManager.RegisterRoutedEvent(
        "AsyncLayoutChanging", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MarkdownView));
    private void NotifyAsyncLayoutChanging() => (_tableOwner ?? this).RaiseEvent(new RoutedEventArgs(AsyncLayoutChangingEvent));
    private const double ChatParagraphSpacing = 10;
    private const double DefaultFontSize = 16;
    private double ChatLineHeight => Math.Max(25, FontSize * AiChatTypography.LineHeight / AiChatTypography.FontSize);
    private bool UsesChatTypography => ChatMode || UseChatEmphasis ||
        _tableOwner is { } owner && (owner.ChatMode || owner.UseChatEmphasis);
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseCustomContainers().UsePipeTables().UseEmphasisExtras().UseCjkFriendlyEmphasis().UseAutoLinks().DisableHtml().Build();
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, _) => ((MarkdownView)d).Render()));
    public bool IsStreaming { get => (bool)GetValue(IsStreamingProperty); set => SetValue(IsStreamingProperty, value); }
    public static readonly DependencyProperty ChatModeProperty = DependencyProperty.Register(
        nameof(ChatMode), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, _) => ((MarkdownView)d).UpdateChatTypography()));
    public bool ChatMode { get => (bool)GetValue(ChatModeProperty); set => SetValue(ChatModeProperty, value); }
    public static readonly DependencyProperty UseChatEmphasisProperty = DependencyProperty.Register(
        nameof(UseChatEmphasis), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, _) => ((MarkdownView)d).UpdateChatTypography()));
    public bool UseChatEmphasis { get => (bool)GetValue(UseChatEmphasisProperty); set => SetValue(UseChatEmphasisProperty, value); }
    public static readonly DependencyProperty TurnNumberProperty = DependencyProperty.Register(
        nameof(TurnNumber), typeof(int), typeof(MarkdownView), new PropertyMetadata(0, (d, _) => ((MarkdownView)d).UpdateHeadingNumbers()));
    public int TurnNumber { get => (int)GetValue(TurnNumberProperty); set => SetValue(TurnNumberProperty, value); }
    private string? _imageBaseDirectory;
    public string? ImageBaseDirectory
    {
        get => _imageBaseDirectory;
        set
        {
            if (_imageBaseDirectory == value) return;
            _imageBaseDirectory = value;
            if (_hasRelativeImages || _tableViews.Any(t => t.View._hasRelativeImages)) Render();
        }
    }
    private Paragraph? _streamParagraph;
    private string _streamText = "";
    private bool _restoringSpacedEmphasis;

    internal Action<string>? SourceLink { get; set; }
    internal Action<string>? LocalLink { get; set; }
    internal bool LinkListNumbers { get; set; }
    public bool UseSystemFont { get; set; }
    internal Func<string, bool, ChatSvg?>? FormulaAsset { get; set; }
    public MarkdownView()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = false; IsDocumentEnabled = true; IsUndoEnabled = false;
        BorderThickness = new Thickness(0); Background = Brushes.Transparent; Padding = new Thickness(0, 0, 34, 0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FontSize = DefaultFontSize; FontFamily = new FontFamily("Times New Roman, Source Han Serif SC, FangSong");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(SelectionBrushProperty, "TextSelectionBackground");
        SetResourceReference(SelectionTextBrushProperty, "TextSelectionForeground");
        // RichTextBox always paints selection in an adorner, unlike TextBox/PasswordBox.
        // An opaque brush would cover the glyphs even with SelectionTextBrush set.
        SelectionOpacity = .35;
        InitializeCodeSurfaces();
        GotKeyboardFocus += (_, _) =>
        {
            if (IsKeyboardFocusWithin) _selectionView = _tableViews.FirstOrDefault(t => t.View.IsKeyboardFocusWithin)?.View;
        };
        Unloaded += (_, _) => { if (_renderPending) { _renderVersion++; _renderPaused = true; } };
        Loaded += (_, _) => { if (_renderPaused) Render(); };
        SizeChanged += (_, e) => { if (!IsStreaming && Markdown.Contains("columns") && (e.PreviousSize.Width < 560) != (e.NewSize.Width < 560)) Render(); };
        // Own the copy command so rich editors cannot prefer the default RTF/XAML formula images.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            e.Handled = true;
            var selected = SelectionView();
            try { if (!selected.Selection.IsEmpty) selected.CopyToClipboard(selected.Selection.Start, selected.Selection.End); }
            catch (System.Runtime.InteropServices.ExternalException) { ToolTip = "剪贴板暂时被占用，请重试复制。"; }
        }, (_, e) => { e.CanExecute = !SelectionView().Selection.IsEmpty; e.Handled = true; }));
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty) _readingInkAxes?.Clear();
        if (e.Property == FontSizeProperty && !_updatingChatTypography && UsesChatTypography && !string.IsNullOrEmpty(Markdown)) Render();
    }

    internal string Revision { get; private set; } = "";
    internal int LayoutVersion { get; private set; }
    internal Task RenderTask { get; private set; } = Task.CompletedTask;
    private int _renderVersion;
    private bool _renderPending, _renderPaused, _updatingChatTypography;
    private double _estimatedHeight;
    private const int BackgroundRenderThreshold = 4000, UiRenderSliceMilliseconds = 6;
    private void UpdateChatTypography()
    {
        var chat = ChatMode || UseChatEmphasis;
        if (chat) FontFamily = ChatReadingTypography.Family;
        _updatingChatTypography = true;
        if (chat && FontSize == DefaultFontSize) SetCurrentValue(FontSizeProperty, AiChatTypography.FontSize);
        else if (!chat && FontSize == AiChatTypography.FontSize) SetCurrentValue(FontSizeProperty, DefaultFontSize);
        _updatingChatTypography = false;
        Typography.SetStylisticSet4(this, chat);
        if (IsStreaming) _streamParagraph = null;
        Render();
    }
    private void Render()
    {
        var version = ++_renderVersion;
        _renderPending = false; _renderPaused = false;
        LayoutVersion++;
        Revision = TeXeed.App.AI.AiStorage.Hash(RevisionSourceOverride ?? Markdown ?? "");
        if (IsStreaming)
        {
            // Keep appending to the existing document and re-evaluate only the final grapheme.
            if (_streamParagraph is null)
            {
                ClearRichState();
                _streamText = "";
                ResetStreamEmoji();
                _streamParagraph = new Paragraph();
                var document = NewDocument();
                document.Blocks.Add(_streamParagraph);
                Document = document;
            }
            if (ChatMode && _streamParagraph is { } streamParagraph)
            {
                streamParagraph.Margin = new Thickness(0);
                streamParagraph.LineHeight = ChatLineHeight;
                streamParagraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
            var next = Markdown ?? "";
            AppendStreamEmoji(_streamParagraph, next);
            RenderTask = Task.CompletedTask;
            return;
        }
        _streamParagraph = null; _streamText = ""; ResetStreamEmoji();
        var source = Markdown ?? "";
        if (source.Length > BackgroundRenderThreshold)
        {
            _renderPending = true;
            _estimatedHeight = Math.Max(80, (source.Count(c => c == '\n') + source.Length / 90) * 18);
            Document = NewDocument(); Document.Blocks.Add(new Paragraph(new Run("正在排版…")));
            RenderTask = RenderDeferredAsync(source, version);
        }
        else
        {
            var document = NewDocument(); ClearRichState(); _math = new ChatMath();
            try { var blocks = ChatMarkdown.Parse(_math.Prepare(source, Pipeline), Pipeline, out _restoringSpacedEmphasis); AddBlocks(document.Blocks, blocks, 0); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CodeFallback(document, source); }
            if (_tableViews.Count == 0) _math.ReleaseOriginalSource();
            TrimAnswerEnd(document); ApplyFolding(document); Document = document; RenderTask = Task.CompletedTask;
        }
    }
    private FlowDocument NewDocument()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            FontSize = FontSize, FontFamily = FontFamily, LineHeight = ChatMode || UseChatEmphasis ? ChatLineHeight : 25 };
        Typography.SetStylisticSet4(document, UsesChatTypography);
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink"); return document;
    }
    private void ClearRichState()
    { _formControls.Clear(); _formFeedbackHosts.Clear(); _formBlockWidths.Clear(); _formCellCandidates.Clear(); _formCellColumns.Clear(); _branchLinkBlocks.Clear(); _branchLineModes.Clear(); _equations.Clear(); _annotationPointers.Clear(); _clipboardMath.Clear(); _formulaWidths.Clear(); _revealSections.Clear(); _bookmarkSections.Clear(); _foldBodies.Clear(); _foldHints.Clear(); InvalidateBranchText(); _bookmarkHeadings.Clear(); _bookmarkFormulas.Clear(); _headingMarkers.Clear(); ResetHeadingNumbers(); ClearMedia(); ClearCodeSurfaces(); }
    private void CodeFallback(FlowDocument document, string source)
    {
        ClearRichState(); document.Blocks.Clear(); document.Blocks.Add(CodeParagraph(source));
        TrimAnswerEnd(document);
    }
    private void TrimAnswerEnd(FlowDocument document)
    {
        if (ChatMode && document.Blocks.LastBlock is { } last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }
    private async Task RenderDeferredAsync(string source, int version)
    {
        var document = NewDocument();
        try
        {
            var prepared = await Task.Run(() =>
            {
                var math = new ChatMath(); var text = math.Prepare(source, Pipeline);
                var blocks = ChatMarkdown.Parse(text, Pipeline, out var repaired);
                return (Math: math, Blocks: blocks, Repaired: repaired);
            });
            if (version != _renderVersion) return;
            ClearRichState(); _math = prepared.Math; _restoringSpacedEmphasis = prepared.Repaired;
            var slice = Stopwatch.StartNew();
            foreach (var block in prepared.Blocks)
            {
                if (version != _renderVersion) return;
                AddBlock(document.Blocks, block, 0);
                if (slice.ElapsedMilliseconds < UiRenderSliceMilliseconds) continue;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                slice.Restart();
            }
            if (version != _renderVersion) return;
            TrimAnswerEnd(document); ApplyFolding(document);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (version != _renderVersion) return; CodeFallback(document, source); }
        if (version != _renderVersion) return;
        if (_tableViews.Count == 0) _math.ReleaseOriginalSource();
        NotifyAsyncLayoutChanging();
        _renderPending = false; _annotationPointers.Clear(); LayoutVersion++;
        Document = document; InvalidateMeasure();
    }

    private void AddBlocks(BlockCollection target, ContainerBlock source, int depth)
    {
        // Bound nesting independently of model output, including malformed streaming fragments.
        if (depth > 32) return;
        foreach (var block in source) AddBlock(target, block, depth);
    }
    private void AddBlock(BlockCollection target, Markdig.Syntax.Block block, int depth)
    {
        switch (block)
        {
            case Markdig.Extensions.CustomContainers.CustomContainer columns when columns.Info == "columns":
                AddColumns(target, columns, depth); break;
            case HeadingBlock heading:
                var h = Paragraph(heading.Inline, depth);
                h.Tag = new HeadingInfo(heading.Level, heading.Span.Start);
                _bookmarkHeadings.TryAdd(heading.Span.Start, h);
                if (ChatMode) AddHeadingNumber(h, heading.Level);
                else
                {
                    h.FontSize = heading.Level switch { 1 => 24, 2 => 21, 3 => 18, _ => 16 };
                    h.FontFamily = UseSystemFont ? FontFamily : new FontFamily("Source Han Sans SC, Microsoft YaHei");
                    h.FontWeight = FontWeights.SemiBold; h.Margin = new Thickness(0, 16, 0, 10);
                }
                target.Add(h); break;
            case ParagraphBlock paragraph:
                if (paragraph.Inline?.FirstChild is LiteralInline literal && _math.Tokens.TryGetValue(literal.Content.ToString().Trim(), out var math) && math.Display)
                    target.Add(DisplayFormula(math));
                else target.Add(Paragraph(paragraph.Inline, depth)); break;
            case CodeBlock code:
                target.Add(CodeParagraph(code.Lines.ToString())); break;
            case QuoteBlock quote:
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 2, 0, 2), Margin = new Thickness(0, 6, 0, 14) };
                section.SetResourceReference(Block.BorderBrushProperty, "Accent");
                AddBlocks(section.Blocks, quote, depth + 1); target.Add(section); break;
            case ListBlock list:
                var items = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(ChatMode ? Math.Max(24, FontSize * 1.5) : 24, 0, 0, 0), Margin = ChatMode ? new Thickness(0, 0, 0, ChatParagraphSpacing) : new Thickness(0, 4, 0, 12) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) items.StartIndex = start;
                if (ChatMode && list.IsOrdered)
                {
                    var digits = ((long)items.StartIndex + Math.Max(0, list.Count - 1)).ToString().Length;
                    items.Padding = new Thickness(items.Padding.Left + FontSize * .5 * (digits - 1), 0, 0, 0);
                }
                var itemNumber = items.StartIndex;
                foreach (var child in list.OfType<ListItemBlock>())
                { var item = new ListItem(); AddBlocks(item.Blocks, child, depth + 1);
                    if (ChatMode && item.Blocks.LastBlock is { } lastItemBlock)
                    {
                        lastItemBlock.Margin = new Thickness(lastItemBlock.Margin.Left, lastItemBlock.Margin.Top, lastItemBlock.Margin.Right, 0);
                    }
                    if (LinkListNumbers && list.IsOrdered && item.Blocks.FirstBlock is Paragraph paragraph && paragraph.Inlines.FirstInline is Hyperlink link)
                    { items.MarkerStyle = TextMarkerStyle.None; if (link.Name == "SnapshotReference") items.Padding = new Thickness(0); link.Inlines.InsertBefore(link.Inlines.FirstInline, new Run(itemNumber + ". ")); }
                    itemNumber++; items.ListItems.Add(item); }
                target.Add(items); break;
            case MdTable table:
                if (ChatMode) { AddScrollableTable(target, table, depth); break; }
                var result = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 16) };
                var rows = new TableRowGroup(); result.RowGroups.Add(rows);
                foreach (var row in table.OfType<MdRow>())
                {
                    var r = new TableRow(); rows.Rows.Add(r);
                    foreach (var cell in row.OfType<MdCell>())
                    {
                        var c = new TableCell { Padding = new Thickness(9, 6, 9, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                        c.SetResourceReference(TableCell.BorderBrushProperty, "Guide");
                        if (row.IsHeader)
                        {
                            c.FontWeight = ChatMode || UseChatEmphasis || _tableOwner is { } owner && (owner.ChatMode || owner.UseChatEmphasis) ? FontWeights.Normal : FontWeights.SemiBold;
                            c.SetResourceReference(TextElement.BackgroundProperty, "PanelBackground");
                        }
                        AddBlocks(c.Blocks, cell, depth + 1); r.Cells.Add(c);
                    }
                }
                target.Add(result); break;
            case ThematicBreakBlock:
                var line = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 12, 0, 16) };
                line.SetResourceReference(Block.BorderBrushProperty, "Guide"); target.Add(line); break;
            case ContainerBlock container:
                AddBlocks(target, container, depth + 1); break;
            case LeafBlock leaf:
                target.Add(Paragraph(leaf.Inline, depth)); break;
        }
    }

    private Paragraph Paragraph(ContainerInline? inline, int depth)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, ChatParagraphSpacing) };
        if (ChatMode) paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var formCount = _formControls.Count;
        var formulaCount = _clipboardMath.Count;
        if (inline is not null) AddInlines(paragraph.Inlines, inline, depth + 1);
        if (UsesChatTypography && _clipboardMath.Count != formulaCount) paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        if (_formControls.Count != formCount) { paragraph.LineHeight = double.NaN; paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight; }
        return paragraph;
    }

    private void AddInlines(InlineCollection target, ContainerInline source, int depth)
    {
        if (depth > 32) return;
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    _standaloneFormLine = IsStandaloneFormLine(source, literal);
                    try { AddFormText(target, ChatMarkdown.RestoreSpaces(literal.Content.ToString(), _restoringSpacedEmphasis)); }
                    finally { _standaloneFormLine = false; }
                    break;
                case CodeInline code:
                    target.Add(CodeSpan(code.Content)); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        if (UsesChatTypography) ApplyChatEmphasis(span);
                        else span.FontWeight = FontWeights.Bold;
                    }
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis, depth + 1); target.Add(span);
                    break;
                case LinkInline link:
                    if (link.IsImage && TryImageUri(link.Url, out var imageUri))
                    {
                        target.Add(ImageInline(imageUri, ImageAltText(link)));
                    }
                    else if (!link.IsImage && link.Url?.StartsWith('#') == true)
                    {
                        var key = Uri.UnescapeDataString(link.Url[1..]);
                        var local = EquationLink(key, null);
                        if (!TryRenderSnapshotLink(local, link, key)) AddInlines(local.Inlines, link, depth + 1);
                        RestoreLinkEmphasisColor(local.Inlines);
                        target.Add(local);
                    }
                    else if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    {
                        var hyperlink = Link(uri); AddInlines(hyperlink.Inlines, link, depth + 1);
                        RestoreLinkEmphasisColor(hyperlink.Inlines);
                        target.Add(hyperlink);
                    }
                    else { if (link.IsImage) target.Add(new Run("[图片] ")); AddInlines(target, link, depth + 1); }
                    break;
                case AutolinkInline auto:
                    if (Uri.TryCreate(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" or "mailto")
                    { var hyperlink = Link(address); hyperlink.Inlines.Add(new Run(auto.Url)); target.Add(hyperlink); }
                    else target.Add(new Run(auto.Url));
                    break;
                case ContainerInline container: AddInlines(target, container, depth + 1); break;
            }
        }
    }

    private static Hyperlink Link(Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { link.ToolTip = "无法打开链接，可复制地址后打开。"; }
            e.Handled = true;
        };
        return link;
    }
}
