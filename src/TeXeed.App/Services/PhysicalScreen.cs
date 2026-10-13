using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TeXeed.App.Services;

internal readonly record struct PhysicalScreenMetrics(double DipsPerCentimeter, bool IsEstimated, string Device)
{
    public static PhysicalScreenMetrics Estimated => new(96 / 2.54, true, "");
}

/// <summary>Convert panel centimeters to this HWND's DIPs, independently of Windows UI scaling.</summary>
internal static class PhysicalScreen
{
    private static readonly Dictionary<string, Size?> PanelSizes = new(StringComparer.OrdinalIgnoreCase);
    public static void Invalidate() => PanelSizes.Clear();

    public static PhysicalScreenMetrics Measure(FrameworkElement view)
    {
        if (PresentationSource.FromVisual(view) is not HwndSource) return PhysicalScreenMetrics.Estimated;
        var center = view.PointToScreen(new Point(view.ActualWidth / 2, view.ActualHeight / 2));
        var monitor = MonitorFromPoint(new NativePoint { X = (int)center.X, Y = (int)center.Y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return PhysicalScreenMetrics.Estimated;
        if (!PanelSizes.TryGetValue(info.Device, out var panel))
        {
            panel = ReadPanelSize(info.Device);
            PanelSizes[info.Device] = panel;
        }
        var mode = new DeviceMode { Size = (ushort)Marshal.SizeOf<DeviceMode>() };
        var rotated = EnumDisplaySettings(info.Device, -1, ref mode) && mode.Orientation is 1 or 3;
        var centimeters = panel is { } size ? (rotated ? size.Height : size.Width) / 10 : 0;
        return FromDimensions(info.Monitor.Right - info.Monitor.Left, centimeters,
            VisualTreeHelper.GetDpi(view).DpiScaleX, info.Device);
    }

    internal static PhysicalScreenMetrics FromDimensions(int pixels, double centimeters, double dpiScale, string device = "")
    {
        if (pixels <= 0 || !double.IsFinite(centimeters) || centimeters <= 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
            return PhysicalScreenMetrics.Estimated with { Device = device };
        return new(pixels / centimeters / dpiScale, false, device);
    }

    private static Size? ReadPanelSize(string device)
    {
        for (uint index = 0; ; index++)
        {
            var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(device, index, ref display, 1)) return null; // EDD_GET_DEVICE_INTERFACE_NAME
            if ((display.Flags & 1) == 0) continue;
            var pieces = display.Id.Split('#');
            if (pieces.Length < 3 || !pieces[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
                || pieces[1].Contains('\\') || pieces[2].Contains('\\')) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{pieces[1]}\{pieces[2]}\Device Parameters");
                if (key?.GetValue("EDID") is byte[] bytes && ParseEdid(bytes) is { } size) return size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }
    }

    internal static Size? ParseEdid(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> header = [0, 255, 255, 255, 255, 255, 255, 0];
        if (bytes.Length < 128 || !bytes[..8].SequenceEqual(header)) return null;
        var checksum = 0;
        foreach (var value in bytes[..128]) checksum += value;
        if ((checksum & 255) != 0) return null;
        // Detailed timing descriptors give millimeters; the basic block is rounded to whole centimeters.
        for (var offset = 54; offset + 18 <= 126; offset += 18)
        {
            if (bytes[offset] == 0 && bytes[offset + 1] == 0) continue;
            var width = bytes[offset + 12] | ((bytes[offset + 14] & 0xf0) << 4);
            var height = bytes[offset + 13] | ((bytes[offset + 14] & 0x0f) << 8);
            if (Plausible(width, height)) return new Size(width, height);
        }
        var basicWidth = bytes[21] * 10; var basicHeight = bytes[22] * 10;
        return Plausible(basicWidth, basicHeight) ? new Size(basicWidth, basicHeight) : null;
    }
    private static bool Plausible(int width, int height) => width is >= 30 and <= 4000 && height is >= 30 and <= 4000;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DeviceMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(84)] public uint Orientation;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DeviceMode mode);
}
