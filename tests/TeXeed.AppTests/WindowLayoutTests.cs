using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct TestMonitor { public int Size; public MenuRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref TestMonitor info);
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
        foreach (var name in new[] { "EditorStatusBar", "PreviewStatusBar" })
        {
            var footer = (FrameworkElement)window.FindName(name);
            if (footer.PointToScreen(new Point(0, footer.ActualHeight)).Y > target.Bottom + 1)
                throw new Exception("Maximize hid " + name);
        }
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
        foreach (var prior in new[] { WindowState.Normal, WindowState.Maximized })
        {
            window.WindowState = prior; await Settle();
            var bounds = window.RestoreBounds;
            var key = CreateKey(window, Key.F11, Keyboard.PreviewKeyDownEvent);
            await CallAsync(window, "HandleShortcutAsync", key, ModifierKeys.None); await Settle();
            if (!key.Handled || title.IsVisible || !item.IsChecked || !toast.IsVisible || workspace.TranslatePoint(new Point(), window).Y > 1)
                throw new Exception("F11 failed to hide caption, show toast, check menu and fill fullscreen");
            CheckWindowBottom(window, true);
            await Task.Delay(240);
            if (toast.Opacity < .9) throw new Exception("Fullscreen toast did not fade in");
            Capture(window, Path.Combine(output, "fullscreen-" + prior + ".png"));
            // Alt still provides access to the View/Fullscreen command without a title bar.
            Invoke(window, "ProcessMenuKeyDown", Key.V, ModifierKeys.Alt, false); await Settle();
            var popup = (Popup)Field(window, "_compactMenu")!;
            var panel = (CompactMenuPanel)popup.Child;
            if (!popup.IsOpen) throw new Exception("Fullscreen Alt+V did not show menu");
            Descendants<Button>(panel).Single(button => ReferenceEquals(button.Tag, item)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            if (window.WindowState != prior || !title.IsVisible || item.IsChecked || !toast.IsVisible || popup.IsOpen)
                throw new Exception("Menu exit did not restore previous window state");
            if (Math.Abs(window.RestoreBounds.Width - bounds.Width) > 1 || Math.Abs(window.RestoreBounds.Height - bounds.Height) > 1)
                throw new Exception("Fullscreen lost restored window dimensions");
            if (prior == WindowState.Maximized) CheckWindowBottom(window, false);
        }
        await Task.Delay(2800);
        if (toast.IsVisible) throw new Exception("Fullscreen toast did not fade out and hide");
        window.WindowState = WindowState.Normal; await Settle();
        Console.WriteLine("PASS: work-area maximize, fullscreen monitor bounds, F11/menu exit, normal/maximized state restoration, toast fades and Alt access");
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
