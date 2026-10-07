using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Shell;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct TestMonitor { public int Size; public MenuRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref TestMonitor info);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [StructLayout(LayoutKind.Sequential)] private struct CoveragePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(CoveragePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
    private static void CheckWindowBottom(MainWindow window, bool fullscreen)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = new TestMonitor { Size = Marshal.SizeOf<TestMonitor>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) throw new Exception("Cannot read monitor bounds");
        var target = fullscreen ? monitor.Monitor : monitor.Work;
        var root = (FrameworkElement)window.FindName("WindowRoot");
        var bottom = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        var top = root.PointToScreen(new Point());
        if (bottom.Y > target.Bottom + 1 || bottom.X > target.Right + 1 || top.X < target.Left - 1 || top.Y < target.Top - 1)
            throw new Exception($"Window content clipped: ({top})–({bottom}), monitor ({target.Left},{target.Top})–({target.Right},{target.Bottom}), fullscreen={fullscreen}");
        if (fullscreen)
        {
            if (Math.Abs(top.X - target.Left) > 1 || Math.Abs(top.Y - target.Top) > 1
                || Math.Abs(bottom.X - target.Right) > 1 || Math.Abs(bottom.Y - target.Bottom) > 1)
                throw new Exception("Fullscreen content does not fill the entire monitor");
            CheckFullscreenRegion(hwnd, target);
        }
        foreach (var name in new[] { "EditorStatusBar", "PreviewStatusBar" })
        {
            var footer = (FrameworkElement)window.FindName(name);
            if (footer.PointToScreen(new Point(0, footer.ActualHeight)).Y > target.Bottom + 1)
                throw new Exception("Maximize hid " + name);
        }
    }
    private static void CheckFullscreenRegion(nint hwnd, MenuRect monitor)
    {
        if (!GetWindowRect(hwnd, out var bounds)) throw new Exception("Cannot read fullscreen HWND bounds");
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Exception("Cannot allocate fullscreen region probe");
        try
        {
            // WPF layout and the HWND rectangle can be full-screen even when
            // WindowChrome clips its actual native region to the work area.
            if (GetWindowRgn(hwnd, region) == 0) return; // No explicit region: full HWND rectangle.
            foreach (var point in new[] {
                new Point(monitor.Left + 1, monitor.Top + 1), new Point(monitor.Right - 2, monitor.Top + 1),
                new Point(monitor.Left + 1, monitor.Bottom - 2), new Point(monitor.Right - 2, monitor.Bottom - 2) })
                if (!PtInRegion(region, (int)point.X - bounds.Left, (int)point.Y - bounds.Top))
                    throw new Exception($"Fullscreen native window region excludes monitor point {point}; taskbar strip remains uncovered");
        }
        finally { DeleteObject(region); }
    }
    private static async Task CheckFullscreenAsync(MainWindow window, string output)
    {
        async Task Settle()
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout();
        }
        var title = (FrameworkElement)window.FindName("TitleBar");
        var item = (MenuItem)window.FindName("FullscreenMenuItem");
        var toast = (FrameworkElement)window.FindName("FullscreenToast");
        var workspace = (FrameworkElement)window.FindName("Workspace");
        var originalChrome = WindowChrome.GetWindowChrome(window);
        var originalResizeMode = window.ResizeMode;
        foreach (var prior in new[] { WindowState.Normal, WindowState.Maximized })
        {
            window.WindowState = prior; await Settle();
            var bounds = window.RestoreBounds;
            var key = CreateKey(window, Key.F11, Keyboard.PreviewKeyDownEvent);
            await CallAsync(window, "HandleShortcutAsync", key, ModifierKeys.None); await Settle();
            if (!key.Handled || title.IsVisible || !item.IsChecked || !toast.IsVisible || workspace.TranslatePoint(new Point(), window).Y > 1)
                throw new Exception("F11 failed to hide caption, show toast, check menu and fill fullscreen");
            CheckWindowBottom(window, true);
            if (WindowChrome.GetWindowChrome(window) is not null || window.Topmost)
                throw new Exception("Fullscreen must remove work-area chrome clipping without forcing always-on-top");
            if (!(bool)Field(window, "_shellFullscreenMarked")!) throw new Exception("Fullscreen was not explicitly registered with Windows Shell");
            await Task.Delay(240);
            CheckActiveFullscreenCoverage(window);
            if (toast.Opacity < .9) throw new Exception("Fullscreen toast did not fade in");
            Capture(window, Path.Combine(output, "fullscreen-" + prior + ".png"));
            // Shell reactivation and WPF state restoration must not recreate
            // the work-area clip after a full-screen minimize/restore cycle.
            window.WindowState = WindowState.Minimized; await Settle();
            SystemCommands.RestoreWindow(window); window.Activate(); await Settle();
            await Task.Delay(160);
            CheckWindowBottom(window, true);
            CheckActiveFullscreenCoverage(window);
            // Alt still provides access to the View/Fullscreen command without a title bar.
            Invoke(window, "ProcessMenuKeyDown", Key.V, ModifierKeys.Alt, false); await Settle();
            var popup = (Popup)Field(window, "_compactMenu")!;
            var panel = (CompactMenuPanel)popup.Child;
            if (!popup.IsOpen) throw new Exception("Fullscreen Alt+V did not show menu");
            Descendants<Button>(panel).Single(button => ReferenceEquals(button.Tag, item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            if (window.WindowState != prior || !title.IsVisible || item.IsChecked || !toast.IsVisible || popup.IsOpen)
                throw new Exception("Menu exit did not restore previous window state");
            if ((bool)Field(window, "_shellFullscreenMarked")!) throw new Exception("Leaving fullscreen did not clear Shell marking");
            if (!ReferenceEquals(WindowChrome.GetWindowChrome(window), originalChrome) || window.ResizeMode != originalResizeMode)
                throw new Exception("Leaving fullscreen did not restore the original native titlebar/resizing chrome");
            if (Math.Abs(window.RestoreBounds.Width - bounds.Width) > 1 || Math.Abs(window.RestoreBounds.Height - bounds.Height) > 1)
                throw new Exception("Fullscreen lost restored window dimensions");
            if (prior == WindowState.Maximized) CheckWindowBottom(window, false);
        }
        await Task.Delay(2800);
        if (toast.IsVisible) throw new Exception("Fullscreen toast did not fade out and hide");
        window.WindowState = WindowState.Normal; await Settle();
        Console.WriteLine("PASS: work-area maximize, full-monitor HWND region, taskbar-strip hit testing, minimize/reactivation, restored chrome/state, F11/menu exit, toast fades and Alt access");
    }
    private static void CheckActiveFullscreenCoverage(MainWindow window)
    {
        // Foreground checks measure actual desktop occlusion; RenderTargetBitmap
        // ignores both native HWND clipping and other windows covering the app.
        window.Activate();
        if (!window.IsActive) throw new Exception("Cannot activate fullscreen coverage fixture");
        var hwnd = new WindowInteropHelper(window).Handle;
        var tray = FindWindow("Shell_TrayWnd", null);
        for (var above = GetWindow(hwnd, 3); above != 0; above = GetWindow(above, 3))
            if (above == tray) throw new Exception("Taskbar remained above active fullscreen window");
        var monitor = new TestMonitor { Size = Marshal.SizeOf<TestMonitor>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) throw new Exception("Cannot read fullscreen coverage monitor");
        var point = new CoveragePoint { X = (monitor.Monitor.Left + monitor.Monitor.Right) / 2, Y = monitor.Monitor.Bottom - 2 };
        var hit = WindowFromPoint(point);
        if (GetAncestor(hit, 2) != hwnd) // GA_ROOT
            throw new Exception($"Fullscreen does not cover the bottom of the monitor: hit HWND={hit}, expected={hwnd}");
    }
    private static async Task CheckZoomControlsAsync(MainWindow window, string output)
    {
        var preview = (PdfPreview)window.FindName("Preview");
        var slider = (Slider)window.FindName("ZoomSlider");
        var button = (Button)window.FindName("ZoomButton");
        var input = (TextBox)window.FindName("ZoomInput");
        var auto = (ToggleButton)window.FindName("AutoWidthButton");
        slider.Value = 123.45;
        if (Math.Abs(preview.Zoom - 1.2345) > .00001 || auto.IsChecked == true || !button.Content.ToString()!.Contains("123.45"))
            throw new Exception("Slider did not update exact zoom and disable auto width");
        auto.IsChecked = true; auto.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!preview.IsAutoWidth || auto.IsChecked != true) throw new Exception("Auto width failed to enable");
        auto.IsChecked = false; auto.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (preview.IsAutoWidth) throw new Exception("Auto width toggle-off failed to freeze zoom");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var selectedInk = (SolidColorBrush)input.SelectionTextBrush;
        if (selectedInk.Color != ((SolidColorBrush)input.Foreground).Color || selectedInk.Color == ((SolidColorBrush)input.SelectionBrush).Color)
            throw new Exception("Selected zoom percentage is unreadable in current theme");
        Capture(window, Path.Combine(output, "zoom-percentage-selected.png"));
        input.Text = "137.62%";
        input.RaiseEvent(CreateKey(window, Key.Enter, Keyboard.KeyDownEvent));
        if (input.IsVisible || Math.Abs(preview.Zoom - 1.3762) > .00001) throw new Exception("Click-to-enter zoom percentage failed");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); input.Text = "NaN";
        input.RaiseEvent(CreateKey(window, Key.Enter, Keyboard.KeyDownEvent));
        if (!input.IsVisible || Math.Abs(preview.Zoom - 1.3762) > .00001) throw new Exception("Invalid zoom input was accepted");
        input.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.KeyDownEvent));
        preview.SetZoom(1.1); // The wheel also calls SetZoom directly: toolbar must update from the preview event.
        if (Math.Abs(slider.Value - 110) > .00001 || button.Content.ToString() != "110.00%") throw new Exception($"External preview zoom did not update toolbar: {slider.Value}, {button.Content}");
        preview.FitWidth(); window.UpdateLayout();
        var before = preview.Zoom;
        window.Width += 120; window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!preview.IsAutoWidth || Math.Abs(slider.Value - preview.Zoom * 100) > .001)
            throw new Exception("Resize fit zoom disagrees with actual percentage");
        if (preview.PageCount > 0 && Math.Abs(before - preview.Zoom) < .001)
            throw new Exception("Auto width did not actually resize loaded PDF pages");
        Capture(window, Path.Combine(output, "zoom-slider.png"));
        window.Width -= 120;
        Console.WriteLine("PASS: zoom slider, exact percentage entry/validation, auto-width toggle and resize/wheel synchronization");
    }
}
