using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Middle-button autoscroll for a single reading viewport.</summary>
internal sealed class AutoScrollController : IDisposable
{
    private const double DeadZone = 12, MaxSpeed = 1800;
    private static AutoScrollController? _active;
    private readonly ScrollViewer _scroll;
    private readonly Action? _firstMovement;
    private readonly KeyEventHandler _keyHandler;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private AutoScrollIndicator? _indicator;
    private AdornerLayer? _layer;
    private Window? _window;
    private Point _anchor, _pointer;
    private long _lastTick;
    private bool _initialMiddleDown, _dragged, _moved, _disposed;
    internal bool IsActive => _indicator is not null;
    internal Func<ScrollViewer, bool> CapturePointer { get; set; } = static viewer => Mouse.Capture(viewer, CaptureMode.SubTree);

    internal AutoScrollController(ScrollViewer scroll, Action? firstMovement = null)
    {
        _scroll = scroll; _firstMovement = firstMovement; _keyHandler = KeyDown;
        _scroll.PreviewMouseDown += MouseDown;
        _scroll.PreviewMouseUp += MouseUp;
        _scroll.PreviewMouseMove += MouseMove;
        _scroll.PreviewMouseWheel += MouseWheel;
        _scroll.Unloaded += Unloaded;
        _scroll.IsVisibleChanged += VisibilityChanged;
        _scroll.LostMouseCapture += LostMouseCapture;
        _scroll.ScrollChanged += ScrollChanged;
        _timer.Tick += TimerTick;
    }

    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsActive) { Stop(); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Middle || Keyboard.Modifiers != ModifierKeys.None || !CanStart(e.OriginalSource as DependencyObject)) return;
        if (!Start(e.GetPosition(_scroll))) return;
        _initialMiddleDown = true;
        e.Handled = true;
    }

    private bool CanStart(DependencyObject? source)
    {
        for (var item = source; item is not null && !ReferenceEquals(item, _scroll); item = Parent(item))
        {
            if (item is TextBoxBase { IsReadOnly: false } or PasswordBox or ComboBox or ButtonBase or ScrollBar or Hyperlink) return false;
            if (item is ScrollViewer nested && (nested.ScrollableWidth > .5 || nested.ScrollableHeight > .5)) return false;
        }
        return true;
    }

    private static DependencyObject? Parent(DependencyObject item) => item switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(item),
        FrameworkContentElement element => element.Parent,
        ContentElement element => ContentOperations.GetParent(element),
        _ => LogicalTreeHelper.GetParent(item)
    };

    internal bool Start(Point point)
    {
        if (_disposed || IsActive || !_scroll.IsVisible || (_scroll.ScrollableWidth <= .5 && _scroll.ScrollableHeight <= .5)) return false;
        var layer = AdornerLayer.GetAdornerLayer(_scroll);
        if (layer is null) return false;
        _active?.Stop();
        _anchor = _pointer = point; _dragged = _moved = _initialMiddleDown = false;
        _layer = layer;
        _indicator = new AutoScrollIndicator(_scroll, point);
        layer.Add(_indicator);
        _window = Window.GetWindow(_scroll);
        if (_window is not null) { _window.AddHandler(Keyboard.PreviewKeyDownEvent, _keyHandler, true); _window.Deactivated += Deactivated; }
        if (!CapturePointer(_scroll) || !IsActive) { Stop(); return false; }
        _active = this;
        return true;
    }

    private void MouseMove(object sender, MouseEventArgs e) { if (IsActive) UpdatePointer(e.GetPosition(_scroll)); }
    internal void UpdatePointer(Point point)
    {
        if (!IsActive) return;
        _pointer = point;
        if (_initialMiddleDown && (_pointer - _anchor).Length > DeadZone) _dragged = true;
        var moving = CanMove(_pointer.X - _anchor.X, _scroll.HorizontalOffset, _scroll.ScrollableWidth)
            || CanMove(_pointer.Y - _anchor.Y, _scroll.VerticalOffset, _scroll.ScrollableHeight);
        if (moving && !_timer.IsEnabled) { _lastTick = Stopwatch.GetTimestamp(); _timer.Start(); }
        else if (!moving) _timer.Stop();
    }

    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive || e.ChangedButton != MouseButton.Middle || !_initialMiddleDown) return;
        _initialMiddleDown = false;
        if (_dragged) Stop();
        e.Handled = true;
    }
    private void MouseWheel(object sender, MouseWheelEventArgs e) { if (IsActive) Stop(); }
    private void KeyDown(object sender, KeyEventArgs e) { Stop(); if (e.Key == Key.Escape) e.Handled = true; }
    internal static bool StopForWindow(Window window)
    {
        if (_active is not { } active || !ReferenceEquals(active._window, window)) return false;
        active.Stop(); return true;
    }
    private void Deactivated(object? sender, EventArgs e) => Stop();
    private void Unloaded(object sender, RoutedEventArgs e) => Stop();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!_scroll.IsVisible) Stop(); }
    private void LostMouseCapture(object sender, MouseEventArgs e) { if (IsActive && !ReferenceEquals(Mouse.Captured, _scroll)) Stop(); }
    private void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (IsActive && (e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0 || e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0))
        { _indicator?.InvalidateVisual(); UpdatePointer(_pointer); }
    }

    private void TimerTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        Advance(Stopwatch.GetElapsedTime(_lastTick, now));
        _lastTick = now;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (!IsActive) return;
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, .1);
        var x = Math.Clamp(_scroll.HorizontalOffset + Speed(_pointer.X - _anchor.X, _scroll.ScrollableWidth) * seconds, 0, _scroll.ScrollableWidth);
        var y = Math.Clamp(_scroll.VerticalOffset + Speed(_pointer.Y - _anchor.Y, _scroll.ScrollableHeight) * seconds, 0, _scroll.ScrollableHeight);
        if (Math.Abs(x - _scroll.HorizontalOffset) < .001 && Math.Abs(y - _scroll.VerticalOffset) < .001) { _timer.Stop(); return; }
        if (!_moved) { _moved = true; _firstMovement?.Invoke(); }
        if (Math.Abs(x - _scroll.HorizontalOffset) >= .001) _scroll.ScrollToHorizontalOffset(x);
        if (Math.Abs(y - _scroll.VerticalOffset) >= .001) _scroll.ScrollToVerticalOffset(y);
    }

    private static double Speed(double delta, double range) => range <= .5 || Math.Abs(delta) <= DeadZone ? 0
        : Math.Sign(delta) * Math.Min(MaxSpeed, (Math.Abs(delta) - DeadZone) * 4);
    private static bool CanMove(double delta, double offset, double range) => Speed(delta, range) is var speed
        && (speed < 0 && offset > .001 || speed > 0 && offset < range - .001);

    internal void Stop()
    {
        if (!IsActive) return;
        _timer.Stop();
        if (ReferenceEquals(_active, this)) _active = null;
        var indicator = _indicator; _indicator = null;
        _layer?.Remove(indicator); _layer = null;
        if (_window is not null) { _window.RemoveHandler(Keyboard.PreviewKeyDownEvent, _keyHandler); _window.Deactivated -= Deactivated; _window = null; }
        if (ReferenceEquals(Mouse.Captured, _scroll)) Mouse.Capture(null);
        _initialMiddleDown = _dragged = _moved = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _disposed = true;
        _scroll.PreviewMouseDown -= MouseDown; _scroll.PreviewMouseUp -= MouseUp;
        _scroll.PreviewMouseMove -= MouseMove; _scroll.PreviewMouseWheel -= MouseWheel;
        _scroll.Unloaded -= Unloaded; _scroll.IsVisibleChanged -= VisibilityChanged;
        _scroll.LostMouseCapture -= LostMouseCapture; _scroll.ScrollChanged -= ScrollChanged;
        _timer.Tick -= TimerTick;
    }

    private sealed class AutoScrollIndicator : Adorner
    {
        private static readonly Brush Background = MakeBrush(235, 219, 219, 219);
        private static readonly Brush Ink = MakeBrush(255, 51, 51, 51);
        private static readonly Pen Outline = MakePen(MakeBrush(150, 60, 60, 60), 1);
        private static readonly Pen Arrow = MakePen(Ink, 3);
        private readonly ScrollViewer _viewer;
        private readonly Point _point;
        internal AutoScrollIndicator(ScrollViewer viewer, Point point) : base(viewer)
        { _viewer = viewer; _point = point; IsHitTestVisible = false; }
        private Point DisplayPoint => new(ClampToViewport(_point.X, _viewer.ActualWidth), ClampToViewport(_point.Y, _viewer.ActualHeight));
        private static double ClampToViewport(double value, double size) => size < 49 ? size / 2 : Math.Clamp(value, 24.5, size - 24.5);
        protected override void OnRender(DrawingContext dc)
        {
            var center = DisplayPoint;
            dc.DrawEllipse(Background, Outline, center, 24, 24);
            dc.DrawEllipse(Ink, null, center, 2.6, 2.6);
            if (_viewer.ScrollableHeight > .5)
            {
                Chevron(dc, center, new Vector(0, -1)); Chevron(dc, center, new Vector(0, 1));
            }
            if (_viewer.ScrollableWidth > .5)
            {
                Chevron(dc, center, new Vector(-1, 0)); Chevron(dc, center, new Vector(1, 0));
            }
        }
        private static void Chevron(DrawingContext dc, Point center, Vector direction)
        {
            var tip = center + direction * 15; var basePoint = center + direction * 10;
            var perpendicular = new Vector(-direction.Y, direction.X) * 4;
            dc.DrawLine(Arrow, basePoint + perpendicular, tip);
            dc.DrawLine(Arrow, tip, basePoint - perpendicular);
        }
        private static Brush MakeBrush(byte a, byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
        private static Pen MakePen(Brush brush, double width) { var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    }
}
