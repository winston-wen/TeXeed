using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private void MessagesScroll_Wheel(object sender, MouseWheelEventArgs e)
        => RouteMessagesWheel(e, Keyboard.Modifiers);

    internal void RouteMessagesWheel(MouseWheelEventArgs e, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)) { CancelLayoutAnchor(); return; }
        var horizontal = modifiers.HasFlag(ModifierKeys.Shift);
        if (!horizontal) CancelReadingRestore();
        // Tunnel before RichTextBox/TextBox and horizontal-only formula scrollers
        // consume the wheel. A vertically scrollable activity keeps its own scroll;
        // at its edge the gesture continues through the enclosing conversation.
        for (DependencyObject? item = e.OriginalSource as DependencyObject; item is not null; item = ScrollParent(item))
        {
            if (item is System.Windows.Controls.Primitives.TextBoxBase box &&
                box.Template?.FindName("PART_ContentHost", box) is ScrollViewer content &&
                ScrollNestedWithLayoutAnchor(content, e.Delta, horizontal)) { e.Handled = true; return; }
            if (item is ScrollViewer scroll)
            {
                var tracking = !horizontal && ReferenceEquals(scroll, MessagesScroll) && _layoutAnchorPending;
                if (tracking ? ScrollWheel(scroll, e.Delta, horizontal, TrackLayoutAnchorWheel, _layoutAnchor?.Offset)
                    : ScrollNestedWithLayoutAnchor(scroll, e.Delta, horizontal)) { e.Handled = true; return; }
            }
            if (ReferenceEquals(item, MessagesScroll)) break;
        }
    }

    private void CancelReadingRestore()
    {
        if (!_restoringReading) return;
        _readingRestoreVersion++;
        _restoringReading = false;
    }

    private void NavigationMouseDown(object sender, MouseButtonEventArgs e)
    {
        CancelLayoutAnchor();
        for (var item = e.OriginalSource as DependencyObject; item is not null; item = ScrollParent(item))
        {
            if (item is System.Windows.Controls.Primitives.ScrollBar) { CancelReadingRestore(); break; }
            if (ReferenceEquals(item, MessagesScroll)) break;
        }
    }

    private void NavigationKeyDown(object sender, KeyEventArgs e)
    {
        CancelLayoutAnchor();
        if (e.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)) return;
        for (var item = e.OriginalSource as DependencyObject; item is not null; item = ScrollParent(item))
            if (item is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false } or PasswordBox or ComboBox or System.Windows.Controls.Primitives.ButtonBase) return;
        CancelReadingRestore();
    }

    private void NavigationTouchMove(object? sender, TouchEventArgs e) { CancelLayoutAnchor(); CancelReadingRestore(); }
    internal static bool ScrollWheel(ScrollViewer scroll, int delta, bool horizontal, Action<double, double>? beforeScroll = null, double? pendingOffset = null)
    {
        var extent = horizontal ? scroll.ScrollableWidth : scroll.ScrollableHeight;
        var offset = pendingOffset ?? (horizontal ? scroll.HorizontalOffset : scroll.VerticalOffset);
        if (delta == 0 || extent <= 0 || (delta > 0 ? offset <= 0 : offset >= extent)) return false;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return true;
        var step = lines < 0 ? (horizontal ? scroll.ViewportWidth : scroll.ViewportHeight) : lines * 16d;
        var next = Math.Clamp(offset - delta / 120d * step, 0, extent);
        beforeScroll?.Invoke(offset, next);
        if (horizontal) scroll.ScrollToHorizontalOffset(next); else scroll.ScrollToVerticalOffset(next);
        return true;
    }
    private static DependencyObject? ScrollParent(DependencyObject value) => value switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(value),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(value)
    };
}
