using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Highlights the active scrollbar without changing its visibility or geometry.</summary>
public static class ScrollActivity
{
    public static readonly DependencyProperty HighlightOnScrollProperty = DependencyProperty.RegisterAttached(
        "HighlightOnScroll", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false, Changed));
    public static bool GetHighlightOnScroll(DependencyObject element) => (bool)element.GetValue(HighlightOnScrollProperty);
    public static void SetHighlightOnScroll(DependencyObject element, bool value) => element.SetValue(HighlightOnScrollProperty, value);

    private static readonly DependencyPropertyKey IsScrollingPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsScrolling", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false));
    public static readonly DependencyProperty IsScrollingProperty = IsScrollingPropertyKey.DependencyProperty;
    public static bool GetIsScrolling(DependencyObject element) => (bool)element.GetValue(IsScrollingProperty);

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
        private ScrollViewer? _viewer;

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
            for (DependencyObject? node = _bar; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is ScrollViewer viewer) { _viewer = viewer; break; }
            _viewer?.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled), true);
            _bar.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(ScrollbarScrolled), true);
        }

        private void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, _viewer)) return;
            if ((_bar.Orientation == Orientation.Vertical && e.VerticalChange != 0) ||
                (_bar.Orientation == Orientation.Horizontal && e.HorizontalChange != 0)) Activate();
        }

        private void ScrollbarScrolled(object sender, ScrollEventArgs e) => Activate();
        private void Activate()
        {
            if (!_bar.IsVisible || !_bar.IsEnabled) return;
            _bar.SetValue(IsScrollingPropertyKey, true);
            _idle.Stop(); _idle.Start();
        }

        private void Idle(object? sender, EventArgs e)
        {
            if (_bar.IsMouseCaptureWithin) return;
            _idle.Stop();
            _bar.SetValue(IsScrollingPropertyKey, false);
        }

        private void Detach()
        {
            _idle.Stop();
            _bar.SetValue(IsScrollingPropertyKey, false);
            _viewer?.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            _viewer = null;
            _bar.RemoveHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(ScrollbarScrolled));
        }

        public void Dispose()
        {
            Detach();
            _bar.Loaded -= Loaded; _bar.Unloaded -= Unloaded;
            _idle.Tick -= Idle;
            _bar.ClearValue(IsScrollingPropertyKey);
        }
    }
}
