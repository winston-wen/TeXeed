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
    private static async Task CheckSidebarPerformanceAsync(MainWindow window, string path, string output)
    {
        var results = new Dictionary<string, double>();
        var watch = Stopwatch.StartNew();
        var project = await new ProjectService().LoadAsync(path);
        results["discoveryMs"] = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"PERF discovery={watch.Elapsed.TotalMilliseconds:F1}ms files={project.Files.Count} chars={project.Files.Sum(file => file.Content.Length)}");
        watch.Restart(); var index = LatexIndexer.Build(project.Files);
        results["indexMs"] = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"PERF index={watch.Elapsed.TotalMilliseconds:F1}ms headings={index.Outline.Count} labels={index.Labels.Count} bookmarks={index.Bookmarks.Count} todos={index.Todos.Count}");
        watch.Restart(); await CallAsync(window, "OpenAsync", path);
        results["openMs"] = watch.Elapsed.TotalMilliseconds;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
        results["firstFrameMs"] = watch.Elapsed.TotalMilliseconds;
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        results["outlineContainers"] = Descendants<TreeViewItem>(outline).Count();
        Console.WriteLine($"PERF open={results["openMs"]:F1}ms firstFrame={results["firstFrameMs"]:F1}ms containers={results["outlineContainers"]}");
        Capture(window, Path.Combine(output, "book-initial.png"));
        var icons = ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().ToArray();
        foreach (var mode in new[] { "files", "labels", "outline", "files", "outline", "bookmarks" })
        {
            watch.Restart(); icons.Single(button => Equals(button.Tag, mode)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sync = watch.Elapsed.TotalMilliseconds;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
            var key = mode + "-" + results.Keys.Count(key => key.StartsWith(mode + "-"));
            results[key] = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF switch={mode} work={sync:F1}ms frame={watch.Elapsed.TotalMilliseconds:F1}ms");
            if (mode == "files")
            {
                Console.WriteLine($"PERF file tree containers={Descendants<TreeViewItem>((FrameworkElement)window.FindName("FileTree")).Count()}");
                Capture(window, Path.Combine(output, key + ".png"));
            }
            if (mode == "bookmarks") Capture(window, Path.Combine(output, "bookmarks-book.png"));
        }
        watch.Restart(); Invoke(window, "RefreshIndex");
        var refresh = watch.Elapsed.TotalMilliseconds;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
        results["unchangedRefreshMs"] = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"PERF unchanged refresh work={refresh:F1}ms frame={watch.Elapsed.TotalMilliseconds:F1}ms");
        var cache = (ProjectIndexCache)Field(window, "_sidebarIndex")!;
        if (cache.LastParsedFileCount != 0) throw new Exception("Unchanged sidebar refresh parsed project files again");
        var editor = (LatexEditor)window.FindName("Editor");
        watch.Restart(); editor.Document.Insert(editor.Document.TextLength, "\n\\label{texeed-perf-new}\n"); Invoke(window, "RefreshIndex");
        results["editIndexMs"] = watch.Elapsed.TotalMilliseconds;
        if (cache.LastParsedFileCount != 1 || !cache.Update(CurrentProjectFiles(window)).Labels.Any(entry => entry.Title == "texeed-perf-new"))
            throw new Exception("Editing one file did not update just that file's index");
        Console.WriteLine($"PERF single-file edit={results["editIndexMs"]:F1}ms");
        editor.Undo(); Invoke(window, "RefreshIndex");
        if (cache.Update(CurrentProjectFiles(window)).Labels.Any(entry => entry.Title == "texeed-perf-new")) throw new Exception("Undo retained a stale cached label");
        if (results["files-1"] > 250 || results["unchangedRefreshMs"] > 100)
            throw new Exception("Warm sidebar latency exceeded its regression budget");
        File.WriteAllText(Path.Combine(output, "sidebar-performance.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static IEnumerable<ProjectFile> CurrentProjectFiles(MainWindow window) => (IEnumerable<ProjectFile>)Invoke(window, "CurrentFiles")!;
}
