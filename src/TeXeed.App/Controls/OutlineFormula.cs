using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace TeXeed.App.Controls;

/// <summary>A cached vector formula, fitted to a single outline row.</summary>
public sealed class OutlineFormula : FrameworkElement
{
    private static readonly Dictionary<string, Geometry?> Cache = new(StringComparer.Ordinal);
    private readonly Geometry? _geometry;
    private readonly string _source;
    private readonly double _fontSize;
    public bool IsFormulaRendered => _geometry is not null;
    public const double MaximumHeight = 24;

    static OutlineFormula() => TextElement.ForegroundProperty.OverrideMetadata(typeof(OutlineFormula),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public OutlineFormula(string source, double fontSize)
    {
        _source = source; _fontSize = fontSize;
        IsHitTestVisible = false;
        ClipToBounds = true;
        if (!Cache.TryGetValue(source, out _geometry))
        {
            try
            {
                if (source.Length <= 4096)
                {
                    var formula = WpfTeXFormulaParser.Instance.Parse(source);
                    var geometry = formula.RenderToGeometry(WpfTeXEnvironment.Create(TexStyle.Text, 20, "Segoe UI"), scale: 20);
                    if (!geometry.Bounds.IsEmpty && geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0)
                    { geometry.Freeze(); _geometry = geometry; }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unknown macros and incomplete edits remain readable as source.
                // No TeX process or user-defined command runs in the outline.
            }
            if (Cache.Count >= 256) Cache.Clear();
            Cache[source] = _geometry;
        }
    }

    private double Scale => _geometry is null ? 1 : Math.Min(_fontSize / 20, MaximumHeight / _geometry.Bounds.Height);
    private FormattedText Fallback() => new(_source, System.Globalization.CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface("Cambria Math"), _fontSize,
        (Brush)GetValue(TextElement.ForegroundProperty), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize) => _geometry is { } geometry
        ? new Size(geometry.Bounds.Width * Scale + 4, geometry.Bounds.Height * Scale)
        : new Size(Fallback().WidthIncludingTrailingWhitespace + 4, Math.Min(MaximumHeight, Fallback().Height));

    protected override void OnRender(DrawingContext context)
    {
        if (_geometry is not { } geometry) { context.DrawText(Fallback(), new Point(2, 0)); return; }
        context.PushTransform(new TranslateTransform(2, 0));
        context.PushTransform(new ScaleTransform(Scale, Scale));
        context.PushTransform(new TranslateTransform(-geometry.Bounds.X, -geometry.Bounds.Y));
        context.DrawGeometry((Brush)GetValue(TextElement.ForegroundProperty), null, geometry);
        context.Pop(); context.Pop(); context.Pop();
    }
}
