using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
            var controls = new[] { "AppIconButton", "CompactMenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" };
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
        var icon = (FrameworkElement)window.FindName("AppIconButton");
        if (CaptionMessage(hwnd, 0x84, 0, CaptionPoint(icon.PointToScreen(new Point(20, 20)))) != 1)
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
        var popup = compact.ContextMenu!;
        if (!popup.IsOpen || !popup.Items.OfType<MenuItem>().SequenceEqual(menus))
            throw new Exception("Compact menu must reuse all live menu commands");
        var view = menus[2];
        view.IsSubmenuOpen = true;
        await SettleAsync();
        var workspaceMenu = (MenuItem)window.FindName("WorkspaceVisibilityMenuItem");
        var previous = workspaceMenu.IsChecked;
        workspaceMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (workspaceMenu.IsChecked == previous) throw new Exception("Compact View menu command did not execute");
        workspaceMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
        popup.RaiseEvent(escape);
        await SettleAsync();
        if (!escape.Handled || popup.IsOpen) throw new Exception("Compact popup must route Escape to the window and return focus");
        Invoke(window, "ProcessMenuKeyDown", Key.V, ModifierKeys.Alt, false);
        await SettleAsync();
        if (!popup.IsOpen || !view.IsSubmenuOpen) throw new Exception("Alt+V must open compact View menu");

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
        await SettleAsync(); CheckBounds();
        Capture(window, Path.Combine(output, "titlebar-maximized.png"));
        ((Button)window.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal, "Caption restore did not work");
        ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.WindowState == WindowState.Minimized, "Caption minimize did not work");
        SystemCommands.RestoreWindow(window);
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal, "Window did not restore after minimizing");
        await SettleAsync();
        captionScreen = title.PointToScreen(new Point(title.ActualWidth - 180, 20));
        CaptionMessage(hwnd, 0xA3, 2, CaptionPoint(captionScreen));
        await WaitUntilAsync(() => window.WindowState == WindowState.Maximized, "Native caption double-click did not maximize");
        SystemCommands.RestoreWindow(window); await SettleAsync();
        Console.WriteLine("PASS: themed native title bar, caption hit tests, compact live menus/Alt shortcuts, minimum width, resizing, maximize/restore/minimize and double-click");
    }
}
