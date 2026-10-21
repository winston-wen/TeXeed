using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Dictionary<InlineUIContainer, ChatMath.Token> _clipboardMath = [];
    private readonly List<(FrameworkElement Grid, int Columns)> _formulaWidths = [];
    private double _measuredWidth = double.NaN;
    private static double FormulaAvailableWidth(double width, int columns) => Math.Max(80, (width - 34) / columns - (columns > 1 ? 64 : 32));
    protected override Size MeasureOverride(Size availableSize)
    {
        // Size embedded formula rows BEFORE FlowDocument formats its paragraphs.
        // Binding every row to ActualWidth used to invalidate the whole document
        // after arrange, causing repeated bottomless formatting and scroll stalls.
        if (double.IsFinite(availableSize.Width) && availableSize.Width != _measuredWidth)
        {
            _measuredWidth = availableSize.Width;
            foreach (var (grid, columns) in _formulaWidths) grid.Width = FormulaAvailableWidth(_measuredWidth, columns);
        }
        var measured = base.MeasureOverride(availableSize);
        return _renderPending ? new Size(measured.Width, Math.Max(measured.Height, _estimatedHeight)) : measured;
    }
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
            target.Add(MathRemainder(text[offset..match.Index]));
            var token = _math.Tokens[match.Value];
            if (token.Reference is { } reference)
            {
                var number = _math.Labels.GetValueOrDefault(reference, reference + " ?");
                target.Add(EquationLink(reference, token.Parenthesize ? $"({number})" : number));
            }
            else target.Add(FormulaInline(token));
            offset = match.Index + match.Length;
        }
        target.Add(MathRemainder(text[offset..]));
    }
    private Run MathRemainder(string text)
    {
        var run = new Run(text);
        if (_math.HasUnclosedMath && (text.Contains('$') || text.Contains(@"\(") || text.Contains(@"\[")))
        { run.FontFamily = new FontFamily("Consolas"); run.FontSize = 14; run.SetResourceReference(TextElement.BackgroundProperty, "PanelBackground"); }
        return run;
    }
    private Inline FormulaInline(ChatMath.Token token)
    {
        var inline = new InlineUIContainer(new ChatFormula(token.Source, token.Display, () => LayoutVersion++, FormulaAsset?.Invoke(token.Source, token.Display)))
            { BaselineAlignment = BaselineAlignment.Baseline, Tag = "$" + token.Source + "$" };
        _clipboardMath.Add(inline, token);
        return inline;
    }
    private Paragraph DisplayFormula(ChatMath.Token token)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 12, 0, 18), Padding = new Thickness(8), LineHeight = double.NaN };
        var formula = new ChatFormula(token.Source, true, () => LayoutVersion++, FormulaAsset?.Invoke(token.Source, true));
        var grid = new ChatFormulaRow(formula, token.Number);
        _formulaWidths.Add((grid, _columnCount));
        grid.Width = FormulaAvailableWidth(double.IsFinite(_measuredWidth) ? _measuredWidth : 0, _columnCount);
        var inline = new InlineUIContainer(grid) { Tag = "$$" + token.Source + (token.Label is null ? "" : @"\label{" + token.Label + "}") + @"\tag{" + token.Number + "}$$" };
        paragraph.Inlines.Add(inline);
        _clipboardMath.Add(inline, token);
        _equations.TryAdd(token.Label ?? "eq-" + token.Number, paragraph);
        _equations.TryAdd("eq-" + token.Number, paragraph);
        return paragraph;
    }
    private Hyperlink EquationLink(string label, string? text)
    {
        var link = new Hyperlink { ToolTip = "跳转到公式 " + label };
        if (text is not null) link.Inlines.Add(new Run(text));
        link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
        link.Click += (_, e) => { if (LocalLink is not null && label.StartsWith("snapshot-", StringComparison.Ordinal)) LocalLink(label); else if (_equations.TryGetValue(label, out var target)) { Reveal(target); target.BringIntoView(); } else link.ToolTip = "未找到公式：" + label; e.Handled = true; };
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
}
