using System.IO;
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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private static void MouseActionIn(UIElement scope) => scope.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseUpEvent });

    private static async Task WaitForBarsHiddenAsync(params ScrollBar[] bars)
    {
        // Dispatcher work (layout/raster completion) can delay the idle timer; wait
        // for the observable state instead of assuming the next frame's deadline.
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            if (bars.All(b => b.Opacity < .01 && !b.IsHitTestVisible)) return;
        }
        throw new Exception("Bars did not become idle: " + string.Join("; ", bars.Select(b => $"opacity={b.Opacity}, hit={b.IsHitTestVisible}, over={b.IsMouseOver}, capture={b.IsMouseCaptureWithin}")));
    }

    private static async Task CheckScrollChromeAsync(MainWindow window, string output, string theme)
    {
        var editor = (LatexEditor)window.FindName("Editor");
        var pane = (Grid)window.FindName("EditorPane");
        var workspace = (Grid)window.FindName("Workspace");
        var previewPane = (Grid)window.FindName("PreviewPane");
        var editorBar = Descendants<ScrollBar>(editor).First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
        var previewBar = Descendants<ScrollBar>(previewPane).First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
        var oldOffset = editor.VerticalOffset;
        // Previous modal/menu tests can leave the real pointer over a scrollbar.
        // Park it on the test window's title bar so idle assertions test idle input.
        GetCursorPos(out var previousPointer);
        var titlePoint = window.PointToScreen(new Point(20, -10));
        SetCursorPos((int)titlePoint.X, (int)titlePoint.Y);
        var log = (Expander)window.FindName("LogExpander");
        var tabs = (TabControl)window.FindName("LogTabs");
        var diagnostics = (ListBox)window.FindName("DiagnosticsList");
        var logText = (TextBox)window.FindName("LogText");
        diagnostics.ItemsSource = Enumerable.Range(1, 100).Select(n => new { Message = $"LaTeX Warning: Reference example-{n} undefined.", Line = n }).ToArray();
        logText.Text = string.Join("\n", Enumerable.Range(1, 300).Select(n => $"Log {n}: " + new string('x', 100)));
        try
        {
            // Scope input must reveal only that pane; geometry is stable throughout fading.
            await WaitForBarsHiddenAsync(editorBar, previewBar);
            if (editorBar.Opacity > .01 || previewBar.Opacity > .01) throw new Exception($"Idle bars did not fade out: editor={editorBar.Opacity}, preview={previewBar.Opacity}, over={editorBar.IsMouseOver}/{previewBar.IsMouseOver}, capture={Mouse.Captured}");
            var width = editor.ActualWidth;
            MouseActionIn(pane); await Task.Delay(150);
            if (editorBar.Opacity < .99 || !editorBar.IsHitTestVisible || previewBar.Opacity > .01)
                throw new Exception("Mouse activity did not reveal only the local pane");
            for (var i = 0; i < 8; i++)
            {
                pane.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
                await Task.Delay(200);
            }
            await WaitForBarsHiddenAsync(editorBar);
            if (editorBar.Opacity > .01 || editorBar.IsHitTestVisible || editor.ActualWidth != width)
                throw new Exception($"Scrollbar fade changed layout or left an invisible mouse target: opacity={editorBar.Opacity}, hit={editorBar.IsHitTestVisible}, width={editor.ActualWidth}/{width}");
            pane.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.LeftShift)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Task.Delay(150);
            if (editorBar.Opacity < .99) throw new Exception("Keyboard activity did not reveal scrollbar");
            var track = (Track)editorBar.Template.FindName("PART_Track", editorBar);
            // Capture the bar directly: a synthetic Thumb capture is cancelled by WPF
            // on a later layout-generated mouse move when no physical button is down.
            if (!Mouse.Capture(editorBar)) throw new Exception("Could not capture scrollbar");
            await Task.Delay(1450);
            if (editorBar.Opacity < .99) throw new Exception("Captured scrollbar faded while dragging");
            Mouse.Capture(null);

            foreach (var name in new[] { "SidebarSplitter", "PreviewSplitter" })
            {
                var splitter = (GridSplitter)window.FindName(name);
                var column = workspace.ColumnDefinitions[Grid.GetColumn(splitter)];
                if (column.ActualWidth != 2 || Math.Abs(splitter.ActualWidth - 8) > .1)
                    throw new Exception("Splitter visual gap/hit target is not 2/8 DIP");
                var center = splitter.TranslatePoint(new Point(splitter.ActualWidth / 2, 100), workspace);
                foreach (var dx in new[] { -3d, 0d, 3d })
                {
                    var hit = workspace.InputHitTest(new Point(center.X + dx, center.Y)) as DependencyObject;
                    while (hit != null && hit != splitter) hit = VisualTreeHelper.GetParent(hit);
                    if (hit != splitter) throw new Exception("Divider hit target is occluded by adjacent pane");
                }
            }

            log.IsExpanded = true; window.UpdateLayout();
            if (!((TextBlock)window.FindName("LogToggleHint")).Text.Contains("收起")) throw new Exception("Missing collapse output hint");
            foreach (var atEnd in new[] { false, true })
            {
                if (atEnd) editor.ScrollToEnd(); else editor.ScrollToHome();
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                foreach (var bar in Descendants<ScrollBar>(editor).Where(b => b.IsVisible))
                {
                    var t = (Track)bar.Template.FindName("PART_Track", bar);
                    var box = t.Thumb.TransformToAncestor(t).TransformBounds(new Rect(t.Thumb.RenderSize));
                    if (box.Left < -.1 || box.Top < -.1 || box.Right > t.ActualWidth + .1 || box.Bottom > t.ActualHeight + .1)
                        throw new Exception("Long-document thumb overflows its track with output open");
                    if (bar.Orientation == Orientation.Vertical && t.Thumb.ActualHeight < 23.9)
                        throw new Exception("Long-document thumb is too short to grab");
                }
            }
            var horizontal = Descendants<ScrollBar>(editor).First(b => b.IsVisible && b.Orientation == Orientation.Horizontal);
            if (horizontal.ActualHeight != 12) throw new Exception("Horizontal scrollbar must be 12 DIP");
            var hTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            var x = horizontal.Value;
            ((RoutedCommand)hTrack.IncreaseRepeatButton.Command).Execute(null, hTrack.IncreaseRepeatButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (horizontal.Value <= x) throw new Exception("Horizontal track click did not scroll");
            editor.ScrollToHorizontalOffset(0); editor.ScrollToVerticalOffset(oldOffset);

            double? rightEdge = null;
            for (var tab = 0; tab < 2; tab++)
            {
                tabs.SelectedIndex = tab; window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var bar = Descendants<ScrollBar>(tabs).First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
                var edge = bar.TranslatePoint(new Point(bar.ActualWidth, 0), pane).X;
                if (rightEdge is { } previous && Math.Abs(previous - edge) > .1 || Math.Abs(edge - pane.ActualWidth) > .1)
                    throw new Exception("Problems/log scrollbars do not align with the pane edge");
                rightEdge = edge;
                MouseActionIn(pane); MouseActionIn(tabs); MouseActionIn(previewPane); await Task.Delay(120);
                Capture(window, Path.Combine(output, $"{theme}-output-{tab}.png"));
            }
            log.IsExpanded = false; window.UpdateLayout();
            if (!((TextBlock)window.FindName("LogToggleHint")).Text.Contains("展开")) throw new Exception("Missing expand output hint");
            Console.WriteLine($"PASS: {theme} activity fade, capture retention, pane isolation, thin divider hit targets, long-document thumb bounds and output alignment");
        }
        finally
        {
            Mouse.Capture(null);
            SetCursorPos(previousPointer.X, previousPointer.Y);
            log.IsExpanded = false; tabs.SelectedIndex = 0;
            diagnostics.ItemsSource = null; logText.Clear();
            editor.ScrollToHorizontalOffset(0); editor.ScrollToVerticalOffset(oldOffset);
            window.UpdateLayout(); MouseActionIn(pane); MouseActionIn(previewPane);
        }
    }
}
