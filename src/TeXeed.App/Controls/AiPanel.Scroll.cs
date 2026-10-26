using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private void MessagesScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Tunnel before RichTextBox/TextBox and horizontal-only formula scrollers
        // consume the wheel. A vertically scrollable activity keeps its own scroll;
        // at its edge the gesture continues through the enclosing conversation.
        for (DependencyObject? item = e.OriginalSource as DependencyObject; item is not null; item = ScrollParent(item))
        {
            if (item is System.Windows.Controls.Primitives.TextBoxBase box &&
                box.Template?.FindName("PART_ContentHost", box) is ScrollViewer content &&
                ScrollWheel(content, e.Delta, horizontal)) { e.Handled = true; return; }
            if (item is ScrollViewer scroll && ScrollWheel(scroll, e.Delta, horizontal)) { e.Handled = true; return; }
            if (ReferenceEquals(item, MessagesScroll)) break;
        }
    }
    internal static bool ScrollWheel(ScrollViewer scroll, int delta, bool horizontal)
    {
        var extent = horizontal ? scroll.ScrollableWidth : scroll.ScrollableHeight;
        var offset = horizontal ? scroll.HorizontalOffset : scroll.VerticalOffset;
        if (delta == 0 || extent <= 0 || (delta > 0 ? offset <= 0 : offset >= extent)) return false;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return true;
        var step = lines < 0 ? (horizontal ? scroll.ViewportWidth : scroll.ViewportHeight) : lines * 16d;
        var next = Math.Clamp(offset - delta / 120d * step, 0, extent);
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
