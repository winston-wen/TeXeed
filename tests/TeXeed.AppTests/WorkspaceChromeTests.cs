using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TeXeed.App;

internal static partial class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint security);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DesktopStartup
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedData, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DesktopProcess
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string executable, StringBuilder command, nint processSecurity,
        nint threadSecurity, bool inheritHandles, uint creationFlags, nint environment,
        string? directory, ref DesktopStartup startup, out DesktopProcess child);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private static Task CheckWorkspaceChromeAsync(string output) =>
        RunOnInputDesktopAsync("workspace chrome", "--workspace-chrome-native", Path.Combine(output, "workspace-chrome"));

    private static async Task RunOnInputDesktopAsync(string name, string option, string output)
    {
        // WPF discards native mouse messages when another application or the lock screen
        // covers its HWND. Give the fixture its own desktop without switching the display.
        var desktopName = $"TeXeedInput{Guid.NewGuid():N}";
        var desktop = CreateDesktop(desktopName, 0, 0, 0, 0x1ff, 0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var fixture = Directory.CreateDirectory(output).FullName;
            var executable = Path.Combine(AppContext.BaseDirectory, "TeXeed.AppTests.exe");
            var command = new StringBuilder($"\"{executable}\" {option} --output \"{fixture}\"");
            var startup = new DesktopStartup { Size = (uint)Marshal.SizeOf<DesktopStartup>(), Desktop = desktopName };
            const uint createNoWindow = 0x08000000;
            if (!CreateProcess(executable, command, 0, 0, false, createNoWindow, 0, null, ref startup, out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                using var process = Process.GetProcessById(child.ProcessId);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
                var log = Path.Combine(fixture, "input.log");
                if (File.Exists(log)) Console.Write(File.ReadAllText(log));
                if (process.ExitCode != 0) throw new Exception($"{name} fixture exited with code {process.ExitCode}");
            }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
        }
        finally { CloseDesktop(desktop); }
    }

    private static async Task ExerciseWorkspaceChromeAsync(MainWindow window, string output)
    {
        var root = Path.Combine(output, "sidebar-order-" + Guid.NewGuid().ToString("N")[..8]);
        string CreateProject(string name)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var main = Path.Combine(dir, "main.tex");
            File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}Test\\end{document}\n");
            return main;
        }
        var first = CreateProject("A"); var second = CreateProject("B");
        var panel = (Panel)window.FindName("WorkspaceIconBar");
        var hwnd = new WindowInteropHelper(window).Handle;
        string[] Order() => panel.Children.Cast<Button>().Select(b => (string)b.Tag).ToArray();
        async Task SendMouse(uint message, Point point, int buttons = 0)
        {
            var device = PresentationSource.FromVisual(window)!.CompositionTarget.TransformToDevice.Transform(point);
            if (!PostMessage(hwnd, message, buttons, CaptionPoint(device))) throw new Exception("Window mouse input could not be queued");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        Point Center(FrameworkElement e) => e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), window);
        async Task Drag(int from, int to)
        {
            var start = Center((Button)panel.Children[from]); var end = Center((Button)panel.Children[to]);
            var keys = new byte[256];
            if (!GetKeyboardState(keys)) throw new Exception("Cannot obtain input state for sidebar drag");
            var pressed = (byte[])keys.Clone(); pressed[1] = 0x80;
            SetCapture(hwnd);
            try
            {
                await SendMouse(0x0200, start);
                SetKeyboardState(pressed);
                await SendMouse(0x0201, start, 1);
                await SendMouse(0x0200, end, 1);
                SetKeyboardState(keys);
                await SendMouse(0x0202, end);
            }
            finally { SetKeyboardState(keys); ReleaseCapture(); }
        }
        async Task Open(string path)
        {
            await CallAsync(window, "OpenAsync", path); window.UpdateLayout();
        }
        var normal = new[] { "outline", "files", "search", "bookmarks", "labels", "todos" };
        await Open(first);
        if (!Order().SequenceEqual(normal)) throw new Exception("New project did not use the default sidebar order");
        var active = Field(window, "_sidebar"); var expanded = Field(window, "_workspaceExpanded");
        await Drag(5, 0);
        var orderA = new[] { "todos", "outline", "files", "search", "bookmarks", "labels" };
        if (!Order().SequenceEqual(orderA) || !Equals(active, Field(window, "_sidebar")) || !Equals(expanded, Field(window, "_workspaceExpanded")))
            throw new Exception("Sidebar drag failed to reorder or inadvertently activated its button: " + string.Join(",", Order()));
        await Open(second);
        if (!Order().SequenceEqual(normal)) throw new Exception("Previous project's sidebar order leaked into a new project");
        await Drag(0, 3);
        var orderB = new[] { "files", "search", "bookmarks", "outline", "labels", "todos" };
        if (!Order().SequenceEqual(orderB)) throw new Exception("Downward sidebar drag failed");
        await Open(first);
        if (!Order().SequenceEqual(orderA)) throw new Exception("First project lost its sidebar order after switching");
        await Open(second);
        if (!Order().SequenceEqual(orderB)) throw new Exception("Second project lost its sidebar order after switching");
        Capture(window, Path.Combine(output, "sidebar-reordered.png"));

        var title = (FrameworkElement)window.FindName("TitleBar");
        var f11 = CreateKey(window, Key.F11, Keyboard.PreviewKeyDownEvent);
        await CallAsync(window, "HandleShortcutAsync", f11, ModifierKeys.None);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (title.IsVisible) throw new Exception("Fullscreen menu started open");
        var menu = (Menu)window.FindName("MainMenu");
        var view = (MenuItem)menu.Items[2];
        SetCapture(hwnd);
        try
        {
            await SendMouse(0x0200, new Point(window.ActualWidth / 2, 8));
            if (title.IsVisible) throw new Exception("Menu opened away from the top edge");
            await SendMouse(0x0200, new Point(window.ActualWidth / 2, 1));
        }
        finally { ReleaseCapture(); }
        if (!title.IsVisible) throw new Exception("Hovering at the top edge did not reveal the menu");
        if (menu.IsVisible)
        {
            var at = Center(view);
            SetCapture(hwnd);
            await SendMouse(0x0200, at);
            await SendMouse(0x0201, at, 1);
            await SendMouse(0x0202, at);
            if (!view.IsSubmenuOpen) throw new Exception("The hover-revealed View menu did not open on click");
        }
        else if (!((System.Windows.Controls.Primitives.Popup)Field(window, "_compactMenu")!).IsOpen)
            throw new Exception("The hover-revealed compact menu did not open");
        Capture(window, Path.Combine(output, "fullscreen-top-menu.png"));
        Invoke(window, "CloseTitleMenus");
        ReleaseCapture();
        window.RaiseEvent(CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent));
        if (title.IsVisible) throw new Exception("Escape did not dismiss the hover-revealed title bar");
        window.RaiseEvent(CreateKey(window, Key.LeftAlt, Keyboard.PreviewKeyDownEvent));
        window.RaiseEvent(CreateKey(window, Key.LeftAlt, Keyboard.PreviewKeyUpEvent));
        if (!title.IsVisible) throw new Exception("Alt stopped revealing the title bar after hover dismissal");
        await CallAsync(window, "HandleShortcutAsync", CreateKey(window, Key.F11, Keyboard.PreviewKeyDownEvent), ModifierKeys.None);
        Console.WriteLine("PASS: top-edge hover and clickable fullscreen menu, Escape/Alt, bidirectional native sidebar drags and separate project order after reopening");
    }
}
