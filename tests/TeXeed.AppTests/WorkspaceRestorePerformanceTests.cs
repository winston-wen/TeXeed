using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckWorkspaceRestorePerformanceAsync(MainWindow window, string output, bool normalizeRestore)
    {
        var root = Directory.GetCurrentDirectory();
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "workspace-performance-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex"); var other = Path.Combine(output, "other.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 1; page <= 8; page++)
        {
            if (page > 1) lines.Add("\\newpage");
            for (var row = 1; row <= 40; row++) lines.Add($"\\noindent Page {page}, row {row:00}. Restore state.\\par");
        }
        lines.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', lines) + "\n");
        File.WriteAllText(other, "\\documentclass{article}\n\\begin{document}\nOther project.\n\\end{document}\n");
        var settings = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        settings.SetValue(window, new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.None, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(fixture, "absent.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync"); await WaitForPreviewRenderedAsync(window);
        var preview = (PdfPreview)window.FindName("Preview"); var editor = (LatexEditor)window.FindName("Editor");
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        if (preview.PageCount != 8) throw new Exception("Restore performance fixture did not produce eight pages");
        var samples = new List<object>();
        for (var run = 0; run < 9; run++)
        {
            editor.CaretOffset = editor.Document.GetLineByNumber(normalizeRestore ? 1 : 180).Offset;
            editor.ScrollToVerticalOffset(normalizeRestore ? 0 : 160 * editor.TextArea.TextView.DefaultLineHeight);
            scroll.ScrollToVerticalOffset(normalizeRestore ? 0 : scroll.ScrollableHeight * .6);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            await CallAsync(window, "OpenAsync", other);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            await CallAsync(window, "OpenAsync", main);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            var openMs = watch.Elapsed.TotalMilliseconds;
            await WaitForPreviewRenderedAsync(window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            watch.Stop(); var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (run >= 2) samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, openMs, renderMs = watch.Elapsed.TotalMilliseconds - openMs, allocatedBytes = bytes });
        }
        var controls = Descendants<Control>(window).Where(c => c.IsVisible).ToArray();
        var visible = controls.Count(c => !VisibleBounds(c, window).IsEmpty);
        Capture(window, Path.Combine(output, "workspace-performance.png"));
        var result = new { fixture = "reopen cached 8-page, 320-row project after switching away; editor and preview positions set before switch", normalizeRestore, samples, visibleControls = visible, createdVisibleControls = controls.Length };
        File.WriteAllText(Path.Combine(output, "workspace-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
