using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckCachedBookAsync(MainWindow window, string main, string output)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed", "settings.json");
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))! with { LivePreview = true };
        window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, settings);
        var preview = (PdfPreview)window.FindName("Preview");
        var timer = (DispatcherTimer)Field(window, "_liveTimer")!;
        var watch = Stopwatch.StartNew();
        await CallAsync(window, "OpenAsync", main);
        await WaitForPreviewRenderedAsync(window);
        if (preview.PageCount != 310 || timer.IsEnabled || Field(window, "_compileTask") != null || (int)Field(window, "_pdfRevision")! != (int)Field(window, "_revision")!)
            throw new Exception("Book cache did not restore all pages without compiling");
        var restoredMs = watch.Elapsed.TotalMilliseconds;
        await Task.Delay(settings.DebounceMs + 300);
        if (timer.IsEnabled || Field(window, "_compileTask") != null || (bool)Field(window, "_building")!) throw new Exception("Book cache scheduled a delayed rebuild");
        Capture(window, Path.Combine(output, "book-cached-open.png"));
        Console.WriteLine($"PASS: 310-page book restored and rendered from cache in {restoredMs:F0}ms, with no delayed compiler");
    }
    private static async Task CheckPreviewCacheAsync(MainWindow window, string output)
    {
        const string engine = "C:/texlive/2026/bin/windows/xelatex.exe";
        if (!File.Exists(engine)) throw new Exception("Cache UI regression needs the installed TeX Live XeLaTeX");
        var folder = Path.Combine(output, "cache-project 中文"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        var content = "\\documentclass{article}\n\\begin{document}\nCached preview.\n\\end{document}\n";
        File.WriteAllText(main, content);
        var project = await new ProjectService().LoadAsync(main);
        var settings = new AppSettings { Engine = TexEngine.XeLaTeX, EnginePath = engine, LivePreview = true, DebounceMs = 400 };
        var result = await new CompileService().CompileAsync(project, settings);
        if (!result.Success) throw new Exception(result.Log);
        if (await new CompileService().GetCachedPreviewAsync(project, settings) is not { IsCurrent: true }) throw new Exception("Real TeX cache was not persisted");
        var settingsField = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        settingsField.SetValue(window, settings);
        var preview = (PdfPreview)window.FindName("Preview");
        var timer = (DispatcherTimer)Field(window, "_liveTimer")!;
        var pdfTime = File.GetLastWriteTimeUtc(result.PdfPath!);
        var watch = Stopwatch.StartNew();
        await CallAsync(window, "OpenAsync", main);
        await WaitForPreviewRenderedAsync(window);
        if (preview.PageCount != 1 || (int)Field(window, "_pdfRevision")! != (int)Field(window, "_revision")! || timer.IsEnabled || Field(window, "_compileTask") != null)
            throw new Exception("Opening a current cache did not load the PDF without scheduling a compiler");
        await Task.Delay(700);
        if (File.GetLastWriteTimeUtc(result.PdfPath!) != pdfTime || timer.IsEnabled || Field(window, "_compileTask") != null)
            throw new Exception("A deferred opening event recompiled an unchanged cached project");
        await CallAsync(window, "BuildAsync", false);
        if (File.GetLastWriteTimeUtc(result.PdfPath!) != pdfTime) throw new Exception("Unnecessary automatic build did not reuse cache");
        Console.WriteLine($"PASS: real cached PDF opened without compiler in {watch.Elapsed.TotalMilliseconds:F0}ms including rendering/debounce observation");
        Capture(window, Path.Combine(output, "cached-project-open.png"));

        // A new window simulates restarting, including live-preview being enabled.
        var reopened = new MainWindow { Width = 1100, Height = 700 };
        settingsField.SetValue(reopened, settings); reopened.Show();
        await reopened.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await CallAsync(reopened, "OpenAsync", main);
        if (((PdfPreview)reopened.FindName("Preview")).PageCount != 1 || ((DispatcherTimer)Field(reopened, "_liveTimer")!).IsEnabled)
            throw new Exception("Restarting lost the valid cache or scheduled another compile");
        await CloseAndObserveAsync(reopened, "reopened cache window"); window.Activate();

        // Even with live preview disabled, stale/legacy output is shown immediately.
        settingsField.SetValue(window, settings with { LivePreview = false });
        File.WriteAllText(main, content.Replace("Cached", "Changed"));
        await CallAsync(window, "OpenAsync", main);
        if (preview.PageCount != 1 || (int)Field(window, "_pdfRevision")! != -1 || timer.IsEnabled)
            throw new Exception("Changed sources did not keep the old preview as stale without an automatic build");
        settingsField.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", main);
        if (preview.PageCount != 1) throw new Exception("Stale cache was blanked before updating");
        await WaitUntilAsync(() => !(bool)Field(window, "_building")! && !timer.IsEnabled
            && (int)Field(window, "_pdfRevision")! == (int)Field(window, "_revision")!, "Changed source was not rebuilt in the background");
        if (File.GetLastWriteTimeUtc(result.PdfPath!) == pdfTime) throw new Exception("Changed source reused the old PDF as current");
        File.Delete(Path.Combine(folder, ".texeed", "preview", "main.texeed-cache.json"));
        settingsField.SetValue(window, settings with { LivePreview = false });
        await CallAsync(window, "OpenAsync", main);
        if (preview.PageCount != 1 || (int)Field(window, "_pdfRevision")! != -1) throw new Exception("Legacy output without a manifest did not restore its preview");
        Console.WriteLine("PASS: restart reuse, stale/legacy preview restoration, background update and automatic-build deduplication");
    }
}
