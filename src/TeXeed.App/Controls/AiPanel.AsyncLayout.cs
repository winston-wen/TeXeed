using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private long _layoutAnchorVersion;
    private bool _layoutAnchorPending;
    private LayoutAnchor? _layoutAnchor;
    private sealed class LayoutAnchor(FrameworkElement owner, TextPointer? pointer, double y, double offset)
    {
        internal FrameworkElement Owner = owner;
        internal TextPointer? Pointer = pointer;
        internal double Y = y;
        internal double Offset = offset;
        internal Dictionary<(ScrollViewer Scroll, bool Horizontal), double>? NestedOffsets;
    }

    private void CancelLayoutAnchor()
    {
        _layoutAnchorVersion++;
        _layoutAnchorPending = false;
        _layoutAnchor = null;
    }

    private void TrackLayoutAnchorWheel(double oldOffset, double newOffset)
    {
        if (_layoutAnchor is not { } anchor) return;
        anchor.Y -= newOffset - oldOffset;
        anchor.Offset = newOffset;
    }

    private bool ScrollNestedWithLayoutAnchor(ScrollViewer scroll, int delta, bool horizontal)
    {
        if (_layoutAnchor is not { } anchor || !scroll.IsAncestorOf(anchor.Owner)) return ScrollWheel(scroll, delta, horizontal);
        var key = (scroll, horizontal);
        var pending = anchor.NestedOffsets is not null && anchor.NestedOffsets.TryGetValue(key, out var offset) ? offset : (double?)null;
        return ScrollWheel(scroll, delta, horizontal, (oldOffset, newOffset) =>
        {
            if (!ReferenceEquals(_layoutAnchor, anchor)) return;
            if (!horizontal) anchor.Y -= newOffset - oldOffset;
            (anchor.NestedOffsets ??= [])[key] = newOffset;
        }, pending);
    }

    private void AsyncLayoutChanging(object sender, RoutedEventArgs e)
    {
        if (_layoutAnchorPending || _disposed || _restoringReading || !MessagesScroll.IsVisible || MessagesScroll.ViewportHeight <= 0) return;
        if (_operation is not null && MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 40) return;
        if (e.OriginalSource is FrameworkElement changed && changed.IsLoaded && changed.TranslatePoint(new Point(), MessagesScroll).Y >= MessagesScroll.ViewportHeight) return;
        var center = new Point(MessagesScroll.ViewportWidth / 2, MessagesScroll.ViewportHeight / 2);
        var hit = MessagesScroll.InputHitTest(center) as DependencyObject;
        MarkdownView? text = null;
        FrameworkElement? nearby = null;
        for (var item = hit; item is not null && !ReferenceEquals(item, MessagesScroll); item = ScrollParent(item))
        {
            if (item is MarkdownView view) { text = view; break; }
            if (nearby is null && item is FrameworkElement element && element is not ScrollViewer && element.IsLoaded && element.IsVisible &&
                _turnViews.Values.Any(turn => turn.IsLoaded && turn.IsAncestorOf(element)))
                nearby = element;
        }
        if (text is null && nearby is null)
            text = _turnViews.Values.Where(view => view.IsLoaded && view.IsVisible)
                .SelectMany(view => view.Questions.Concat(view.Answers)).Where(view => view.IsLoaded && view.IsVisible)
                .OrderBy(view =>
                {
                    var top = view.TranslatePoint(new Point(), MessagesScroll).Y;
                    return center.Y < top ? top - center.Y : center.Y > top + view.ActualHeight ? center.Y - top - view.ActualHeight : 0;
                }).FirstOrDefault();
        FrameworkElement? owner = (FrameworkElement?)text ?? nearby;
        TextPointer? pointer = null;
        if (text is not null)
        {
            var position = MessagesScroll.TranslatePoint(center, text);
            position.X = Math.Clamp(position.X, 0, Math.Max(0, text.ActualWidth));
            position.Y = Math.Clamp(position.Y, 0, Math.Max(0, text.ActualHeight));
            pointer = text.GetPositionFromPoint(position, true);
        }
        owner ??= _turnViews.Values.FirstOrDefault(view => view.IsLoaded && view.IsVisible &&
            view.TranslatePoint(new Point(), MessagesScroll).Y <= center.Y &&
            view.TranslatePoint(new Point(0, view.ActualHeight), MessagesScroll).Y > center.Y);
        if (owner is null || !TryLayoutAnchorY(owner, pointer, out var originalY)) return;
        var originalOffset = MessagesScroll.VerticalOffset;
        var conversation = _book.Active;
        var version = ++_layoutAnchorVersion;
        var anchor = new LayoutAnchor(owner, pointer, originalY, originalOffset);
        _layoutAnchor = anchor;
        _layoutAnchorPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _layoutAnchorVersion) return;
            _layoutAnchorPending = false;
            _layoutAnchor = null;
            if (_disposed || _restoringReading || _book.Active != conversation || !owner.IsLoaded || !owner.IsVisible ||
                Math.Abs(MessagesScroll.VerticalOffset - anchor.Offset) > .5 || !TryLayoutAnchorY(anchor.Owner, anchor.Pointer, out var newY)) return;
            var difference = newY - anchor.Y;
            if (Math.Abs(difference) > .5)
                MessagesScroll.ScrollToVerticalOffset(Math.Clamp(anchor.Offset + difference, 0, MessagesScroll.ScrollableHeight));
        }));
    }

    private bool TryLayoutAnchorY(FrameworkElement owner, TextPointer? pointer, out double y)
    {
        y = 0;
        try
        {
            if (pointer is not null && owner is MarkdownView view && pointer.IsInSameDocument(view.Document.ContentStart))
            {
                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty) return false;
                y = owner.TranslatePoint(rect.TopLeft, MessagesScroll).Y;
            }
            else y = owner.TranslatePoint(new Point(), MessagesScroll).Y;
            return double.IsFinite(y);
        }
        catch (InvalidOperationException) { return false; }
    }
}
