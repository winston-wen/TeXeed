using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
            var executable = Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".exe");
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
                if (System.IO.File.Exists(log)) Console.Write(System.IO.File.ReadAllText(log));
                if (process.ExitCode != 0) throw new Exception($"{name} fixture exited with code {process.ExitCode}");
            }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
        }
        finally { CloseDesktop(desktop); }
    }
}
