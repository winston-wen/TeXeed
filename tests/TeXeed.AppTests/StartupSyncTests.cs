using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckStartupSyncAsync(MainWindow window, string main, string output)
    {
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed", "settings.json");
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))! with { LivePreview = false, FollowMode = PreviewFollowMode.Viewport };
        window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, settings);
        await CallAsync(window, "OpenAsync", main);
        // Deliberately navigate before waiting for the opening cover raster.
        var project = (TexProject)Field(window, "_project")!;
        var chapter = project.Files.Single(f => f.Name == "02.tex");
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        var pages = (Canvas)Field(preview, "_pages")!;
        Invoke(window, "ActivateFile", chapter.Path);
        editor.ScrollToVerticalOffset(0);
        window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var watch = Stopwatch.StartNew();
        await CallAsync(window, "ForwardAsync", false);
        var syncMs = watch.Elapsed.TotalMilliseconds;
        if (syncMs > 500) throw new Exception($"Prepared first chapter synchronization took {syncMs:F0}ms");
        var point = await ((SyncTexService)Field(window, "_sync")!).ForwardViewportAsync(project, (string)Field(window, "_pdfPath")!, new(chapter.Path, 1, 1), settings)
            ?? throw new Exception("Chapter has no prepared mapping");
        var layout = ((IList)Field(preview, "_layouts")!)[point.Page - 1]!;
        var top = (double)layout.GetType().GetProperty("Top")!.GetValue(layout)!;
        if (Math.Abs(pages.TranslatePoint(new Point(0, top + 1), scroll).Y) > 2) throw new Exception("First chapter follow did not apply immediately");
        await WaitForPreviewRenderedAsync(window);
        var renderMs = watch.Elapsed.TotalMilliseconds;
        var visual = ((IDictionary)Field(preview, "_visuals")!)[point.Page - 1]!;
        var image = (Image)visual.GetType().GetProperty("Image")!.GetValue(visual)!;
        if (image.Source is not BitmapSource) throw new Exception("Requested chapter raster was not displayed");
        Capture(window, Path.Combine(output, "first-chapter-synchronized.png"));
        Console.WriteLine($"PASS: first chapter sync {syncMs:F1}ms, latest chapter fully rendered {renderMs:F0}ms after startup navigation");
        var firstVersion = (int)Field(preview, "_renderVersion")!;
        preview.ShowLocation(point with { Page = point.Page + 1 }, highlight: false);
        if ((int)Field(preview, "_renderVersion")! <= firstVersion) throw new Exception("New synchronization did not retire obsolete viewport raster requests");
        await WaitForPreviewRenderedAsync(window);
    }
}
