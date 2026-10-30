using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPreviewPerformanceAsync(MainWindow window, string root, string output)
    {
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "preview-performance-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 1; page <= 8; page++)
        {
            if (page > 1) lines.Add("\\newpage");
            if (page == 4) lines.Add("\\begin{verbatim}");
            if (page == 6) lines.Add("\\begin{picture}(300,500)");
            for (var row = 1; row <= 40; row++)
                lines.Add(page == 4 ? $"code line {row:00}: perform operation" : page == 6
                    ? $"\\put(10,{500 - row * 12}){{\\makebox(0,0)[l]{{Flow step {row:00}}}}}"
                    : $"\\noindent Page {page}, row {row:00}. View synchronization.\\par");
            if (page == 4) lines.Add("\\end{verbatim}");
            if (page == 6) lines.Add("\\end{picture}");
        }
        lines.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', lines) + "\n");
        var settingsField = window.GetType().GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        settingsField.SetValue(window, new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.Viewport, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(fixture, "missing-synctex.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
        var editor = (LatexEditor)window.FindName("Editor"); var preview = (PdfPreview)window.FindName("Preview");
        if (preview.PageCount != 8) throw new Exception("Performance fixture did not produce eight pages");
        await WaitForPreviewRenderedAsync(window);
        var timer = (DispatcherTimer)Field(window, "_syncTimer")!;
        var samples = new List<object>();
        for (var i = 0; i < 9; i++)
        {
            // Alternate fixed positions; force the same forward operation without the 150 ms throttle.
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            editor.ScrollToVerticalOffset(((i % 2 == 0 ? 145 : 225) - 1) * editor.TextArea.TextView.DefaultLineHeight);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); timer.Stop();
            await CallAsync(window, "ForwardAsync", false);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            await WaitForPreviewRenderedAsync(window);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            watch.Stop(); var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (i >= 2) samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
        }
        var controls = Descendants<Control>(window).Where(c => c.IsVisible).ToArray();
        var visible = controls.Count(c => !VisibleBounds(c, window).IsEmpty);
        Capture(window, Path.Combine(output, "preview-performance.png"));
        var result = new { fixture = "8 pages, 320 source rows including verbatim and TeX picture; viewport scroll and immediate forward", samples, visibleControls = visible, createdVisibleControls = controls.Length };
        File.WriteAllText(Path.Combine(output, "preview-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
