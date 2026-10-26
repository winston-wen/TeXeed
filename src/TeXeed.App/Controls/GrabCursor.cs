using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Arrow at rest; open hand after a hold, closed hand while dragging.</summary>
public static class GrabCursor
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GrabCursor), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(GrabCursor));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement owner) return;
        (owner.GetValue(StateProperty) as State)?.Detach();
        owner.SetValue(StateProperty, (bool)e.NewValue ? new State(owner) : null);
    }

    private sealed class State
    {
        private readonly FrameworkElement _owner;
        private readonly DispatcherTimer _hold = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(400) };
        private Point _start;
        private bool _pressed, _held, _dragging;
        public State(FrameworkElement owner)
        {
            _owner = owner;
            owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down), true);
            owner.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
            owner.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up), true);
            owner.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(Query), true);
            owner.LostMouseCapture += Lost; owner.Unloaded += Unloaded;
            _hold.Tick += Hold;
        }
        private Point Position(MouseEventArgs e) => e.GetPosition(Window.GetWindow(_owner) ?? (IInputElement)_owner);
        private void Down(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            Begin(Position(e));
        }
        private void Begin(Point point) { Cancel(); _start = point; _pressed = true; _hold.Start(); }
        private void Move(object sender, MouseEventArgs e) => MovePointer(Position(e), e.LeftButton == MouseButtonState.Pressed);
        private void MovePointer(Point point, bool pressed)
        {
            if (!_pressed) return;
            if (!pressed || !_owner.IsMouseCaptureWithin) { Cancel(); return; }
            if (!_dragging && (Math.Abs(point.X - _start.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(point.Y - _start.Y) >= SystemParameters.MinimumVerticalDragDistance))
            { _dragging = true; _hold.Stop(); Mouse.UpdateCursor(); }
        }
        private void Hold(object? sender, EventArgs e) => CompleteHold(Mouse.LeftButton == MouseButtonState.Pressed);
        private void CompleteHold(bool pressed)
        {
            _hold.Stop();
            if (!_pressed || !pressed || !_owner.IsMouseCaptureWithin) { Cancel(); return; }
            _held = true; Mouse.UpdateCursor();
        }
        private void Query(object sender, QueryCursorEventArgs e)
        {
            e.Cursor = _dragging ? DragCursors.Grabbing : _held ? DragCursors.Grab : Cursors.Arrow;
            e.Handled = true;
        }
        private void Up(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) Cancel(); }
        private void Lost(object sender, MouseEventArgs e) { if (!_owner.IsMouseCaptureWithin) Cancel(); }
        private void Unloaded(object sender, RoutedEventArgs e) => Cancel();
        private void Cancel()
        {
            _hold.Stop(); var changed = _held || _dragging;
            _pressed = _held = _dragging = false;
            if (changed) Mouse.UpdateCursor();
        }
        public void Detach()
        {
            Cancel();
            _owner.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down));
            _owner.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            _owner.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up));
            _owner.RemoveHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(Query));
            _owner.LostMouseCapture -= Lost; _owner.Unloaded -= Unloaded;
        }
    }
}
