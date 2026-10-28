using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TeXeed.App.Controls;

/// <summary>One lightweight math row, avoiding a Grid/TextBlock tree for every equation number.</summary>
internal sealed class ChatFormulaRow : Decorator
{
    private readonly string _number;
    private FormattedText? _label;
    private readonly double _indent;
    static ChatFormulaRow() => TextElement.ForegroundProperty.OverrideMetadata(typeof(ChatFormulaRow),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    internal ChatFormulaRow(ChatFormula formula, string? number, double indent = 0, bool leftAligned = false)
    {
        formula.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        _number = "(" + number + ")"; _indent = indent; Child = new ChatFormulaHost(formula, leftAligned); ClipToBounds = true;
        SetResourceReference(TextElement.ForegroundProperty, "Muted");
    }
    private FormattedText Label() => new(_number, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Times New Roman"), 13, (Brush)GetValue(TextElement.ForegroundProperty), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    protected override Size MeasureOverride(Size constraint)
    {
        _label = Label();
        var width = double.IsFinite(constraint.Width) ? constraint.Width : 600;
        Child.Measure(new Size(Math.Max(1, width - _label.Width - 12 - _indent), constraint.Height));
        return new Size(width, Math.Max(Child.DesiredSize.Height, _label.Height));
    }
    protected override Size ArrangeOverride(Size size)
    {
        var label = _label ?? Label();
        Child.Arrange(new Rect(_indent, Math.Max(0, (size.Height - Child.DesiredSize.Height) / 2), Math.Max(1, size.Width - label.Width - 12 - _indent), Child.DesiredSize.Height));
        return size;
    }
    protected override void OnRender(DrawingContext context)
    {
        var label = Label(); context.DrawText(label, new Point(Math.Max(0, ActualWidth - label.Width), Math.Max(0, (ActualHeight - label.Height) / 2)));
    }
}
