using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private static void MouseActionIn(UIElement scope) => scope.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseUpEvent });

    private static Border ScrollGrip(ScrollBar bar)
    {
        var thumb = ((Track)bar.Template.FindName("PART_Track", bar)).Thumb;
        return (Border)thumb.Template.FindName("Grip", thumb);
    }
    private static double ScrollThumbOpacity(string key) => (double)Application.Current.FindResource(key);
    private static Color ScreenshotPixel(string path, Point position)
    {
        using var file = File.OpenRead(path);
        var frame = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var x = Math.Clamp((int)position.X, 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)position.Y, 0, bitmap.PixelHeight - 1);
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static async Task WaitForBarsIdleAsync(params ScrollBar[] bars)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(100);
            if (bars.All(b => b.Opacity > .99 && b.IsHitTestVisible && !ScrollActivity.GetIsScrolling(b) && Math.Abs(ScrollGrip(b).Opacity - ScrollThumbOpacity("ScrollThumbIdleOpacity")) < .01)) return;
        }
        throw new Exception("Bars did not become dim and usable: " + string.Join("; ", bars.Select(b => $"opacity={b.Opacity}, thumb={ScrollGrip(b).Opacity}, active={ScrollActivity.GetIsScrolling(b)}, hit={b.IsHitTestVisible}, over={b.IsMouseOver}, capture={b.IsMouseCaptureWithin}")));
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
        var oldWrap = editor.WordWrap;
        AssertEditorScrollEdges(editor);
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
            foreach (var bar in new[] { editorBar, previewBar })
                AssertHandThumb((Track)bar.Template.FindName("PART_Track", bar), bar == editorBar ? "Editor vertical" : "PDF vertical");
            // The idle scrollbar remains visible and clickable; only the scrolled thumb brightens.
            await WaitForBarsIdleAsync(editorBar, previewBar);
            var width = editor.ActualWidth;
            editor.ScrollToVerticalOffset(oldOffset > 100 ? oldOffset - 60 : oldOffset + 60);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!ScrollActivity.GetIsScrolling(editorBar) || Math.Abs(ScrollGrip(editorBar).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01 || ScrollActivity.GetIsScrolling(previewBar))
                throw new Exception("Scrolling did not brighten only the active pane's thumb");
            await WaitForBarsIdleAsync(editorBar);
            if (editor.ActualWidth != width) throw new Exception("Scrollbar activity changed editor layout");
            var track = (Track)editorBar.Template.FindName("PART_Track", editorBar);
            // Capture the bar directly: a synthetic Thumb capture is cancelled by WPF
            // on a later layout-generated mouse move when no physical button is down.
            if (!Mouse.Capture(editorBar)) throw new Exception("Could not capture scrollbar");
            editorBar.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, editorBar.Value) { RoutedEvent = ScrollBar.ScrollEvent });
            await Task.Delay(1450);
            if (!ScrollActivity.GetIsScrolling(editorBar) || Math.Abs(ScrollGrip(editorBar).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01) throw new Exception("Captured scrollbar dimmed while dragging");
            Mouse.Capture(null);
            await WaitForBarsIdleAsync(editorBar);
            await CheckIndependentScrollbarsAsync(window, output, theme);

            foreach (var name in new[] { "SidebarSplitter", "PreviewSplitter" })
            {
                var splitter = (GridSplitter)window.FindName(name);
                if (QueryCursorFor(splitter) != Cursors.SizeWE)
                    throw new Exception("Divider lost its resize cursor");
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
            AssertEditorScrollEdges(editor);
            if (!((TextBlock)window.FindName("LogToggleHint")).Text.Contains("收起")) throw new Exception("Missing collapse output hint");
            foreach (var atEnd in new[] { false, true })
            {
                if (atEnd) editor.ScrollToEnd(); else editor.ScrollToHome();
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                AssertEditorScrollEdges(editor);
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
            if (oldWrap && Descendants<ScrollBar>(editor).Any(b => b.IsVisible && b.Orientation == Orientation.Horizontal))
                throw new Exception("Wrapped editor must not show a horizontal scrollbar");
            // Exercise the horizontal track in the explicitly unwrapped mode.
            editor.SetWordWrap(false); editor.ScrollToHorizontalOffset(0); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var horizontal = Descendants<ScrollBar>(editor).First(b => b.IsVisible && b.Orientation == Orientation.Horizontal);
            if (horizontal.ActualHeight != 12) throw new Exception("Horizontal scrollbar must be 12 DIP");
            var hTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            AssertHandThumb(hTrack, "Editor horizontal");
            var x = horizontal.Value;
            ((RoutedCommand)hTrack.IncreaseRepeatButton.Command).Execute(null, hTrack.IncreaseRepeatButton);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (horizontal.Value <= x) throw new Exception("Horizontal track click did not scroll");
            editor.SetWordWrap(oldWrap); window.UpdateLayout();
            editor.ScrollToHorizontalOffset(0); editor.ScrollToVerticalOffset(oldOffset);

            double? rightEdge = null;
            for (var tab = 0; tab < 2; tab++)
            {
                tabs.SelectedIndex = tab; window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var bar = Descendants<ScrollBar>(tabs).First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
                AssertHandThumb((Track)bar.Template.FindName("PART_Track", bar), tab == 0 ? "Problems" : "Log");
                var edge = bar.TranslatePoint(new Point(bar.ActualWidth, 0), pane).X;
                if (rightEdge is { } previous && Math.Abs(previous - edge) > .1 || Math.Abs(edge - pane.ActualWidth) > .1)
                    throw new Exception("Problems/log scrollbars do not align with the pane edge");
                rightEdge = edge;
                MouseActionIn(pane); MouseActionIn(tabs); MouseActionIn(previewPane); await Task.Delay(120);
                Capture(window, Path.Combine(output, $"{theme}-output-{tab}.png"));
            }
            log.IsExpanded = false; window.UpdateLayout();
            if (!((TextBlock)window.FindName("LogToggleHint")).Text.Contains("展开")) throw new Exception("Missing expand output hint");
            Console.WriteLine($"PASS: {theme} persistent dim scrollbar, active thumb brightness, pane isolation, capture retention, cursors, divider hit targets, thumb bounds and output alignment");
        }
        finally
        {
            Mouse.Capture(null);
            SetCursorPos(previousPointer.X, previousPointer.Y);
            log.IsExpanded = false; tabs.SelectedIndex = 0;
            diagnostics.ItemsSource = null; logText.Clear();
            editor.SetWordWrap(oldWrap);
            editor.ScrollToHorizontalOffset(0); editor.ScrollToVerticalOffset(oldOffset);
            window.UpdateLayout(); MouseActionIn(pane); MouseActionIn(previewPane);
        }
    }

    private static async Task CheckIndependentScrollbarsAsync(Window owner, string output, string theme)
    {
        var left = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 700, Height = 1000 } };
        var middle = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Height = 1000 } };
        var empty = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 20, Height = 20 } };
        var fixedEmpty = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Width = 20, Height = 20 } };
        var grid = new Grid();
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left); Grid.SetColumn(middle, 1); grid.Children.Add(middle); Grid.SetColumn(empty, 2); grid.Children.Add(empty);
        Grid.SetColumn(fixedEmpty, 3); grid.Children.Add(fixedEmpty);
        var dialog = new Window { Owner = owner, Width = 600, Height = 300, ShowInTaskbar = false, Content = grid };
        try
        {
            dialog.Show(); dialog.UpdateLayout(); await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var leftV = (ScrollBar)left.Template.FindName("PART_VerticalScrollBar", left);
            var leftH = (ScrollBar)left.Template.FindName("PART_HorizontalScrollBar", left);
            var middleV = (ScrollBar)middle.Template.FindName("PART_VerticalScrollBar", middle);
            var emptyV = (ScrollBar)empty.Template.FindName("PART_VerticalScrollBar", empty);
            var emptyH = (ScrollBar)empty.Template.FindName("PART_HorizontalScrollBar", empty);
            var fixedBar = (ScrollBar)fixedEmpty.Template.FindName("PART_VerticalScrollBar", fixedEmpty);
            var fixedTrack = (Track)fixedBar.Template.FindName("PART_Track", fixedBar);
            if (!fixedBar.IsVisible || fixedBar.Maximum != 0 || fixedBar.IsEnabled || fixedTrack.IsVisible || fixedTrack.Thumb.IsVisible)
                throw new Exception("An empty fixed gutter must not present a draggable scrollbar thumb");
            if (!leftV.IsVisible || !leftH.IsVisible || !middleV.IsVisible || emptyV.IsVisible || emptyH.IsVisible)
                throw new Exception("Auto scrollbar visibility did not follow actual overflow");
            await WaitForBarsIdleAsync(leftV, leftH, middleV);
            Capture(dialog, Path.Combine(output, $"{theme}-scrollbar-idle.png"));
            left.ScrollToVerticalOffset(100); dialog.UpdateLayout();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!ScrollActivity.GetIsScrolling(leftV) || Math.Abs(ScrollGrip(leftV).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01 ||
                ScrollActivity.GetIsScrolling(leftH) || ScrollActivity.GetIsScrolling(middleV))
                throw new Exception("Vertical scrolling highlighted an unrelated axis or viewer");
            Capture(dialog, Path.Combine(output, $"{theme}-scrollbar-active.png"));
            await WaitForBarsIdleAsync(leftV, leftH, middleV);
            left.ScrollToHorizontalOffset(100); dialog.UpdateLayout();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!ScrollActivity.GetIsScrolling(leftH) || Math.Abs(ScrollGrip(leftH).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01 ||
                ScrollActivity.GetIsScrolling(leftV) || ScrollActivity.GetIsScrolling(middleV))
                throw new Exception("Horizontal scrolling highlighted an unrelated axis or viewer");
            await WaitForBarsIdleAsync(leftV, leftH, middleV);
        }
        finally { dialog.Close(); owner.Activate(); }
    }

    private static async Task CheckScrollActivityAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "scrollbar-project");
        Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}test\\end{document}\n");
        await CallAsync(window, "OpenAsync", main);
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        var book = (TeXeed.App.AI.AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        book.Turns.Add(new TeXeed.App.AI.AiTurn("问题", string.Join("\n\n", Enumerable.Range(1, 80).Select(i => $"正文第 {i} 段，滚动时不隐藏滚动条。"))));
        Invoke(window, "OpenAiTab"); Invoke(panel, "RefreshTranscript");
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var oldTheme = ThemeManager.Current.Id;
        GetCursorPos(out var previousPointer);
        try
        {
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(theme);
                window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
                if (scroll.ScrollableHeight < 200) throw new Exception("AI scrollbar fixture did not overflow");
                var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                var point = window.PointToScreen(new Point(20, -10));
                if (!SetCursorPos((int)point.X, (int)point.Y)) throw new Exception("Cannot move pointer away from scrollbar");
                await WaitForBarsIdleAsync(bar);
                var visual = (FrameworkElement)window.Content;
                Point ThumbCenter() { var grip = ScrollGrip(bar); return grip.TranslatePoint(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2), visual); }
                var idlePath = Path.Combine(output, $"{theme}-ai-scrollbar-idle.png");
                Capture(window, idlePath);
                var idlePixel = ScreenshotPixel(idlePath, ThumbCenter());
                scroll.ScrollToVerticalOffset(150); window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (!ScrollActivity.GetIsScrolling(bar) || Math.Abs(ScrollGrip(bar).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01)
                    throw new Exception("AI conversation scrolling did not brighten the thumb");
                var activePath = Path.Combine(output, $"{theme}-ai-scrollbar-active.png");
                Capture(window, activePath);
                var activePixel = ScreenshotPixel(activePath, ThumbCenter());
                var trackPixel = ScreenshotPixel(activePath, bar.TranslatePoint(new Point(bar.ActualWidth / 2, bar.ActualHeight - 3), visual));
                var idleLuminance = RelativeLuminance(idlePixel); var activeLuminance = RelativeLuminance(activePixel); var trackLuminance = RelativeLuminance(trackPixel);
                var contrast = (Math.Max(activeLuminance, trackLuminance) + .05) / (Math.Min(activeLuminance, trackLuminance) + .05);
                if (activeLuminance <= idleLuminance + .01 || contrast < 1.5)
                    throw new Exception($"{theme} active thumb is not brighter and distinguishable: idle {idlePixel}, active {activePixel}, track {trackPixel}, contrast {contrast:F2}");
                await WaitForBarsIdleAsync(bar);
                foreach (var delta in new[] { 120, -120 })
                {
                    var before = scroll.VerticalOffset;
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    scroll.RaiseEvent(wheel); window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    if (!wheel.Handled || Math.Abs(scroll.VerticalOffset - before) < 1 ||
                        !ScrollActivity.GetIsScrolling(bar) || Math.Abs(ScrollGrip(bar).Opacity - ScrollThumbOpacity("ScrollThumbActiveOpacity")) > .01)
                        throw new Exception("AI conversation mouse-wheel routing did not move and brighten the scrollbar");
                    await WaitForBarsIdleAsync(bar);
                }
                await CheckIndependentScrollbarsAsync(window, output, theme);
                scroll.ScrollToTop(); window.UpdateLayout();
            }
            Console.WriteLine("PASS: dark/light AI conversation and independent scrollbars remain visible while idle, brighten on their own axis, and hide automatically only without overflow");
        }
        finally { ThemeManager.Apply(oldTheme); SetCursorPos(previousPointer.X, previousPointer.Y); }
    }

    private static void AssertEditorScrollEdges(LatexEditor editor)
    {
        var view = editor.TextArea.TextView;
        var contentEdge = view.TranslatePoint(new Point(view.ActualWidth, view.ActualHeight), editor);
        var vertical = Descendants<ScrollBar>(editor).First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
        var horizontal = Descendants<ScrollBar>(editor).FirstOrDefault(b => b.IsVisible && b.Orientation == Orientation.Horizontal);
        var rightGap = vertical.TranslatePoint(new Point(), editor).X - contentEdge.X;
        var bottomGap = horizontal is null ? 0 : horizontal.TranslatePoint(new Point(), editor).Y - contentEdge.Y;
        if (Math.Abs(rightGap) > 1 || Math.Abs(bottomGap) > 1)
            throw new Exception($"Blank strips between editor viewport and scrollbars: right={rightGap:F2}, bottom={bottomGap:F2} DIP");
    }
}
