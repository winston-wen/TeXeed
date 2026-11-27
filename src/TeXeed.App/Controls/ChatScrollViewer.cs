using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

public sealed class ChatScrollViewer : ScrollViewer
{
    private bool _revealingItem;
    private DispatcherOperation? _focusReveal;
    private long _wheelRemainder;
    private int _lastWheelDirection;
    internal Action? TextNavigationStarting { get; set; }

    internal void ResetWheelRemainder() { _wheelRemainder = 0; _lastWheelDirection = 0; }

    internal bool ScrollHistoryWheel(int delta, Action<double>? trackLayoutAnchor)
    {
        if (delta == 0 || ScrollInfo is not ChatVirtualizingPanel panel) return false;
        var direction = Math.Sign(delta);
        if (_lastWheelDirection == -direction && !panel.IsMeasureValid) panel.UpdateLayout();
        var offset = panel.VerticalOffset;
        var limit = Math.Max(0, panel.ExtentHeight - panel.ViewportHeight);
        // An earlier relative wheel command invalidates measure. The exposed
        // offset still describes the previous frame, including its old edge.
        if (limit <= 0 || panel.IsMeasureValid && (delta > 0 ? offset <= 0 : offset >= limit))
        {
            ResetWheelRemainder();
            return false;
        }
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) { ResetWheelRemainder(); return true; }
        if (_lastWheelDirection != direction) _wheelRemainder = 0;
        _lastWheelDirection = direction;
        var unitsPerNotch = Math.Max(1, lines);
        var units = Math.DivRem(_wheelRemainder + (long)delta * unitsPerNotch,
            Mouse.MouseWheelDeltaForOneLine, out _wheelRemainder);
        if (units == 0) return true;
        var step = lines < 0 ? panel.ViewportHeight : 16d;
        trackLayoutAnchor?.Invoke(-units * step);

        // Relative WPF commands retain the visible container as their anchor while the
        // panel revises its extent. Setting an absolute offset aborts that pending anchor.
        var notches = Math.DivRem(Math.Abs(units), unitsPerNotch, out var extraLines);
        for (long i = 0; i < notches; i++)
            if (units > 0) panel.MouseWheelUp(); else panel.MouseWheelDown();
        for (long i = 0; i < extraLines; i++)
            if (units > 0) panel.LineUp(); else panel.LineDown();
        return true;
    }

    internal void KeepContentPosition(double viewportMovement)
    {
        // Apply to the layout's current offset before rendering. A queued scroll command
        // could run after an explicit search result or the user's next wheel gesture.
        if (ScrollInfo is not { VerticalOffset: > 0, ViewportHeight: > 0 } info) return;
        var limit = Math.Max(0, info.ExtentHeight - info.ViewportHeight);
        info.SetVerticalOffset(Math.Clamp(info.VerticalOffset + viewportMovement, 0, limit));
    }

    static ChatScrollViewer()
    {
        EventManager.RegisterClassHandler(typeof(ChatScrollViewer), RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((sender, e) => ((ChatScrollViewer)sender).OnChildBringIntoView(e)));
    }

    private void OnChildBringIntoView(RequestBringIntoViewEventArgs e)
    {
        // Nested WPF scrollers can reissue a local caret or focus rectangle as a
        // request for the whole rich document. Only explicit item reveals use that route.
        if (_revealingItem && e.TargetObject is Visual target &&
            VisualTreeHelper.GetParent(target) is ChatVirtualizingPanel) return;
        e.Handled = true;
    }

    internal void RevealItem(Action reveal)
    {
        ResetWheelRemainder();
        CancelFocusReveal();
        var previous = _revealingItem;
        _revealingItem = true;
        try { reveal(); }
        finally { _revealingItem = previous; }
    }

    internal void CancelFocusReveal()
    {
        _focusReveal?.Abort();
        _focusReveal = null;
    }

    internal void QueueFocusReveal(IInputElement? expectedFocus = null)
    {
        CancelFocusReveal();
        _focusReveal = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _focusReveal = null;
            var focus = Keyboard.FocusedElement;
            if (!IsVisible || (expectedFocus is not null && !ReferenceEquals(expectedFocus, focus))) return;
            if (focus is TextElement text && text.Parent is not null)
            {
                var owner = TextOwner(text);
                if (owner is not null) RevealText(owner, text.ContentStart);
            }
            else if (focus is MarkdownView markdown)
                RevealText(markdown, markdown.CaretPosition);
            else if (focus is FrameworkElement element && IsAncestorOf(element))
            {
                RevealRectangle(element, () =>
                    element is TextBox { AcceptsReturn: true } box && box.ActualHeight > ViewportHeight
                        ? box.GetRectFromCharacterIndex(box.CaretIndex)
                        : new Rect(element.RenderSize));
            }
        }));
    }

    private static MarkdownView? TextOwner(TextElement text)
    {
        for (DependencyObject? node = text; node is FrameworkContentElement content; node = content.Parent)
            if (content.Parent is MarkdownView view) return view;
        return null;
    }

    internal void NavigateText(MarkdownView view, TextPointer pointer)
    {
        CancelFocusReveal();
        TextNavigationStarting?.Invoke();
        UpdateLayout();
        RevealText(view, pointer);
    }

    private void RevealText(MarkdownView view, TextPointer pointer)
    {
        if (!view.IsLoaded || !IsAncestorOf(view)) return;
        RevealRectangle(view, () =>
        {
            var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
            return rect.IsEmpty ? pointer.GetCharacterRect(LogicalDirection.Backward) : rect;
        });
    }

    private void RevealRectangle(FrameworkElement element, Func<Rect> target)
    {
        if (!element.IsLoaded || !element.IsVisible || ViewportHeight <= 0) return;
        var rect = target();
        if (rect.IsEmpty) return;
        for (var node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, this); node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer { ScrollableWidth: > 0 } local ||
                local.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var bounds = element.TransformToAncestor(local).TransformBounds(rect);
            var dx = RevealDelta(bounds.Left, bounds.Right, local.ViewportWidth);
            if (Math.Abs(dx) > .5)
                local.ScrollToHorizontalOffset(Math.Clamp(local.HorizontalOffset + dx, 0, local.ScrollableWidth));
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var position = element.TransformToAncestor(this).TransformBounds(rect);
            var dy = RevealDelta(position.Top, position.Bottom, ViewportHeight);
            var offset = Math.Clamp(VerticalOffset + dy, 0, ScrollableHeight);
            if (Math.Abs(offset - VerticalOffset) <= .5) return;
            ScrollToVerticalOffset(offset);
            // Measuring a virtualized turn can revise its estimated position.
            if (pass != 0) return;
            UpdateLayout();
            if (!element.IsLoaded || !element.IsVisible || !IsAncestorOf(element)) return;
            rect = target();
            if (rect.IsEmpty) return;
        }
    }

    private static double RevealDelta(double start, double end, double viewport)
    {
        if (end - start >= viewport && start <= 0 && end >= viewport) return 0;
        return start < 0 ? Math.Max(start, end - viewport) : end > viewport ? Math.Min(start, end - viewport) : 0;
    }
}
