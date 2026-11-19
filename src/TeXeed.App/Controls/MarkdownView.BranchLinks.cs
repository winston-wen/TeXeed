using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private readonly Dictionary<string, InlineUIContainer> _branchLinkBlocks = [];
    private readonly Dictionary<Paragraph, object> _branchLineModes = [];
    private Dictionary<string, double>? _readingInkAxes;
    internal Dictionary<string, InlineUIContainer> BranchLinkBlocks => _branchLinkBlocks;
    internal IReadOnlyDictionary<string, InlineUIContainer> BranchLinkInlines => _branchLinkBlocks;

    internal bool AddBranchLink(string id, string title, AiAnswerAnchor anchor, string? before, string? after, Action open)
    {
        if (_branchLinkBlocks.TryGetValue(id, out var existing))
        {
            if (existing.Parent is not null && existing.Child is Button button) { SetBranchTitle(button, title); return true; }
            _branchLinkBlocks.Remove(id);
        }
        var start = ResolveBranchAnchor(anchor, before, after, reveal: false);
        if (start is null) return false;
        int startOffset;
        if (start.IsInSameDocument(Document.ContentStart)) startOffset = CopySourceLength(Document.ContentStart, start);
        else
        {
            var table = _tableViews.FirstOrDefault(t => start.IsInSameDocument(t.View.Document.ContentStart));
            if (table is null) return false;
            startOffset = CopySourceLength(Document.ContentStart, table.Block.ElementStart) + table.View.CopySourceLength(table.View.Document.ContentStart, start);
        }
        var end = BranchTextPointer(startOffset + anchor.Quote.Trim().Length);
        if (end?.Paragraph is null) return false;
        if (!start.IsInSameDocument(end)) return false;
        var selectionView = start.IsInSameDocument(Document.ContentStart) ? this : _tableViews.FirstOrDefault(table => start.IsInSameDocument(table.View.Document.ContentStart))?.View;
        if (selectionView is null || selectionView.CopySource(start, end).Trim() != anchor.Quote.Trim()) return false;
        // Repeated references to the same text are kept in their original order.
        foreach (var inline in _branchLinkBlocks.Values.Where(link => link.Parent is not null && link.ElementStart.IsInSameDocument(end) && link.ElementStart.Paragraph == end.Paragraph).OrderBy(link => link.ElementStart, TextPointerComparer.Instance))
            if (end.CompareTo(inline.ElementStart) <= 0 && CopySourceLength(end, inline.ElementStart) == 0) end = inline.ElementEnd;
        var owner = end.IsInSameDocument(Document.ContentStart) ? this : _tableViews.FirstOrDefault(table => end.IsInSameDocument(table.View.Document.ContentStart))?.View ?? this;
        var linkButton = new Button
        {
            FontSize = FontSize, FontFamily = FontFamily, FontWeight = FontWeights.Normal,
            FontStyle = FontStyles.Normal, Padding = new Thickness(0), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ContentTemplate = BranchLinkTemplate,
            Tag = owner.ReadingInkAxis(InkWordAt(end))
        };
        Typography.SetStylisticSet4(linkButton, true);
        SetBranchTitle(linkButton, title);
        linkButton.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = owner, Converter = BranchLinkWidthConverter.Instance });
        linkButton.SetResourceReference(Control.ForegroundProperty, "ChatFocus");
        linkButton.Click += (_, _) => open();
        InlineUIContainer container;
        NotifyAsyncLayoutChanging();
        try { container = new InlineUIContainer(linkButton, end) { BaselineAlignment = BaselineAlignment.Baseline }; }
        catch (ArgumentException) { return false; }
        linkButton.SizeChanged += (_, _) =>
        {
            if (container.Parent is null || container.ElementStart.Paragraph is not { } paragraph) return;
            container.BaselineAlignment = linkButton.ActualHeight > linkButton.FontSize * 1.5 ? BaselineAlignment.Top : BaselineAlignment.Baseline;
            UpdateBranchLineMode(paragraph);
        };
        _branchLinkBlocks[id] = container;
        BranchLinksChanged();
        return true;
    }

    internal bool RemoveBranchLink(string id)
    {
        if (!_branchLinkBlocks.Remove(id, out var inline)) return false;
        var lineParagraph = inline.ElementStart.Paragraph;
        switch (inline.Parent)
        {
            case Paragraph paragraph: paragraph.Inlines.Remove(inline); break;
            case Span span: span.Inlines.Remove(inline); break;
        }
        if (lineParagraph is not null) UpdateBranchLineMode(lineParagraph);
        BranchLinksChanged();
        return true;
    }

    private void BranchLinksChanged()
    {
        LayoutVersion++; _annotationPointers.Clear(); _markerVisibleTextVersion = -1;
    }

    private void UpdateBranchLineMode(Paragraph paragraph)
    {
        var hasWrappedLink = _branchLinkBlocks.Values.Any(link => link.Parent is not null && link.ElementStart.Paragraph == paragraph && link.Child is Button button && button.ActualHeight > button.FontSize * 1.5);
        if (hasWrappedLink)
        {
            if (!_branchLineModes.ContainsKey(paragraph)) _branchLineModes[paragraph] = paragraph.ReadLocalValue(Block.LineStackingStrategyProperty);
            if (paragraph.LineStackingStrategy != LineStackingStrategy.MaxHeight) paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        }
        else if (_branchLineModes.Remove(paragraph, out var original))
        {
            if (original == DependencyProperty.UnsetValue) paragraph.ClearValue(Block.LineStackingStrategyProperty);
            else paragraph.SetValue(Block.LineStackingStrategyProperty, original);
        }
    }

    private TextPointer? BranchTextPointer(int offset)
    {
        if (offset < 0) return null;
        for (var p = Document.ContentStart; p is not null && p.CompareTo(Document.ContentEnd) < 0;)
        {
            if (IsDecoration(p, out var paddingEnd)) { p = paddingEnd; continue; }
            if (EmbeddedTable(p) is { } table)
            {
                if (offset <= table.Text.Length) return table.View.BranchTextPointer(offset);
                offset -= table.Text.Length; p = table.Block.ElementEnd; continue;
            }
            var context = p.GetPointerContext(LogicalDirection.Forward);
            var element = context is TextPointerContext.ElementStart or TextPointerContext.ElementEnd
                ? p.GetAdjacentElement(LogicalDirection.Forward) : null;
            if (element is Span { Tag: ChatMath.Token })
            {
                if (offset == 0) return p;
                offset--; p = p.GetNextContextPosition(LogicalDirection.Forward); continue;
            }
            if (context == TextPointerContext.ElementStart && element is InlineUIContainer { Tag: string source } ui)
            {
                if (offset < source.Length) return null;
                offset -= source.Length;
                if (offset == 0) return ui.ElementEnd;
                p = ui.ElementEnd; continue;
            }
            if (context == TextPointerContext.Text)
            {
                var text = p.GetTextInRun(LogicalDirection.Forward);
                if (offset <= text.Length) return p.GetPositionAtOffset(offset);
                offset -= text.Length; p = p.GetPositionAtOffset(text.Length); continue;
            }
            if (context == TextPointerContext.ElementEnd && element is Paragraph)
            { if (offset < Environment.NewLine.Length) return p; offset -= Environment.NewLine.Length; }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return null;
    }

    private sealed class TextPointerComparer : IComparer<TextPointer>
    {
        internal static readonly TextPointerComparer Instance = new();
        public int Compare(TextPointer? x, TextPointer? y) => ReferenceEquals(x, y) ? 0 : x is null ? -1 : y is null ? 1 : x.CompareTo(y);
    }

    private static readonly DataTemplate BranchLinkTemplate = CreateBranchLinkTemplate();
    private static DataTemplate CreateBranchLinkTemplate()
    {
        var text = new FrameworkElementFactory(typeof(EmojiTextBlock));
        text.SetBinding(EmojiTextBlock.SourceTextProperty, new Binding());
        text.SetBinding(EmojiTextBlock.BubbleInkAxisProperty, new Binding(nameof(Tag))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.LineHeightProperty, double.NaN);
        text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight);
        return new DataTemplate { VisualTree = text };
    }
    private static IEnumerable<Inline> ReadingInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Span span)
            {
                if (ReferenceEquals(span.Tag, CodeSpanMarker)) continue;
                foreach (var child in ReadingInlines(span.Inlines)) yield return child;
            }
            else yield return inline;
    }

    private double ReadingInkAxis(string sample)
    {
        if (sample.Length == 0) sample = "中文";
        var axes = _readingInkAxes ??= new(StringComparer.Ordinal);
        if (axes.TryGetValue(sample, out var axis)) return axis;
        axis = InkAxis(sample, FontFamily, FontWeights.Normal, FontStyles.Normal, FontSize);
        if (axes.Count < 64) axes.Add(sample, axis);
        return axis;
    }

    private static string InkWordAt(TextPointer position)
    {
        if (position.Paragraph is not { } paragraph) return "";
        var preceding = "";
        foreach (var inline in ReadingInlines(paragraph.Inlines))
        {
            if (inline is not Run run || ReferenceEquals(run.Tag, HeadingNumberMarker)) continue;
            var sample = FirstTitleWord(run.Text);
            if (sample.Length == 0) continue;
            if (run.ContentStart.CompareTo(position) > 0) return preceding.Length > 0 ? preceding : sample;
            preceding = sample;
        }
        return preceding;
    }
    private static void SetBranchTitle(Button button, string title)
    {
        if (Equals(button.ToolTip, title)) return;
        var label = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.Black, 1);
        // Set the inline baseline before WPF lays out the owning paragraph.
        var iconBaseline = button.FontSize / 2 - (double)button.Tag;
        TextBlock.SetBaselineOffset(button, Math.Max(label.Baseline, iconBaseline));
        button.Content = "🗨️ " + title;
        button.ToolTip = title;
        System.Windows.Automation.AutomationProperties.SetName(button, "追问：" + title);
    }
    private sealed class BranchLinkWidthConverter : IValueConverter
    {
        internal static readonly BranchLinkWidthConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width && double.IsFinite(width) && width > 0 ? Math.Max(1, width - 20) : 320d;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
