using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TeXeed.App.Controls;

/// <summary>Embeds actual LaTeX-generated SVG paths at the TeX baseline, without bitmap rasterization.</summary>
internal sealed class ChatFormula : FrameworkElement
{
    private ChatSvg? _svg;
    private readonly bool _display;
    private readonly Action? _layoutChanged;
    internal string Source { get; }
    internal string? Error { get; private set; }
    internal string? SvgPath => _svg?.Path;
    internal Task RenderTask { get; }
    static ChatFormula()
    {
        TextElement.ForegroundProperty.OverrideMetadata(typeof(ChatFormula),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        TextElement.FontSizeProperty.OverrideMetadata(typeof(ChatFormula),
            new FrameworkPropertyMetadata(15d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    }
    internal ChatFormula(string source, bool display, Action? layoutChanged = null)
    {
        Source = source; _display = display; _layoutChanged = layoutChanged;
        ToolTip = "LaTeX 排版中…\n" + source;
        System.Windows.Automation.AutomationProperties.SetName(this, source);
        RenderTask = LoadAsync();
    }
    private async Task LoadAsync()
    {
        try
        {
            _svg = await ChatTexRenderer.RenderAsync(Source, _display);
            ToolTip = Source;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            ToolTip = Error + "\n" + Source;
        }
        _layoutChanged?.Invoke(); InvalidateMeasure(); InvalidateVisual();
    }
    private double FontSize => (double)GetValue(TextElement.FontSizeProperty);
    private double Scale => FontSize * (_display ? 1.15 : 1) / (10 * 72d / 72.27);
    private FormattedText Fallback() => new(Error is null ? "⋯" : (_display ? "$$\n" + Source + "\n$$" : "$" + Source + "$"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Consolas"), FontSize, (Brush)GetValue(TextElement.ForegroundProperty), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    // WPF's default drawing hit test walks every glyph outline in the SVG. Pointer
    // movement must cost O(1), independently of a formula's path complexity.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hit) =>
        new Rect(RenderSize).Contains(hit.HitPoint) ? new PointHitTestResult(this, hit.HitPoint) : null;
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_svg is { } svg)
        {
            TextBlock.SetBaselineOffset(this, svg.Baseline * Scale + 1);
            return new(Math.Max(1, svg.Bounds.Width * Scale) + 2, Math.Max(1, svg.Bounds.Height * Scale) + 2);
        }
        var text = Fallback();
        TextBlock.SetBaselineOffset(this, text.Baseline + 1);
        return new(Math.Max(1, text.Width) + 2, text.Height + 2);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_svg is not { } svg) { if (Error is not null) drawing.DrawRectangle((Brush?)TryFindResource("PanelBackground") ?? Brushes.Transparent, null, new Rect(RenderSize)); drawing.DrawText(Fallback(), new Point(1, 1)); return; }
        drawing.PushTransform(new TranslateTransform(1 - svg.Bounds.X * Scale, 1 - svg.Bounds.Y * Scale));
        drawing.PushTransform(new ScaleTransform(Scale, Scale));
        drawing.DrawGeometry((Brush)GetValue(TextElement.ForegroundProperty), null, svg.Geometry);
        drawing.Pop(); drawing.Pop();
    }
}
