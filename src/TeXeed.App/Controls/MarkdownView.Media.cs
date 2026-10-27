using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private sealed record TableViewport(BlockUIContainer Block, MarkdownView View, ScrollViewer Scroll, Table Table, double[] Columns, double MinimumWidth, string Text);
    private readonly List<TableViewport> _tableViews = [];
    private readonly List<MarkdownPicture> _pictures = [];
    private MarkdownView? _tableOwner;
    private MarkdownView? _selectionView;
    private bool _hasRelativeImages;
    internal Task MediaTask => Task.WhenAll(_pictures.Select(p => p.LoadTask).Concat(_tableViews.Select(t => t.View.MediaTask)));

    private void ClearMedia()
    {
        foreach (var picture in _pictures) picture.Cancel();
        _pictures.Clear(); _tableViews.Clear(); _selectionView = null; _hasRelativeImages = false;
    }
    private double MediaWidth => Math.Max(80, (double.IsFinite(_measuredWidth) ? _measuredWidth : 400) - Padding.Left - Padding.Right - 2 * AiChatTypography.RichTextInset);
    private void SizeMedia()
    {
        foreach (var table in _tableViews)
            SizeTable(table);
        foreach (var picture in _pictures) picture.Width = MediaWidth;
    }
    private void SizeTable(TableViewport table)
    {
        table.Scroll.Width = MediaWidth;
        var width = Math.Max(MediaWidth, table.MinimumWidth); table.View.Width = width + 2 * AiChatTypography.RichTextInset;
        for (var i = 0; i < table.Columns.Length; i++)
            table.Table.Columns[i].Width = new GridLength(table.Columns[i] * width / table.MinimumWidth);
    }
    private void AddScrollableTable(BlockCollection target, MdTable source, int depth)
    {
        var view = new MarkdownView { Padding = new Thickness(0), Margin = new Thickness(-AiChatTypography.RichTextInset, 0, -AiChatTypography.RichTextInset, 0), FontFamily = FontFamily, FontSize = FontSize,
            ImageBaseDirectory = ImageBaseDirectory, SourceLink = SourceLink, LocalLink = LocalLink, FormulaAsset = FormulaAsset, _math = _math, _tableOwner = this };
        var document = view.NewDocument(); view.AddBlock(document.Blocks, source, depth); view.Document = document;
        var table = (Table)document.Blocks.FirstBlock!; table.Margin = new Thickness(0);
        var widths = new double[table.RowGroups[0].Rows.Max(r => r.Cells.Count)];
        foreach (var row in table.RowGroups[0].Rows)
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var cell = row.Cells[i];
                var text = new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();
                var measured = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, cell.FontWeight, FontStretches.Normal), FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
                widths[i] = Math.Max(widths[i], Math.Clamp(measured + 18, 80, 360));
            }
        foreach (var width in widths) table.Columns.Add(new TableColumn { Width = new GridLength(width) });
        var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Width = MediaWidth };
        var block = new BlockUIContainer(scroll) { Margin = new Thickness(0, 6, 0, 16) };
        var viewport = new TableViewport(block, view, scroll, table, widths, widths.Sum(), view.CopySource(document.ContentStart, document.ContentEnd));
        _tableViews.Add(viewport); SizeTable(viewport);
        foreach (var (key, formula) in view._equations) _equations.TryAdd(key, formula);
        scroll.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0) LayoutVersion++; };
        target.Add(block);
    }
    private TableViewport? EmbeddedTable(TextPointer pointer) =>
        pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
            && pointer.GetAdjacentElement(LogicalDirection.Forward) is BlockUIContainer block
            ? _tableViews.FirstOrDefault(t => ReferenceEquals(t.Block, block)) : null;

    private MarkdownView SelectionView() => _selectionView ?? this;
    internal (int Offset, int Length, string Quote, int TextOffset) GetAnnotationSelection()
    {
        var view = SelectionView();
        var start = view.Selection.Start; var end = view.Selection.End;
        if (view.Selection.IsEmpty && start.Paragraph is { } paragraph) { start = paragraph.ContentStart; end = paragraph.ContentEnd; }
        var textOffset = view.CopySource(view.Document.ContentStart, start).Length;
        var offset = view.Document.ContentStart.GetOffsetToPosition(start);
        if (!ReferenceEquals(view, this))
        {
            var block = _tableViews.Single(t => ReferenceEquals(t.View, view)).Block;
            textOffset += CopySource(Document.ContentStart, block.ElementStart).Length;
            offset = Document.ContentStart.GetOffsetToPosition(block.ElementStart);
        }
        return (offset, start.GetOffsetToPosition(end), view.CopySource(start, end).Trim(), textOffset);
    }
    internal Point? AnnotationPoint(string id, AiAnswerAnchor anchor)
    {
        var pointer = AnnotationPointer(id, anchor);
        if (pointer is null || !pointer.HasValidLayout) return null;
        var point = pointer.GetCharacterRect(LogicalDirection.Forward).TopRight;
        foreach (var table in _tableViews)
            if (pointer.IsInSameDocument(table.View.Document.ContentStart)) return table.View.TranslatePoint(point, this);
        return point;
    }
    private bool TryImageUri(string? source, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (Uri.TryCreate(source, UriKind.Absolute, out var absolute))
        { if (absolute.Scheme is not ("file" or "http" or "https")) return false; uri = absolute; return true; }
        _hasRelativeImages = true;
        if (string.IsNullOrWhiteSpace(ImageBaseDirectory)) return false;
        try { uri = new Uri(Path.GetFullPath(Path.Combine(ImageBaseDirectory, Uri.UnescapeDataString(source)))); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
    private System.Windows.Documents.Inline ImageInline(Uri uri, string alt)
    {
        var picture = new MarkdownPicture(uri, alt, () => { LayoutVersion++; _annotationPointers.Clear(); }) { Width = MediaWidth };
        _pictures.Add(picture);
        return new InlineUIContainer(picture) { Tag = "[图片] " + alt };
    }
    private string ImageAltText(ContainerInline source)
    {
        string Text(Markdig.Syntax.Inlines.Inline inline) => inline switch
        {
            LiteralInline literal => Regex.Replace(literal.Content.ToString(), Regex.Escape(_math.Prefix) + @"\d+END", match =>
            {
                var token = _math.Tokens[match.Value];
                if (token.Reference is not { } reference) return "$" + token.Source + "$";
                var number = _math.Labels.GetValueOrDefault(reference, reference + " ?");
                return token.Parenthesize ? $"({number})" : number;
            }),
            CodeInline code => code.Content,
            AutolinkInline link => link.Url,
            ContainerInline container => string.Concat(container.Select(Text)),
            _ => ""
        };
        return string.Concat(source.Select(Text));
    }
}

internal sealed class MarkdownPicture : Border
{
    private static readonly HttpClient Client = new();
    private readonly Uri _uri;
    private readonly string _alt;
    private readonly Action _changed;
    private CancellationTokenSource? _loading;
    private bool _loaded;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;
    internal MarkdownPicture(Uri uri, string alt, Action changed)
    {
        _uri = uri; _alt = alt; _changed = changed;
        Child = Placeholder("正在读取图片");
        Loaded += async (_, _) =>
        {
            if (_loading is not null) await LoadTask;
            if (IsLoaded && !_loaded && _loading is null) await (LoadTask = LoadAsync());
        };
        Unloaded += (_, _) => Cancel();
    }
    private TextBlock Placeholder(string status) => new() { Text = $"[图片] {_alt} · {status}", TextWrapping = TextWrapping.Wrap, ToolTip = _uri.ToString() };
    internal void Cancel() => _loading?.Cancel();
    private async Task LoadAsync()
    {
        using var cancellation = new CancellationTokenSource(); _loading = cancellation;
        try
        {
            var bytes = _uri.IsFile ? await File.ReadAllBytesAsync(_uri.LocalPath, cancellation.Token)
                : await Client.GetByteArrayAsync(_uri, cancellation.Token);
            var bitmap = await Task.Run(() => ImagePreview.LoadBitmap(new MemoryStream(bytes)), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, ToolTip = _alt };
            ImagePreview.Enable(image, _alt);
            Child = image; _loaded = true; _changed();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { Child = Placeholder("无法读取此图片格式或地址"); _loaded = true; _changed(); }
        finally { _loading = null; }
    }
}
