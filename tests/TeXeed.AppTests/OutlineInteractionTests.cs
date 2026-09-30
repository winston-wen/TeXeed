using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void DoubleClickHeading(MainWindow window, Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(down, 2);
        button.RaiseEvent(down);
        if (!down.Handled) throw new Exception("Heading double click was not consumed");
    }
    private static async Task CheckOutlineInteractionsAsync(MainWindow window, string output)
    {
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var tree = (TreeView)outline.FindName("Tree");
        var node = tree.Items.Cast<OutlineNode>().First(n => n.Children.Count > 0);
        var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
        var button = (Button)item.Template.FindName("HeadingButton", item);
        var navigation = 0;
        EventHandler<TeXeed.Core.SourceLocation> observe = (_, _) => navigation++;
        outline.NavigateRequested += observe;
        try
        {
            var expanded = node.IsExpanded;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (node.IsExpanded == expanded || navigation != 0) throw new Exception("Single heading click must fold without navigation");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DoubleClickHeading(window, button);
            if (node.IsExpanded != expanded || navigation != 1) throw new Exception("Double heading click must navigate once without changing expansion");
            window.UpdateLayout();
            var title = Descendants<OutlineTitleViewport>(tree).First(t => t.IsVisible && t.IsClipped);
            DependencyObject? ancestor = title;
            while (ancestor is not Button && ancestor is not null) ancestor = VisualTreeHelper.GetParent(ancestor);
            var marqueeButton = (Button)ancestor!;
            marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            await Task.Delay(750);
            if (!title.IsMarqueeActive || title.MarqueeOffset <= 2 || navigation != 1)
                throw new Exception("Right-button hold must scroll the clipped title without navigation");
            CaptureOutline(outline, System.IO.Path.Combine(output, "outline-marquee.png"));
            marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (title.IsMarqueeActive || title.MarqueeOffset != 0 || marqueeButton.IsMouseCaptured)
                throw new Exception("Releasing right button did not reset title marquee and capture");
            var scroll = Descendants<ScrollViewer>(tree).First();
            foreach (var row in Descendants<TreeViewItem>(tree).Where(i => i.IsVisible && i.HasItems))
            {
                var expander = (FrameworkElement)row.Template.FindName("Expander", row);
                var right = expander.TranslatePoint(new Point(expander.ActualWidth, 0), tree).X;
                if (right > tree.ActualWidth + 1) throw new Exception("Long heading pushed expander outside sidebar");
            }
            if (scroll.ScrollableWidth > 1) throw new Exception("Outline still needs horizontal scrolling to see expanders");
            Console.WriteLine("PASS: single-click folding, double-click navigation, bounded expanders, right-hold marquee and release reset");
        }
        finally { outline.NavigateRequested -= observe; }
    }
}
