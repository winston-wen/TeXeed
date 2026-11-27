using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>A persisted turn with transient original previews and activity disclosures.</summary>
public sealed class AiTurnView : StackPanel
{
    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(nameof(Turn), typeof(AiTurn), typeof(AiTurnView), new PropertyMetadata(null, (d, _) => ((AiTurnView)d).Build()));
    public AiTurn Turn { get => (AiTurn)GetValue(TurnProperty); set => SetValue(TurnProperty, value); }
    private int _turnNumber;
    public int TurnNumber { get => _turnNumber; set { if (_turnNumber == value) return; _turnNumber = value; _number.Text = value > 0 ? $"[{value}]" : ""; _ordinalAlignment?.Schedule(); foreach (var answer in Answers) answer.TurnNumber = value; } }
    private ChatOrdinalAlignment? _ordinalAlignment;
    private string? _imageBaseDirectory;
    public string? ImageBaseDirectory { get => _imageBaseDirectory; set { _imageBaseDirectory = value; foreach (var answer in Answers) answer.ImageBaseDirectory = value; } }
    internal Action? Changed;
    internal Action<FrameworkElement>? FoldRequested;
    internal Action<FrameworkElement>? FoldGroupRequested;
    internal FrameworkElement FoldTarget => Turn.Collapsed && _foldedRow is not null ? _foldedRow : _sentTime;
    internal Button? FoldGroupHint { get; private set; }
    internal Action<MarkdownView>? AnswerReady, AnswerGone, QuestionReady, QuestionGone;
    internal Action<MarkdownView>? AnswerFoldingChanged;
    internal Func<AiEntry, bool>? AnswerReferenceRequested;
    internal Action<AiEntry>? PreviewChanged;
    internal Action<string>? SourceRequested;
    internal Action? RetryRequested;
    internal Action? ConfirmInputRequested, EndInputRequested;
    private (bool Show, bool CanConfirm, bool CanEnd) _inputActions;
    internal void SetInputActions(bool show, bool canConfirm, bool canEnd)
    {
        _inputActions = (show, canConfirm, canEnd);
        _footer?.SetInputActions(show, canConfirm, canEnd);
    }
    private bool _canRetry;
    internal bool CanRetry { get => _canRetry; set { _canRetry = value; if (_footer is not null) _footer.CanRetry = value; } }
    internal List<MarkdownView> Answers { get; } = [];
    internal List<MarkdownView> Questions { get; } = [];
    internal Action<ContextMenu>? ConfigureRevisionMenu;
    private TurnRevisionEditor? _revisionEditor;
    internal void SetRevisionEditor(TurnRevisionEditor? editor)
    {
        if (ReferenceEquals(_revisionEditor, editor)) return;
        if (_revisionEditor is not null) Children.Remove(_revisionEditor);
        _revisionEditor = editor;
        _body.Visibility = editor is null ? Visibility.Visible : Visibility.Collapsed;
        if (editor is null || _footer is null) return;
        if (editor.Parent is AiTurnView previous) previous.SetRevisionEditor(null);
        editor.Margin = _body.Margin;
        Children.Insert(Children.IndexOf(_footer), editor);
    }

