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
    internal Dictionary<string, InlineUIContainer> BranchLinkBlocks => _branchLinkBlocks;
    internal IReadOnlyDictionary<string, InlineUIContainer> BranchLinkInlines => _branchLinkBlocks;

    internal bool AddBranchLink(string id, string title, AiAnswerAnchor anchor, string? before, string? after, Action open)
    {
        if (_branchLinkBlocks.TryGetValue(id, out var existing))
        {
            if (existing.Parent is not null && existing.Child is Button button) { button.Content = "🗨️ " + title; button.ToolTip = title; System.Windows.Automation.AutomationProperties.SetName(button, "追问：" + title); return true; }
            _branchLinkBlocks.Remove(id);
        }
        var start = ResolveBranchAnchor(anchor, before, after, reveal: false);
        if (start is null) return false;
        int startOffset;
        if (start.IsInSameDocument(Document.ContentStart)) startOffset = CopySource(Document.ContentStart, start).Length;
        else
        {
            var table = _tableViews.FirstOrDefault(t => start.IsInSameDocument(t.View.Document.ContentStart));
            if (table is null) return false;
            startOffset = CopySource(Document.ContentStart, table.Block.ElementStart).Length + table.View.CopySource(table.View.Document.ContentStart, start).Length;
        }
        var end = BranchTextPointer(startOffset + anchor.Quote.Trim().Length);
        if (end?.Paragraph is null) return false;
        if (!start.IsInSameDocument(end)) return false;
        var selectionView = start.IsInSameDocument(Document.ContentStart) ? this : _tableViews.FirstOrDefault(table => start.IsInSameDocument(table.View.Document.ContentStart))?.View;
        if (selectionView is null || selectionView.CopySource(start, end).Trim() != anchor.Quote.Trim()) return false;
        // Repeated references to the same text are kept in their original order.
        foreach (var inline in _branchLinkBlocks.Values.Where(link => link.Parent is not null && link.ElementStart.IsInSameDocument(end) && link.ElementStart.Paragraph == end.Paragraph).OrderBy(link => link.ElementStart, TextPointerComparer.Instance))
            if (end.CompareTo(inline.ElementStart) <= 0 && CopySource(end, inline.ElementStart).Length == 0) end = inline.ElementEnd;
        var linkButton = new Button
        {
            Content = "🗨️ " + title, ToolTip = title, FontSize = FontSize, FontFamily = FontFamily, FontWeight = FontWeights.Normal,
            FontStyle = FontStyles.Normal, Padding = new Thickness(0), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ContentTemplate = BranchLinkTemplate
        };
        System.Windows.Automation.AutomationProperties.SetName(linkButton, "追问：" + title);
        var owner = end.IsInSameDocument(Document.ContentStart) ? this : _tableViews.FirstOrDefault(table => end.IsInSameDocument(table.View.Document.ContentStart))?.View ?? this;
        linkButton.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = owner, Converter = BranchLinkWidthConverter.Instance });
        linkButton.SetResourceReference(Control.ForegroundProperty, "ChatFocus");
        linkButton.Click += (_, _) => open();
        InlineUIContainer container;
        try { container = new InlineUIContainer(linkButton, end) { BaselineAlignment = BaselineAlignment.TextBottom }; }
        catch (ArgumentException) { return false; }
        linkButton.SizeChanged += (_, _) =>
        {
            if (container.Parent is null || container.ElementStart.Paragraph is not { } paragraph) return;
            container.BaselineAlignment = linkButton.ActualHeight > linkButton.FontSize * 1.5 ? BaselineAlignment.Top : BaselineAlignment.TextBottom;
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
        LayoutVersion++; _annotationPointers.Clear(); _markerVisibleTextVersion = -1; QueueWaveLayout();
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
            if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Tag: string source } ui)
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
            if (context == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
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
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.LineHeightProperty, double.NaN);
        text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight);
        return new DataTemplate { VisualTree = text };
    }
    private sealed class BranchLinkWidthConverter : IValueConverter
    {
        internal static readonly BranchLinkWidthConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width && double.IsFinite(width) && width > 0 ? Math.Max(1, width - 20) : 320d;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
