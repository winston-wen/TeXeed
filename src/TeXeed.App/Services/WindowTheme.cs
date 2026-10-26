using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TeXeed.App.Services;

internal static class WindowTheme
{
    internal static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window);
    internal static void Apply(Window window)
    {
        // Unsupported Windows versions keep their native caption; client rendering is unaffected.
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var enabled = ThemeManager.Current.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