    private readonly StackPanel _body = new(), _preview = new();
    private readonly TextBlock _number = new() { FontFamily = ChatReadingTypography.Family, FontSize = AiChatTypography.FontSize,
        FontWeight = FontWeights.Normal, Margin = new Thickness(-40, 8, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _sentTime = new() { FontSize = AiChatTypography.MetadataFontSize, TextWrapping = TextWrapping.Wrap, LineHeight = AiChatTypography.ExpandedLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0), Focusable = true, Cursor = Cursors.Hand };
    private AiReplyStatus? _footer;
    private string? _previewId;
    private Grid? _foldedRow;
    private (int First, int Last) _foldLabel;
    private double? _foldRunGap;

    public AiTurnView()
    {
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _previewId is not null) { ClosePreview(); e.Handled = true; } };
        _sentTime.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _number.SetResourceReference(TextBlock.ForegroundProperty, "ChatHeading");
        EnableFold(_sentTime);
        System.Windows.Automation.AutomationProperties.SetHelpText(_sentTime, "双击收起本轮回答");
    }
    private void EnableFold(FrameworkElement target)
    {
        target.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(target); e.Handled = true; } };
        target.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(target); e.Handled = true; } };
    }
    internal void Toggle() => Toggle(_foldedRow?.IsVisible == true ? _foldedRow : this);
    private void Toggle(FrameworkElement target)
    {
        if (FoldRequested is { } toggle) { toggle(target); return; }
        Turn.Collapsed = !Turn.Collapsed; SetFoldRange(TurnNumber); Changed?.Invoke();
    }
    internal void SetFoldRange(int end, bool offerCompact = false)
    {
        if (Turn.Collapsed)
        {
            if (_foldedRow is null || _foldedRow.Visibility != Visibility.Visible)
            {
                ClosePreview();
                foreach (UIElement child in Children) child.Visibility = Visibility.Collapsed;
                if (_foldedRow is null) { _foldedRow = MakeFoldedRow(); Children.Add(_foldedRow); }
                _foldedRow.Visibility = Visibility.Visible;
            }
            var group = end > TurnNumber;
            if (_foldLabel != (TurnNumber, end) || end == 0)
            {
                _foldLabel = (TurnNumber, end);
                var label = end <= TurnNumber ? $"[{TurnNumber}]" : end == TurnNumber + 1 ? $"[{TurnNumber}, {end}]" : $"[{TurnNumber} ~ {end}]";
                ((TextBlock)_foldedRow.Children[0]).Text = label;
                var action = group ? "展开为逐条折叠" : "展开这一轮";
                System.Windows.Automation.AutomationProperties.SetName(_foldedRow, label + " 已折叠，" + action);
                _foldedRow.ToolTip = $"双击或按 Enter、空格{action}";
            }
            if (offerCompact || group)
            {
                FoldGroupHint ??= AddFoldGroupHint();
                var hint = group ? "展开" : "收纳";
                if (!Equals(FoldGroupHint.Content, hint))
                {
                    FoldGroupHint.Content = hint;
                    FoldGroupHint.ToolTip = group ? "双击展开为逐条折叠" : "双击收纳这段连续折叠";
                }
                FoldGroupHint.Visibility = Visibility.Visible;
            }
            else if (FoldGroupHint is not null) FoldGroupHint.Visibility = Visibility.Collapsed;
            return;
        }
        if (_footer is null) BuildExpanded();
        if (_foldedRow is { Visibility: Visibility.Visible })
        {
            foreach (UIElement child in Children)
                child.Visibility = child == _foldedRow || child == _preview || child == _body && _revisionEditor is not null ? Visibility.Collapsed : Visibility.Visible;
            _ordinalAlignment?.Schedule();
        }
        _foldLabel = default;
    }
    internal void SetFoldRunSpacing(bool consecutive)
    {
        if (consecutive == _foldRunGap.HasValue) return;
        var margin = Margin;
        var regularGap = _foldRunGap ?? margin.Bottom;
        _foldRunGap = consecutive ? regularGap : null;
        margin.Bottom = consecutive ? 0 : regularGap;
        Margin = margin;
    }
    private Grid MakeFoldedRow()
    {
        var row = new Grid { Background = Brushes.Transparent, Focusable = true, Cursor = Cursors.Hand,
            MinHeight = AiChatTypography.LineHeight, Margin = new Thickness(-12, 0, 4, 0), ToolTip = "双击或按 Enter、空格展开折叠轮次" };
        // A wide answer can leave the virtualized panel wider than the visible reader.
        // Keep the divider inside the visible reader.
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.SetBinding(WidthProperty, new Binding(nameof(ChatScrollViewer.ViewportWidth))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ChatScrollViewer), 1),
            Converter = FoldDividerWidth.Instance
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 12 + AiChatTypography.TextLeft });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new TextBlock { FontSize = AiChatTypography.FontSize, FontFamily = ChatReadingTypography.Family,
            FontWeight = FontWeights.Normal, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ChatHeading");
        var line = FoldLine();
        Grid.SetColumn(line, 1); row.Children.Add(label); row.Children.Add(line);
        EnableFold(row); return row;
    }
    private sealed class FoldDividerWidth : IValueConverter
    {
        internal static readonly FoldDividerWidth Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is double width && width > 4 ? width - 4 : double.NaN;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
    private static Border FoldLine()
    {
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "ChatDivider");
        return line;
    }
    private Button AddFoldGroupHint()
    {
        var row = _foldedRow!;
        row.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Insert(2, new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row.Children[1], 3);
        var lead = FoldLine();
        lead.Width = 4 * AiChatTypography.FontSize;
        Grid.SetColumn(lead, 1);
        row.Children.Add(lead);
        var hint = new Button { Tag = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0), FontSize = AiChatTypography.FoldHintFontSize, Cursor = Cursors.Hand };
        hint.SetResourceReference(Control.ForegroundProperty, "Muted");
        hint.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) FoldGroupRequested?.Invoke(hint);
        };
        hint.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            if (!e.IsRepeat) FoldGroupRequested?.Invoke(hint);
        };
        hint.Click += (_, _) => FoldGroupRequested?.Invoke(hint);
        Grid.SetColumn(hint, 2);
        row.Children.Add(hint);
        return hint;
    }
    private void Build()
    {
        SetFoldRunSpacing(false);
        _ordinalAlignment?.Dispose(); _ordinalAlignment = null;
        foreach (var old in Answers) AnswerGone?.Invoke(old);
        foreach (var old in Questions) QuestionGone?.Invoke(old);
        if (_number.Parent is Panel question) question.Children.Remove(_number);
        Answers.Clear(); Questions.Clear(); Children.Clear(); _body.Children.Clear(); _preview.Children.Clear(); _previewId = null;
        _revisionEditor = null; _body.Visibility = Visibility.Visible;
        _footer = null; _foldedRow = null; _foldLabel = default; FoldGroupHint = null;
        if (Turn is not null) SetFoldRange(TurnNumber);
    }
    private void BuildExpanded()
    {
        var entries = Turn.SentAnnotations;
        var prompt = entries.Count == 0 ? Turn.DisplayQuestion : Turn.Prompt ?? Turn.DisplayQuestion;
        var annotationsOnly = entries.Count > 0 && string.IsNullOrWhiteSpace(prompt);
        var legacyAnnotationText = string.Join("\n", entries.Select((e, i) => $"{i + 1}. [{LinkLabel(e)}](#snapshot-{i + 1})"));
        var annotationText = AiReferencePresentation.Markup(entries);
        void OpenSnapshot(string key)
        {
            if (!int.TryParse(key.AsSpan("snapshot-".Length), out var n) || n <= 0 || n > entries.Count) return;
            var entry = entries[n - 1];
            if (entry.AnswerAnchor is null || AnswerReferenceRequested?.Invoke(entry) != true) ShowPreview(entry, n);
        }
        var questionRow = new Grid(); questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AiChatTypography.TextLeft) }); questionRow.ColumnDefinitions.Add(new ColumnDefinition());
        var content = Rich(annotationsOnly ? annotationText : prompt, new Thickness(0, 8, 12, entries.Count == 0 || annotationsOnly ? -AiChatTypography.RichTextInset : 0), annotationsOnly, annotationsOnly ? entries : null, annotationsOnly ? legacyAnnotationText : null); Grid.SetColumn(content, 1);
        RegisterQuestion(content);
        if (annotationsOnly)
        {
            content.LocalLink = OpenSnapshot;
            if (content.Document.Blocks.FirstBlock is System.Windows.Documents.List list) { list.Padding = new Thickness(0); list.Margin = new Thickness(0); }
        }
        questionRow.Children.Add(_number); questionRow.Children.Add(content); Children.Add(questionRow);
        _ordinalAlignment = new ChatOrdinalAlignment(content, _number);
        if (entries.Count > 0 && !annotationsOnly)
        {
            var links = Rich(annotationText, new Thickness(AiChatTypography.TextLeft, 0, 12, -AiChatTypography.RichTextInset), true, entries, legacyAnnotationText);
            RegisterQuestion(links);
            links.LocalLink = OpenSnapshot;
            Children.Add(links);
        }
        _preview.Visibility = Visibility.Collapsed; _preview.Margin = new Thickness(AiChatTypography.TextLeft, 8, 12, 10); Children.Add(_preview);
        Children.Add(_sentTime);
        _body.Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0); Children.Add(_body);
        _sentTime.Text = Turn.SentTime;
        _footer = Footer(); Children.Add(_footer);
        BuildBody();
    }
    private void RegisterQuestion(MarkdownView view)
    {
        Questions.Add(view);
        view.Loaded += (_, _) => QuestionReady?.Invoke(view);
        view.Unloaded += (_, _) => QuestionGone?.Invoke(view);
    }
    private void BuildBody()
    {
        var segments = Turn.DisplaySegments;
        var remainingAnswers = segments.Count(segment => segment.Kind != "activity");
        var headings = new AiHeadingNumbers();
        foreach (var segment in segments)
        {
            if (segment.Kind == "activity")
            {
                _body.Children.Add(new AiActivityView { DataContext = segment });
            }
            else
            {
                if (segment.Kind == "final")
                {
                    var divider = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), IsHitTestVisible = false };
                    divider.SetResourceReference(Border.BackgroundProperty, "ChatDivider");
                    _body.Children.Add(divider);
                }
                var answer = Rich("", new Thickness(0, 2, 0, 0)); answer.Padding = new Thickness(0, 0, 34, 0); answer.DataContext = segment;
                answer.HeadingSeed = headings.Snapshot();
                answer.ChatMode = true; answer.TurnNumber = TurnNumber; answer.ImageBaseDirectory = ImageBaseDirectory;
                answer.ConfigureFolding(Turn.CollapsedSections, segment.Id, () => { Changed?.Invoke(); AnswerFoldingChanged?.Invoke(answer); });
                var gradedReply = AiFormGrading.IsApplied(Turn);
                if (gradedReply) answer.RevisionSourceOverride = segment.Text;
                var source = gradedReply ? AiFormGrading.DisplayText(segment.Text) : segment.Text;
                if (Turn.RevisionHistory?.LastOrDefault() is { } revision)
                {
                    answer.RevisionSourceOverride = segment.Text;
                    source = answer.SetRevisionSource(source, revision.Rows.Where(row => row.SegmentId == segment.Id));
                }
                answer.Markdown = source;
                answer.Loaded += (_, _) => AnswerReady?.Invoke(answer); answer.Unloaded += (_, _) => AnswerGone?.Invoke(answer);
                Answers.Add(answer); _body.Children.Add(answer);
                if (--remainingAnswers > 0)
                {
                    if (answer.RenderTask.IsCompletedSuccessfully) headings = new AiHeadingNumbers(answer.HeadingCounts);
                    else foreach (var fragment in MarkdownView.BookmarkFragments(segment.Text))
                        if (fragment.Kind == AiBookmarkKind.Heading && int.TryParse(fragment.Number.AsSpan(1), out var level)) headings.Next(level);
                }
            }
        }
    }
    internal static string LinkLabel(AiEntry entry) => AnnotationSummary(entry).Replace("[", "\\[").Replace("]", "\\]");
    internal static string AnnotationSummary(AiEntry entry)
    {
        if (!entry.HasReference && entry.HasAttachments)
            return string.Join("、", entry.AttachmentItems.Select(item => item.DisplayPath).Concat(entry.HasAttachmentImages ? ["粘贴的图片"] : Array.Empty<string>()));
        var text = string.IsNullOrWhiteSpace(entry.Question) ? entry.AnswerAnchor?.Quote ?? entry.Sources.FirstOrDefault()?.Text ?? entry.Title : entry.Question;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = string.Concat(text.EnumerateRunes().Take(80));
        return text;
    }
    private MarkdownView Rich(string source, Thickness margin, bool numbers = false, IReadOnlyList<AiEntry>? snapshots = null, string? revisionSource = null)
    {
        var view = new MarkdownView { SourceLink = value => SourceRequested?.Invoke(value), LinkListNumbers = numbers, UseChatEmphasis = true,
            SnapshotEntries = snapshots, RevisionSourceOverride = revisionSource,
            Margin = new Thickness(margin.Left - AiChatTypography.RichTextInset, margin.Top, margin.Right, margin.Bottom), Padding = new Thickness(0) };
        view.Markdown = source;
        return view;
    }
    internal void ShowPreview(AiEntry entry, int number)
    {
        if (_previewId == entry.Id) { ClosePreview(); return; }
        _previewId = entry.Id; _preview.Children.Clear();
        var header = new DockPanel(); var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), ToolTip = "关闭原文预览" }; DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => ClosePreview(); header.Children.Add(close); header.Children.Add(new TextBlock { Text = $"原文 · {number}", FontSize = 12 }); _preview.Children.Add(header);
        var contents = new StackPanel();
        if (entry.AnswerAnchor is { } quote) contents.Children.Add(Rich(quote.Quote, new Thickness(0)));
        foreach (var source in entry.Sources) { contents.Children.Add(new TextBlock { Text = source.File, FontSize = 12 }); contents.Children.Add(Rich(source.Text, new Thickness(0))); }
        foreach (var pdf in entry.PdfBlocks)
        {
            contents.Children.Add(new TextBlock { Text = $"PDF 第 {pdf.Page} 页 · 圈选区域快照", FontSize = 12 });
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(pdf.ImageBase64)); bitmap.EndInit(); bitmap.Freeze();
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 10) };
                ImagePreview.Enable(image, $"PDF 第 {pdf.Page} 页"); contents.Children.Add(image);
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException) { contents.Children.Add(new TextBlock { Text = "截图无法读取" }); }
        }
        _preview.Children.Add(new ScrollViewer { Content = contents, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _preview.Visibility = Visibility.Visible; PreviewChanged?.Invoke(entry);
    }
    internal void ClosePreview() { _preview.Visibility = Visibility.Collapsed; _preview.Children.Clear(); _previewId = null; }
    private AiReplyStatus Footer()
    {
        var toggle = new AiReplyStatus { Text = Turn.AnswerTime, Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0), Focusable = true, Cursor = Cursors.Hand };
        toggle.CanRetry = _canRetry; toggle.RetryRequested = () => RetryRequested?.Invoke();
        toggle.ConfirmRequested = () => ConfirmInputRequested?.Invoke();
        toggle.EndRequested = () => EndInputRequested?.Invoke();
        toggle.SetInputActions(_inputActions.Show, _inputActions.CanConfirm, _inputActions.CanEnd);
        toggle.ToolTip = "双击收起本轮，右键整理或查看编辑历史";
        void OpenMenu()
        {
            if (ConfigureRevisionMenu is null || !Turn.CanReviseAnswer) return;
            var menu = new ContextMenu { PlacementTarget = toggle }; ConfigureRevisionMenu(menu);
            menu.IsOpen = true;
        }
        toggle.MouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        toggle.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Apps && !(e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
            OpenMenu(); e.Handled = true;
        };
        EnableFold(toggle);
        return toggle;
    }
}
