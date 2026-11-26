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
        _number = ChatEquationNumber.Caption(number); _indent = indent; Child = new ChatFormulaHost(formula, leftAligned); ClipToBounds = true;
        SetResourceReference(TextElement.ForegroundProperty, "Muted");
    }
    private FormattedText Label() => ChatEquationNumber.Format(this, _number);
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
        ChatEquationNumber.Draw(context, RenderSize, Label());
    }
}

// The transcript draws directly into its formula row. Only the popup needs a separate layout element.
internal sealed class ChatEquationNumber : FrameworkElement
{
    private static readonly Typeface Face = new(new FontFamily("Times New Roman"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly string _number;
    internal ChatEquationNumber(string number)
    {
        _number = Caption(number);
        SetResourceReference(TextElement.ForegroundProperty, "Muted");
    }
    internal static string Caption(string? number) => "(" + number + ")";
    internal static FormattedText Format(FrameworkElement element, string caption) => new(caption,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 13, (Brush)element.GetValue(TextElement.ForegroundProperty),
        VisualTreeHelper.GetDpi(element).PixelsPerDip);
    internal static void Draw(DrawingContext context, Size bounds, FormattedText label) =>
        context.DrawText(label, new Point(Math.Max(0, bounds.Width - label.Width), Math.Max(0, (bounds.Height - label.Height) / 2)));
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Format(this, _number);
        return new(label.Width, label.Height);
    }
    protected override void OnRender(DrawingContext context) => Draw(context, RenderSize, Format(this, _number));
}
