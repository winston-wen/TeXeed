using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TeXeed.App.Controls;

/// <summary>One stable footer for live and persisted replies; only dot opacity animates.</summary>
public sealed class AiReplyStatus : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("", (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty IsThinkingProperty = DependencyProperty.Register(nameof(IsThinking), typeof(bool), typeof(AiReplyStatus), new PropertyMetadata(false, (d, _) => ((AiReplyStatus)d).Refresh()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsThinking { get => (bool)GetValue(IsThinkingProperty); set => SetValue(IsThinkingProperty, value); }
    private readonly TextBlock _stamp = new() { FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = AiChatTypography.FontSize, TextWrapping = TextWrapping.Wrap };
    private readonly Ellipse _indicator = new() { Width = 7, Height = 7, Margin = new Thickness(7, 0, 0, 0), IsHitTestVisible = false };
    private readonly InlineUIContainer _indicatorInline;
    private bool _pulsing;
    public AiReplyStatus()
    {
        Background = Brushes.Transparent;
        _indicatorInline = new InlineUIContainer(_indicator) { BaselineAlignment = BaselineAlignment.Center };
        _stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking");
        var line = new AiWaveRule { Height = 4, Margin = new Thickness(0, 7, 0, 6), IsHitTestVisible = false };
        line.SetResourceReference(AiWaveRule.StrokeProperty, "ChatDivider");
        Children.Add(_stamp); Children.Add(line);
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
        IsVisibleChanged += (_, _) => UpdatePulse();
    }
    private void Refresh()
    {
        _stamp.Inlines.Clear();
        if (IsThinking)
        {
            _stamp.Inlines.Add(new Run("思考中"));
            _stamp.Inlines.Add(_indicatorInline);
        }
        else _stamp.Text = Text;
        System.Windows.Automation.AutomationProperties.SetName(this, IsThinking ? "思考中" : Text);
        UpdatePulse();
    }
    private void UpdatePulse()
    {
        if (!IsThinking || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) { StopPulse(); return; }
        if (_pulsing) return;
        _pulsing = true;
        _indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromSeconds(.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void StopPulse()
    {
        _indicator.BeginAnimation(OpacityProperty, null); _indicator.Opacity = 1; _pulsing = false;
    }
}

// One frozen wave tile per theme brush; resizing only fills a rectangle.
public sealed class AiWaveRule : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(AiWaveRule), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private Brush? _stroke;
    private DrawingBrush? _tile;
    protected override void OnRender(DrawingContext dc)
    {
        if (Stroke is null) return;
        if (_tile is null || !ReferenceEquals(_stroke, Stroke))
        {
            _stroke = Stroke;
            var geometry = Geometry.Parse("M0,2 C2,0 4,0 6,2 C8,4 10,4 12,2"); geometry.Freeze();
            _tile = new DrawingBrush(new GeometryDrawing(null, new Pen(Stroke, 1), geometry)) {
                TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 12, 4), Viewbox = new Rect(0, 0, 12, 4), Stretch = Stretch.Fill };
            if (_tile.CanFreeze) _tile.Freeze();
        }
        dc.DrawRectangle(_tile, null, new Rect(RenderSize));
    }
}
