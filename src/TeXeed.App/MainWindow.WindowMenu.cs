using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TeXeed.App;

public partial class MainWindow
{
    private HwndSource? _captionSource;
    private void InitializeWindowMenu()
    {
        SourceInitialized += (_, _) =>
        {
            _captionSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _captionSource?.AddHook(WindowMenuHook);
        };
        Closed += (_, _) => { _captionSource?.RemoveHook(WindowMenuHook); _captionSource = null; };
    }
    private bool IsWindowMenuPoint(Point screen)
    {
        var local = TitleBar.PointFromScreen(screen);
        if (!new Rect(TitleBar.RenderSize).Contains(local)) return false;
        foreach (var control in new FrameworkElement[] { MainMenu, CompactMenuButton })
        {
            if (!control.IsVisible) continue;
            var bounds = new Rect(control.TranslatePoint(new Point(), TitleBar), control.RenderSize);
            if (bounds.Contains(local)) return false;
        }
        return true;
    }
    private void TitleBar_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var screen = TitleBar.PointToScreen(e.GetPosition(TitleBar));
        if (!IsWindowMenuPoint(screen)) return;
        e.Handled = true;
        ShowWindowMenuAt(screen);
    }
    private nint WindowMenuHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Caption areas use non-client messages, whereas the logo and window
        // buttons route WPF right clicks. Consume WindowChrome's default menu
        // invocation so both paths use the same physical screen coordinates.
        if (message != 0xA5 || _closing || _allowClose) return 0; // WM_NCRBUTTONUP
        var screen = new Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));
        if (!IsWindowMenuPoint(screen)) return 0;
        handled = true;
        ShowWindowMenuAt(screen);
        return 0;
    }
    private void ShowWindowMenuAt(Point screen)
    {
        if (_closing || _allowClose || !IsWindowMenuPoint(screen)) return;
        CloseTitleMenus();
        var hwnd = new WindowInteropHelper(this).Handle;
        // PointToScreen / WM_NCRBUTTONUP already provide device pixels. Calling
        // WPF ShowSystemMenu would scale them again on high-DPI displays.
        var selected = TrackWindowMenu(GetSystemMenu(hwnd, false), 0x102,
            (int)Math.Round(screen.X), (int)Math.Round(screen.Y), hwnd, 0); // LEFT/TOP + RIGHTBUTTON + RETURNCMD
        if (selected != 0) PostWindowMessage(hwnd, 0x112, (nint)selected, 0); // WM_SYSCOMMAND
    }
    private static double WindowMenuSpaceBelow(Point screen)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = (int)screen.X, Y = (int)screen.Y }, 2);
        var info = new MonitorBounds { Size = Marshal.SizeOf<MonitorBounds>() };
        return GetMonitorInfo(monitor, ref info) ? Math.Max(1, info.Work.Bottom - screen.Y) : SystemParameters.WorkArea.Height;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorBounds { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx")] private static extern uint TrackWindowMenu(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostWindowMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorBounds info);
}
