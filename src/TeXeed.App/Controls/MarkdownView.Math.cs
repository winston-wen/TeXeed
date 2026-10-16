using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Extensions.CustomContainers;
using Markdig.Syntax;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private int _columnCount = 1;
    private ChatMath _math = new();
    private readonly Dictionary<string, FrameworkContentElement> _equations = [];
    private readonly Dictionary<string, TextPointer?> _annotationPointers = [];
    internal TextPointer? AnnotationPointer(string id, TeXeed.App.AI.AiAnswerAnchor anchor)
    {
        if (_annotationPointers.TryGetValue(id, out var cached)) return cached;
        var offset = anchor.TextOffset;
        TextPointer? result = null;
        if (offset < 0) result = Document.ContentStart.GetPositionAtOffset(anchor.Offset);
        else for (var p = Document.ContentStart; p is not null && p.CompareTo(Document.ContentEnd) < 0;)
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Tag: string source } ui)
            {
                if (offset <= source.Length) { result = p; break; }
                offset -= source.Length; p = ui.ElementEnd; continue;
            }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = p.GetTextInRun(LogicalDirection.Forward);
                if (offset < text.Length) { result = p.GetPositionAtOffset(offset); break; }
                offset -= text.Length; p = p.GetPositionAtOffset(text.Length); continue;
            }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
            { if (offset < Environment.NewLine.Length) { result = p; break; } offset -= Environment.NewLine.Length; }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        _annotationPointers[id] = result;
        return result;
    }
    private void AddMathText(InlineCollection target, string text)
    {
        var offset = 0;
        foreach (Match match in Regex.Matches(text, Regex.Escape(_math.Prefix) + @"\d+END"))
        {
            target.Add(new Run(text[offset..match.Index]));
            var token = _math.Tokens[match.Value];
            if (token.Reference is { } reference)
            {
                var number = _math.Labels.GetValueOrDefault(reference, reference + " ?");
                target.Add(EquationLink(reference, token.Parenthesize ? $"({number})" : number));
            }
            else target.Add(FormulaInline(token));
            offset = match.Index + match.Length;
        }
        target.Add(new Run(text[offset..]));
    }
    private Inline FormulaInline(ChatMath.Token token)
    {
        try
        {
            var formula = new ChatFormula(token.Source, token.Display);
            return new InlineUIContainer(formula) { BaselineAlignment = BaselineAlignment.Center, Tag = "$" + token.Source + "$" };
        }
        catch (ArgumentException)
        {
            return new Run((token.Display ? "" : "$ ") + token.Source + (token.Display ? "" : " $"))
                { FontFamily = new FontFamily("Cambria Math, Consolas"), ToolTip = "暂不支持的公式语法，保留源文" };
        }
    }
    private Paragraph DisplayFormula(ChatMath.Token token)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 12, 0, 18), Padding = new Thickness(8), LineHeight = double.NaN };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement formula;
        try { formula = new ChatFormula(token.Source, true); }
        catch (ArgumentException) { formula = new TextBlock { Text = token.Source, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, ToolTip = "暂不支持的公式语法，保留源文" }; }
        var scroller = new ScrollViewer { Content = formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6), HorizontalContentAlignment = HorizontalAlignment.Center };
        grid.Children.Add(scroller);
        var number = new TextBlock { Text = $"({token.Number})", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontSize = 13 };
        number.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(number, 1); grid.Children.Add(number);
        grid.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidth(_columnCount) });
        paragraph.Inlines.Add(new InlineUIContainer(grid) { Tag = "$$" + token.Source + (token.Label is null ? "" : @"\label{" + token.Label + "}") + @"\tag{" + token.Number + "}$$" });
        _equations.TryAdd(token.Label ?? "eq-" + token.Number, paragraph);
        _equations.TryAdd("eq-" + token.Number, paragraph);
        return paragraph;
    }
    private Hyperlink EquationLink(string label, string? text)
    {
        var link = new Hyperlink { ToolTip = "跳转到公式 " + label };
        if (text is not null) link.Inlines.Add(new Run(text));
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        link.Click += (_, e) => { if (_equations.TryGetValue(label, out var target)) target.BringIntoView(); else link.ToolTip = "未找到公式：" + label; e.Handled = true; };
        return link;
    }
    private void AddColumns(BlockCollection target, CustomContainer source, int depth)
    {
        var columns = source.OfType<CustomContainer>().Where(c => c.Info == "column").ToArray();
        if (columns.Length < 2 || (ActualWidth > 0 && ActualWidth < 560)) { AddBlocks(target, source, depth + 1); return; }
        var table = new Table { CellSpacing = 12, Margin = new Thickness(0, 4, 0, 12) };
        var row = new TableRow(); var group = new TableRowGroup(); group.Rows.Add(row); table.RowGroups.Add(group);
        foreach (var column in columns)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new TableCell { Padding = new Thickness(10), BorderThickness = new Thickness(1) };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "Guide");
            var previous = _columnCount; _columnCount *= columns.Length;
            AddBlocks(cell.Blocks, column, depth + 1); _columnCount = previous; row.Cells.Add(cell);
        }
        target.Add(table);
    }
    internal string CopySource(TextPointer start, TextPointer end)
    {
        var output = new StringBuilder();
        for (var p = start; p is not null && p.CompareTo(end) < 0;)
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Tag: string source } ui)
            { output.Append(source); p = ui.ElementEnd; continue; }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = p.GetTextInRun(LogicalDirection.Forward); var length = Math.Min(text.Length, p.GetOffsetToPosition(end));
                output.Append(text.AsSpan(0, length)); p = p.GetPositionAtOffset(length); continue;
            }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph) output.AppendLine();
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return output.ToString();
    }
    private sealed class FormulaWidth(int columns) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Math.Max(80, ((double)value - 34) / columns - (columns > 1 ? 64 : 32));
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
