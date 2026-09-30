using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint CaptionMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static nint CaptionPoint(Point point) => (nint)((((int)point.Y & 0xffff) << 16) | ((int)point.X & 0xffff));

    private static async Task CheckTitleBarAsync(MainWindow window, string output)
    {
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome is null || chrome.CaptionHeight != 40 || chrome.ResizeBorderThickness.Left <= 0)
            throw new Exception("Native caption dragging/resizing must use WindowChrome");
        var title = (FrameworkElement)window.FindName("TitleBar");
        var menu = (Menu)window.FindName("MainMenu");
        var compact = (Button)window.FindName("CompactMenuButton");
        var editor = (LatexEditor)window.FindName("Editor");
        var menus = menu.Items.OfType<MenuItem>().ToArray();
        var document = editor.Document;
        var originalTheme = ThemeManager.Current.Id;
        async Task SettleAsync()
        {
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        void CheckBounds()
        {
            var controls = new[] { "CompactMenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" };
            var previousRight = 0d;
            foreach (var name in controls)
            {
                var control = (FrameworkElement)window.FindName(name);
                if (!control.IsVisible) continue;
                var x = control.TranslatePoint(new Point(), title).X;
                if (x < previousRight - 1 || x + control.ActualWidth > title.ActualWidth + 1)
                    throw new Exception($"Caption {name} overlaps or clips at width {window.ActualWidth:F1}");
                previousRight = x + control.ActualWidth;
            }
        }
        window.Width = 1100;
        await SettleAsync();
        if (!menu.IsVisible || compact.IsVisible || menus.Length != 5)
            throw new Exception("Wide caption must show all five menus without a hamburger");
        var hwnd = new WindowInteropHelper(window).Handle;
        var captionScreen = title.PointToScreen(new Point(title.ActualWidth - 180, 20));
        if (CaptionMessage(hwnd, 0x84, 0, CaptionPoint(captionScreen)) != 2)
            throw new Exception("Empty caption must return native HTCAPTION for dragging");
        if (window.FindName("AppIconButton") is not null) throw new Exception("Removed titlebar icon is still present");
        var icon = (FrameworkElement)window.FindName("MaximizeButton");
        if (CaptionMessage(hwnd, 0x84, 0, CaptionPoint(icon.PointToScreen(new Point(20, 15)))) != 1)
            throw new Exception("Caption controls must remain clickable client elements");
        if (CaptionMessage(hwnd, 0x84, 0, CaptionPoint(window.PointToScreen(new Point(2, 2)))) != 13)
            throw new Exception("Window corner must retain native diagonal resize hit testing");
        Capture(window, Path.Combine(output, "titlebar-wide.png"));

        window.Width = window.MinWidth;
        await SettleAsync();
        if (menu.IsVisible || !compact.IsVisible) throw new Exception("Narrow caption did not collapse to logo + menu button");
        CheckBounds();
        compact.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var popup = (Popup)Field(window, "_compactMenu")!;
        var panel = (CompactMenuPanel)popup.Child;
        if (!popup.IsOpen || !menu.Items.OfType<MenuItem>().SequenceEqual(menus))
            throw new Exception("Compact menu must reuse all live menu commands");
        var view = menus[2];
        panel.NavigateTo(view);
        await SettleAsync();
        var workspaceMenu = (MenuItem)window.FindName("WorkspaceVisibilityMenuItem");
        var previous = workspaceMenu.IsChecked;
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, workspaceMenu)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (workspaceMenu.IsChecked == previous) throw new Exception("Compact View menu command did not execute");
        compact.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); panel.NavigateTo(view); await SettleAsync();
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, workspaceMenu)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke(window, "HideMainMenu", true);
        window.Width = 100;
        await SettleAsync();
        if (window.ActualWidth < window.MinWidth - 1) throw new Exception("Native minimum width was not enforced");
        CheckBounds();
        Capture(window, Path.Combine(output, "titlebar-minimum.png"));
        Invoke(window, "ProcessMenuKeyDown", Key.LeftAlt, ModifierKeys.Alt, false);
        Invoke(window, "ProcessMenuKeyUp", Key.LeftAlt, ModifierKeys.None);
        await SettleAsync();
        if (!popup.IsOpen) throw new Exception("Alt must open the compact menu");
        var escape = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent);
        panel.RaiseEvent(escape);
        await SettleAsync();
        if (!escape.Handled || popup.IsOpen) throw new Exception("Compact popup must route Escape to the window and return focus");
        Invoke(window, "ProcessMenuKeyDown", Key.V, ModifierKeys.Alt, false);
        await SettleAsync();
        if (!popup.IsOpen || panel.CurrentMenu != view) throw new Exception("Alt+V must open compact View menu");
        var themes = (MenuItem)window.FindName("ThemeMenu");
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, themes)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        if (panel.Depth != 2 || panel.CurrentMenu != themes || themes.IsSubmenuOpen)
            throw new Exception("Third compact menu level must replace the panel without a cascading popup");
        CaptureOutline(panel, Path.Combine(output, "compact-third-level.png"));
        foreach (var expectedDepth in new[] { 1, 0 })
        {
            panel.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent)); await SettleAsync();
            if (!popup.IsOpen || panel.Depth != expectedDepth) throw new Exception("Escape must return exactly one compact menu level");
        }
        panel.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent)); await SettleAsync();
        if (popup.IsOpen) throw new Exception("Escape at root must close the compact menu");
        compact.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); panel.NavigateTo(view); await SettleAsync();
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, themes)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
        var lightTheme = themes.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "github-light-default"));
        Descendants<Button>(panel).Single(b => ReferenceEquals(b.Tag, lightTheme)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
        if (popup.IsOpen || ThemeManager.Current.Id != "github-light-default") throw new Exception("Compact third-level theme command did not execute and dismiss the menu");
        Invoke(window, "SelectTheme", originalTheme, true);
        var savedHeight = window.Height; window.Height = window.MinHeight; await SettleAsync();
        compact.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); panel.NavigateTo(menus[0]); await SettleAsync();
        var menuScroll = Descendants<ScrollViewer>(panel).Single();
        if (menuScroll.ScrollableHeight <= 1) throw new Exception("Crowded compact File menu must scroll");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        menuScroll.RaiseEvent(wheel); await SettleAsync();
        if (!wheel.Handled || menuScroll.VerticalOffset <= 1) throw new Exception("Mouse wheel did not scroll compact menu down");
        CaptureOutline(panel, Path.Combine(output, "compact-scrolled.png"));
        menuScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent }); await SettleAsync();
        if (menuScroll.VerticalOffset > 1) throw new Exception("Mouse wheel did not scroll compact menu up");
        // A real preview input route on background content must close every level.
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        await SettleAsync(); if (popup.IsOpen) throw new Exception("Outside click did not close all compact menu levels");
        window.Height = savedHeight;

        foreach (var width in new[] { 1100d, 280, 900, 340, 1440 })
        {
            window.Width = width; await SettleAsync(); CheckBounds();
        }
        if (popup.IsOpen || !menu.Items.OfType<MenuItem>().SequenceEqual(menus) || !ReferenceEquals(document, editor.Document))
            throw new Exception("Resize must close popups and retain menu instances and editor document");
        foreach (var theme in new[] { "github-light-default", "ristretto" })
        {
            ThemeManager.Apply(theme); await SettleAsync();
            if (((SolidColorBrush)((Border)title).Background).Color != ThemeManager.Brush("SidebarBackground").Color)
                throw new Exception("Title bar did not follow theme colors");
            Capture(window, Path.Combine(output, "titlebar-" + theme + ".png"));
        }
        ThemeManager.Apply(originalTheme);
        ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.WindowState == WindowState.Maximized, "Caption maximize did not work");
        await SettleAsync(); CheckBounds(); CheckWindowBottom(window, false);
        Capture(window, Path.Combine(output, "titlebar-maximized.png"));
        await CheckNativeWindowMenuAsync(window, title, menu, compact);
        ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal, "Caption restore did not work");
        await SettleAsync();
        await CheckNativeWindowMenuAsync(window, title, menu, compact);
        ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.WindowState == WindowState.Minimized, "Caption minimize did not work");
        SystemCommands.RestoreWindow(window);
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal, "Window did not restore after minimizing");
        await SettleAsync();
        if (!menu.IsVisible) throw new Exception("Restoring minimized window hid the persistent menu");
        captionScreen = title.PointToScreen(new Point(title.ActualWidth - 180, 20));
        CaptionMessage(hwnd, 0xA3, 2, CaptionPoint(captionScreen));
        await WaitUntilAsync(() => window.WindowState == WindowState.Maximized, "Native caption double-click did not maximize");
        SystemCommands.RestoreWindow(window); await SettleAsync();
        await CheckFullscreenAsync(window, output);
        Console.WriteLine("PASS: themed title bar, compact third-level commands, wheel scrolling, per-level Escape/outside dismissal, Alt shortcuts, minimum width and native window controls");
    }
}
