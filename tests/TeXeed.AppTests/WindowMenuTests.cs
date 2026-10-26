using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TeXeed.App;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct MenuRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out MenuRect rect);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MenuPost(nint hwnd, uint message, nint wParam, nint lParam);

    private static async Task CheckNativeWindowMenuAsync(MainWindow window, FrameworkElement title, Menu menu, Button compact)
    {
        if ((bool)Invoke(window, "IsWindowMenuPoint", menu.PointToScreen(new Point(20, 15)))!)
            throw new Exception("Alt menu area must be excluded from titlebar system menu");
        var click = title.PointToScreen(new Point(title.ActualWidth - 200, 20));
        MenuRect? observed = null;
        var hwnd = new WindowInteropHelper(window).Handle;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var deadline = DateTime.UtcNow.AddSeconds(3);
        timer.Tick += (_, _) =>
        {
            var nativeMenu = FindWindow("#32768", null);
            if (nativeMenu != 0 && GetWindowRect(nativeMenu, out var bounds)) observed = bounds;
            if (observed is not null || DateTime.UtcNow >= deadline)
            { MenuPost(hwnd, 0x1F, 0, 0); timer.Stop(); } // WM_CANCELMODE releases TrackPopupMenu's modal loop.
        };
        timer.Start();
        // Exercise the actual non-client right-button message, including hook order.
        CaptionMessage(hwnd, 0xA5, 2, CaptionPoint(click));
        timer.Stop();
        if (observed is not { } rect) throw new Exception("Native right-caption menu did not appear");
        if (Math.Abs(rect.Left - click.X) > 2 || Math.Abs(rect.Top - click.Y) > 2)
            throw new Exception($"Native menu top-left was ({rect.Left},{rect.Top}), expected physical click ({click.X:F1},{click.Y:F1})");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Console.WriteLine("PASS: excluded Alt menu area, and native system menu top-left at physical right-click coordinates");
    }
}
