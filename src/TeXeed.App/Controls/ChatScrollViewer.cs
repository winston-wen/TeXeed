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
    internal Action? TextNavigationStarting { get; set; }

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
