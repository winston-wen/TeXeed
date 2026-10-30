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
    private static async Task CheckExternalGraphicPerformanceAsync(MainWindow window, string output)
    {
        var (root, source, pdfSource, main) = await PrepareGraphicFixtureAsync(output);
        var plain = Path.Combine(root, "plain.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 1; page <= 8; page++)
        {
            if (page > 1) lines.Add("\\newpage");
            for (var row = 1; row <= 40; row++) lines.Add($"\\noindent Page {page}, row {row:00}. Follow.\\par");
        }
        lines.Add("\\end{document}"); File.WriteAllText(plain, string.Join('\n', lines) + "\n");
        var field = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(window, new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.None, Engine = TexEngine.XeLaTeX, SynctexPath = Path.Combine(root, "missing.exe") });
        var sync = (SyncTexService)Field(window, "_sync")!; var settings = (AppSettings)field.GetValue(window)!;
        var preview = (PdfPreview)window.FindName("Preview");
        var scenarios = new Dictionary<string, object>();
        async Task Measure(string name, TexProject project, string pdf, SourceLocation[] targets)
        {
            await sync.PrepareAsync(pdf);
            var probe = await sync.ForwardViewportAsync(project, pdf, targets[0], settings);
            if (probe is null) { scenarios[name] = new { supported = false }; return; }
            var samples = new List<object>();
            for (var run = 0; run < 9; run++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                var location = await sync.ForwardViewportAsync(project, pdf, targets[run % targets.Length], settings);
                if (location is null) throw new Exception(name + " target mapping disappeared during performance run");
                preview.ShowLocation(location, highlight: false, viewportFraction: .5);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                await WaitForPreviewRenderedAsync(window);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                watch.Stop(); var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (run >= 2) samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
            }
            var controls = Descendants<Control>(window).Where(c => c.IsVisible).ToArray();
            scenarios[name] = new { supported = true, samples, visibleControls = controls.Count(c => !VisibleBounds(c, window).IsEmpty), createdVisibleControls = controls.Length };
        }
        await CallAsync(window, "OpenAsync", plain); await CallAsync(window, "BuildAsync"); await WaitForPreviewRenderedAsync(window);
        var project = (TexProject)Field(window, "_project")!; var pdf = (string)Field(window, "_pdfPath")!;
        await Measure("plain", project, pdf, [new(plain, 30), new(plain, 220)]);
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync"); await WaitForPreviewRenderedAsync(window);
        project = (TexProject)Field(window, "_project")!; pdf = (string)Field(window, "_pdfPath")!;
        var files = project.Files.ToList();
        foreach (var path in new[] { source, pdfSource })
            if (!files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) files.Add(new(path, Path.GetFileName(path), ProjectFileKind.Tex, File.ReadAllText(path)));
        project = project with { Files = files };
        await Measure("main-with-graphics", project, pdf, [new(main, 4), new(main, 7)]);
        await Measure("eps-source", project, pdf, [new(source, 5), new(source, 7)]);
        await Measure("pdf-page-2-source", project, pdf, [new(pdfSource, 5), new(pdfSource, 7)]);
        Capture(window, Path.Combine(output, "graphic-performance.png"));
        var result = new { fixture = "plain 8-page document and 2-page document with one generated EPS and page 2 of one generated PDF; alternate forward targets and render", scenarios };
        File.WriteAllText(Path.Combine(output, "graphic-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
