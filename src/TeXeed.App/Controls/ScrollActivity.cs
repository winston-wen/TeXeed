using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Reveals scrollbars on local input without changing the viewport geometry.</summary>
public static class ScrollActivity
{
    public static readonly DependencyProperty IsScopeProperty = DependencyProperty.RegisterAttached(
        "IsScope", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false));
    public static bool GetIsScope(DependencyObject element) => (bool)element.GetValue(IsScopeProperty);
    public static void SetIsScope(DependencyObject element, bool value) => element.SetValue(IsScopeProperty, value);

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached(
        "AutoHide", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false, Changed));
    public static bool GetAutoHide(DependencyObject element) => (bool)element.GetValue(AutoHideProperty);
    public static void SetAutoHide(DependencyObject element, bool value) => element.SetValue(AutoHideProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ScrollActivity));

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollBar bar) return;
        if (bar.GetValue(StateProperty) is State old) old.Dispose();
        bar.SetValue(StateProperty, (bool)args.NewValue ? new State(bar) : null);
    }

    private sealed class State : IDisposable
    {
        private readonly ScrollBar _bar;
        private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly List<(RoutedEvent Event, Delegate Handler)> _handlers = [];
        private UIElement? _scope;
        private bool _shown;
        private Point? _lastMousePosition;

        public State(ScrollBar bar)
        {
            _bar = bar;
            _idle.Tick += Idle;
            bar.Loaded += Loaded;
            bar.Unloaded += Unloaded;
            if (bar.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs e) => Attach();
        private void Unloaded(object sender, RoutedEventArgs e) => Detach();
        private void Attach()
        {
            Detach();
            UIElement? fallback = null;
            for (DependencyObject? node = _bar; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (fallback == null && node is ScrollViewer viewer) fallback = viewer;
                if (node is UIElement scope && GetIsScope(node)) { _scope = scope; break; }
            }
            _scope ??= fallback;
            if (_scope == null) return;
            Listen(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(Input));
            Listen(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Input));
            Listen(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Input));
            Listen(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Input));
            Listen(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Input));
            Listen(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Input));
            Listen(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(Input));
            Listen(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            _bar.MouseEnter += Input;
            _bar.LostMouseCapture += Input;
            _bar.Opacity = 0;
            _bar.IsHitTestVisible = false;
        }

        private void Listen(RoutedEvent routedEvent, Delegate handler)
        {
            _scope!.AddHandler(routedEvent, handler, handledEventsToo: true);
            _handlers.Add((routedEvent, handler));
        }

        private void Input(object sender, RoutedEventArgs e)
        {
            if (e.RoutedEvent == Mouse.PreviewMouseMoveEvent && e is MouseEventArgs mouse)
            {
                // WPF also synthesizes MouseMove after hit-testing/layout changes.
                // Those notifications are not user activity and must not restart
                // the idle timeout (or hiding/revealing can keep itself alive).
                var position = mouse.GetPosition(Window.GetWindow(_bar));
                if (_lastMousePosition == position) return;
                _lastMousePosition = position;
            }
            Reveal();
        }
        private void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0 || e.VerticalChange != 0) Reveal();
        }

        private void Reveal()
        {
            if (!_bar.IsVisible) return;
            if (!_shown)
            {
                _shown = true;
                _bar.IsHitTestVisible = true;
                Fade(1, 100);
            }
            _idle.Stop(); _idle.Start();
        }

        private void Idle(object? sender, EventArgs e)
        {
            // Never fade a thumb or page-track held by the mouse, including when
            // dragging outside the pane. Hovering the bar itself also keeps it usable.
            if (_bar.IsMouseCaptureWithin || _bar.IsMouseOver) return;
            _idle.Stop(); _shown = false;
            _bar.IsHitTestVisible = false;
            Fade(0, 800);
        }

        private void Fade(double target, int milliseconds)
        {
            var from = _bar.Opacity;
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            _bar.Opacity = target;
            if (SystemParameters.ClientAreaAnimation)
                _bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, target,
                    TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop });
        }

        private void Detach()
        {
            _idle.Stop(); _shown = false; _lastMousePosition = null;
            if (_scope != null)
                foreach (var (routedEvent, handler) in _handlers) _scope.RemoveHandler(routedEvent, handler);
            _handlers.Clear(); _scope = null;
            _bar.MouseEnter -= Input;
            _bar.LostMouseCapture -= Input;
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
        }

        public void Dispose()
        {
            Detach();
            _bar.Loaded -= Loaded; _bar.Unloaded -= Unloaded;
            _idle.Tick -= Idle;
            _bar.ClearValue(UIElement.OpacityProperty);
            _bar.ClearValue(UIElement.IsHitTestVisibleProperty);
        }
    }
}
