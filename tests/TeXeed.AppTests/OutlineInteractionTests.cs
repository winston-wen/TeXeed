using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        // Native mouse capture needs an active window and a pointer input source.
        window.Activate();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!window.IsActive) throw new Exception("Cannot activate the outline hold fixture");
        if (!GetCursorPos(out var previousPointer))
            throw new Exception($"Cannot read the outline fixture pointer: Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var tree = (TreeView)outline.FindName("Tree");
        var node = tree.Items.Cast<OutlineNode>().First(n => n.Children.Count > 0);
        var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
        var button = (Button)item.Template.FindName("HeadingButton", item);
        var navigation = 0;
        var pointerRestored = false;
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
            var clippedBranch = Descendants<TreeViewItem>(tree).First(i => i.IsVisible && i.HasItems
                && Descendants<OutlineTitleViewport>((Button)i.Template.FindName("HeadingButton", i)).Single().IsClipped);
            var branchButton = (Button)clippedBranch.Template.FindName("HeadingButton", clippedBranch);
            var branchNode = (OutlineNode)branchButton.DataContext;
            var branchExpanded = branchNode.IsExpanded;
            branchButton.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var pointer = branchButton.PointToScreen(new Point(branchButton.ActualWidth / 2, branchButton.ActualHeight / 2));
            if (!SetCursorPos((int)pointer.X, (int)pointer.Y))
                throw new Exception($"Cannot position the pointer over the outline hold fixture: Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            try
            {
                void Left(RoutedEvent routedEvent) => branchButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent });
                void ReleaseAt(Point point) => typeof(DocumentOutline).GetMethod("CompleteHeadingLeftHold", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(outline, [branchButton, point]);
                // Synthetic routed input does not update the native mouse
                // device position; supply the release position explicitly.
                var center = new Point(branchButton.ActualWidth / 2, branchButton.ActualHeight / 2);
                Left(UIElement.PreviewMouseLeftButtonDownEvent);
                ReleaseAt(center);
                if (branchNode.IsExpanded == branchExpanded || navigation != 1)
                    throw new Exception("Short left press on a clipped branch no longer folds exactly once");
                Left(UIElement.PreviewMouseLeftButtonDownEvent);
                ReleaseAt(center);
                if (branchNode.IsExpanded != branchExpanded) throw new Exception("Second short press did not restore expansion");
                Left(UIElement.PreviewMouseLeftButtonDownEvent);
                ReleaseAt(new Point(-10, -10));
                if (branchNode.IsExpanded != branchExpanded) throw new Exception("Releasing outside the heading must not fold");
                Left(UIElement.PreviewMouseLeftButtonDownEvent);
                await Task.Delay(650);
                if (!Descendants<OutlineTitleViewport>(branchButton).Single().IsMarqueeActive)
                    throw new Exception($"Branch left hold did not start playback: clipped={Descendants<OutlineTitleViewport>(branchButton).Single().IsClipped}, capture={branchButton.IsMouseCaptured}");
                ReleaseAt(center);
                if (branchNode.IsExpanded != branchExpanded || navigation != 1)
                    throw new Exception("Long left press folded a branch on release");
                DoubleClickHeading(window, branchButton);
                if (branchNode.IsExpanded != branchExpanded || navigation != 2)
                    throw new Exception("Clipped branch double click must preserve expansion and navigate once");
                navigation = 1;
            }
            finally
            {
                if (branchButton.IsMouseCaptured) branchButton.ReleaseMouseCapture();
            }
            window.UpdateLayout();
            var title = Descendants<OutlineTitleViewport>(tree).Where(t => t.IsVisible && t.IsClipped)
                .OrderByDescending(t => Descendants<LatexTitleBlock>(t).Single().DesiredSize.Width / t.ActualWidth).First();
            DependencyObject? ancestor = title;
            while (ancestor is not Button && ancestor is not null) ancestor = VisualTreeHelper.GetParent(ancestor);
            var marqueeButton = (Button)ancestor!;
            marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            await Task.Delay(750);
            if (!title.IsMarqueeActive || title.MarqueeOffset <= 2 || navigation != 1)
                throw new Exception("Right-button hold must scroll the clipped title without navigation");
            window.UpdateLayout();
            CheckMarqueeTail(title);
            Capture(window, System.IO.Path.Combine(output, "outline-marquee-tail.png"));
            marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (title.IsMarqueeActive || title.MarqueeOffset != 0 || marqueeButton.IsMouseCaptured)
                throw new Exception("Releasing right button did not reset title marquee and capture");
            var marqueeNode = (OutlineNode)marqueeButton.DataContext;
            var wasExpanded = marqueeNode.IsExpanded;
            marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            await Task.Delay(700);
            if (!title.IsMarqueeActive || title.MarqueeOffset <= 2 || navigation != 1)
                throw new Exception("Left-button long hold must play the title without navigation");
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
            marqueeButton.RaiseEvent(up);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!up.Handled || title.IsMarqueeActive || title.MarqueeOffset != 0 || marqueeButton.IsMouseCaptured
                || marqueeNode.IsExpanded != wasExpanded || navigation != 1)
                throw new Exception("Left-hold release must reset playback without folding or navigation");
            // Losing capture during either the delay or playback must cancel
            // the timer; it must not start moving the title later on its own.
            foreach (var delay in new[] { 0, 500 })
            {
                marqueeButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                if (delay > 0) await Task.Delay(delay);
                marqueeButton.ReleaseMouseCapture();
                await Task.Delay(450);
                if (title.IsMarqueeActive || title.MarqueeOffset != 0 || marqueeButton.IsMouseCaptured)
                    throw new Exception("Losing left capture did not cancel pending/active marquee");
            }
            var scroll = Descendants<ScrollViewer>(tree).First();
            foreach (var row in Descendants<TreeViewItem>(tree).Where(i => i.IsVisible && i.HasItems))
            {
                var expander = (FrameworkElement)row.Template.FindName("Expander", row);
                var right = expander.TranslatePoint(new Point(expander.ActualWidth, 0), tree).X;
                if (right > tree.ActualWidth + 1) throw new Exception("Long heading pushed expander outside sidebar");
            }
            if (scroll.ScrollableWidth > 1) throw new Exception("Outline still needs horizontal scrolling to see expanders");
            Console.WriteLine("PASS: single-click folding, double-click navigation, full title tail rendering, both-button hold playback, release and capture-loss reset");
        }
        finally
        {
            outline.NavigateRequested -= observe;
            pointerRestored = SetCursorPos(previousPointer.X, previousPointer.Y);
        }
        if (!pointerRestored) throw new Exception("Cannot restore the pointer after the outline hold fixture");
    }

    private static void CheckMarqueeTail(OutlineTitleViewport title)
    {
        var content = Descendants<LatexTitleBlock>(title).Single();
        var max = content.DesiredSize.Width - title.ActualWidth;
        if (max <= title.ActualWidth) throw new Exception("Marquee regression fixture must exceed two viewport widths");
        if (VisualTreeHelper.GetClip(content) is { } clip && clip.Bounds.Right < content.DesiredSize.Width - .5)
            throw new Exception("Moving title still has a viewport-sized layout clip; its tail can never appear");
        // Move the real timer to its last step, avoiding a lengthy animation
        // wait. Verify the rendered tail, rather than just the offset value.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(OutlineTitleViewport).GetField("_offset", flags)!.SetValue(title, max - 1);
        typeof(OutlineTitleViewport).GetField("_lastTick", flags)!.SetValue(title, Environment.TickCount64 - 100);
        typeof(OutlineTitleViewport).GetField("_pauseUntil", flags)!.SetValue(title, 0L);
        typeof(OutlineTitleViewport).GetMethod("AdvanceMarquee", flags)!.Invoke(title, null);
        if (Math.Abs(title.MarqueeOffset - max) > .5) throw new Exception("Marquee stopped before reaching the full title tail");
        var width = (int)Math.Ceiling(title.ActualWidth);
        var height = (int)Math.Ceiling(title.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(title) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, title.ActualWidth, title.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var ink = 0;
        for (var y = 0; y < height; y++)
            for (var x = width * 3 / 4; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 40) ink++;
        if (ink < 10) throw new Exception("Last quarter of title is blank at the marquee endpoint");
    }
}
