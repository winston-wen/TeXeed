using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private delegate bool PreviewSettingsWindowEnumerator(nint handle, nint state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, PreviewSettingsWindowEnumerator callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    private static async Task CheckZoomWithMissingSyncAsync(MainWindow window, string output)
    {
        var missing = Path.Combine(output, "missing-synctex.exe");
        if (File.Exists(missing)) throw new Exception("The missing SyncTeX fixture already exists");
        var field = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (AppSettings)field.GetValue(window)!;
        field.SetValue(window, original with { SynctexPath = missing, LivePreview = false });
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(output, "zoom-missing-synctex.pdf"); WriteThemePdf(pdf); await preview.LoadAsync(pdf);
        var dismissed = false; var thread = GetCurrentThreadId();
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        watchdog.Tick += (_, _) =>
        {
            EnumThreadWindows(thread, (handle, _) =>
            {
                var title = new StringBuilder(128); GetWindowText(handle, title, title.Capacity);
                if (title.ToString() == "检查设置") { dismissed = true; PostMessage(handle, 0x0010, 0, 0); }
                return true;
            }, 0);
        };
        watchdog.Start();
        try
        {
            await CheckZoomControlsAsync(window, output);
            if (dismissed) throw new Exception("Zoom regression tried to save the intentionally missing SyncTeX path");
            if (((AppSettings)field.GetValue(window)!).SynctexPath != missing)
                throw new Exception("Zoom regression changed the intentionally missing SyncTeX fixture");
        }
        finally { watchdog.Stop(); field.SetValue(window, original); }
        Console.WriteLine("PASS: zoom controls cancel with an intentionally missing SyncTeX executable; no blocking validation dialog");
    }
}
