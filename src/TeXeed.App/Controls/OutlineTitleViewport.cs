using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>A bounded rich title: formulas remain vector content clipped at the
/// right edge, while the expander stays outside this viewport. Holding either
/// mouse button temporarily moves the full title inside the same row.</summary>
public sealed class OutlineTitleViewport : FrameworkElement
{
    public static readonly DependencyProperty LatexProperty = DependencyProperty.Register(nameof(Latex), typeof(string), typeof(OutlineTitleViewport),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((OutlineTitleViewport)d)._title.Latex = (string)e.NewValue));
    public string Latex { get => (string)GetValue(LatexProperty); set => SetValue(LatexProperty, value); }
    public static readonly DependencyProperty ChildCountProperty = DependencyProperty.Register(nameof(ChildCount), typeof(int), typeof(OutlineTitleViewport), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int ChildCount { get => (int)GetValue(ChildCountProperty); set => SetValue(ChildCountProperty, value); }
    public static readonly DependencyProperty LeaderBrushProperty = DependencyProperty.Register(nameof(LeaderBrush), typeof(Brush), typeof(OutlineTitleViewport), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush LeaderBrush { get => (Brush)GetValue(LeaderBrushProperty); set => SetValue(LeaderBrushProperty, value); }
    private readonly LatexTitleBlock _title = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Canvas _clip;
    private readonly TextBlock _ellipsis = new() { Text = "…", IsHitTestVisible = false };
    private readonly VisualCollection _visuals;
    private readonly TranslateTransform _translate = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private long _pauseUntil, _lastTick;
    private int _direction = 1;
    private double _naturalWidth, _offset;
    public bool IsClipped => _naturalWidth > ActualWidth + .5;
    public bool IsMarqueeActive => _timer.IsEnabled;
    public double MarqueeOffset => _offset;

    public OutlineTitleViewport()
    {
        ClipToBounds = true;
        _title.RenderTransform = _translate;
        // Canvas arranges the child at its full desired width. A Border would
        // impose a layout clip on the title itself; translating that clip along
        // with the text leaves the trailing part permanently invisible.
        _clip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
        _clip.Children.Add(_title);
        _visuals = new VisualCollection(this) { _clip, _ellipsis };
        _timer.Tick += (_, _) => AdvanceMarquee();
        Unloaded += (_, _) => StopMarquee();
    }
    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];
    protected override Size MeasureOverride(Size availableSize)
    {
        _title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _naturalWidth = _title.DesiredSize.Width;
        _ellipsis.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(_naturalWidth, availableSize.Width) : _naturalWidth,
            Math.Min(28, Math.Max(_title.DesiredSize.Height, _ellipsis.DesiredSize.Height)));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var ellipsis = _naturalWidth > finalSize.Width + .5 && !IsMarqueeActive;
        var width = Math.Max(0, finalSize.Width - (ellipsis ? _ellipsis.DesiredSize.Width : 0));
        Canvas.SetTop(_title, (finalSize.Height - _title.DesiredSize.Height) / 2);
        _clip.Measure(new Size(width, finalSize.Height));
        _clip.Arrange(new Rect(0, 0, width, finalSize.Height));
        _ellipsis.Visibility = ellipsis ? Visibility.Visible : Visibility.Hidden;
        _ellipsis.Arrange(new Rect(width, 0, ellipsis ? _ellipsis.DesiredSize.Width : 0, finalSize.Height));
        InvalidateVisual();
        return finalSize;
    }
    protected override void OnRender(DrawingContext context)
    {
        if (ChildCount <= 0 || IsClipped || IsMarqueeActive || ActualWidth <= _naturalWidth + 8) return;
        var pen = new Pen(LeaderBrush, 1) { DashStyle = new DashStyle(new double[] { 1, 3 }, 0), DashCap = PenLineCap.Round };
        context.DrawLine(pen, new Point(_naturalWidth + 6, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
    }
    public void StartMarquee(int initialPauseMilliseconds = 400)
    {
        if (!IsClipped) return;
        _offset = 0; _translate.X = 0; _direction = 1;
        _lastTick = Environment.TickCount64; _pauseUntil = _lastTick + initialPauseMilliseconds;
        _timer.Start(); InvalidateArrange();
    }
    private void AdvanceMarquee()
    {
        var now = Environment.TickCount64;
        var seconds = Math.Min(.1, Math.Max(0, now - _lastTick) / 1000d); _lastTick = now;
        if (now < _pauseUntil) return;
        var max = Math.Max(0, _naturalWidth - ActualWidth);
        _offset = Math.Clamp(_offset + _direction * 45 * seconds, 0, max);
        _translate.X = -_offset;
        if (_offset == max || _offset == 0) { _direction = -_direction; _pauseUntil = now + 600; }
    }
    public void StopMarquee()
    {
        _timer.Stop(); _offset = 0; _translate.X = 0; InvalidateArrange();
    }
}
