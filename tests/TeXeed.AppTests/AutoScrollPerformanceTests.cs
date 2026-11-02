using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAutoScrollPerformanceAsync(MainWindow window, string output)
    {
        var root = Directory.GetCurrentDirectory();
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "auto-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 0; page < 8; page++)
        {
            if (page > 0) lines.Add("\\newpage");
            for (var row = 0; row < 40; row++) lines.Add($"\\noindent Page {page} row {row}: synthetic auto-scroll fixture.\\par");
        }
        lines.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', lines));
        window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
            new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.Viewport, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(fixture, "absent.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync"); await WaitForPreviewRenderedAsync(window);
        var preview = (PdfPreview)window.FindName("Preview");
        var pdfScroll = (ScrollViewer)Field(preview, "_scroll")!;
        var editor = (LatexEditor)window.FindName("Editor");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = window, Width = 900, Height = 760, Content = panel, Title = "Synthetic auto-scroll performance" };
        var result = new Dictionary<string, object>();
        async Task Measure(string name, object controller, ScrollViewer scroll, FrameworkElement scope)
        {
            controller.GetType().GetProperty("CapturePointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, (Func<ScrollViewer, bool>)(_ => true));
            var samples = new List<object>();
            scroll.ScrollToVerticalOffset(Math.Max(1, scroll.ScrollableHeight * .3));
            await scope.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); scope.UpdateLayout();
            for (var run = 0; run < 9; run++)
            {
                var anchor = new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
                var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                if (!(bool)Invoke(controller, "Start", anchor)!) throw new Exception(name + " could not start auto-scroll");
                Invoke(controller, "UpdatePointer", new Point(anchor.X, anchor.Y + 90));
                var original = scroll.VerticalOffset;
                for (var frame = 0; frame < 5; frame++) { Invoke(controller, "Advance", TimeSpan.FromMilliseconds(16)); scope.UpdateLayout(); }
                if (scroll.VerticalOffset <= original) throw new Exception(name + " did not scroll");
                Invoke(controller, "Stop");
                await scope.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); scope.UpdateLayout();
                watch.Stop();
                if (run >= 2) samples.Add(new { elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
            }
            var testAnchor = new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
            var idleControls = Descendants<Control>(scope).Count(c => c.IsVisible && !VisibleBounds(c, scope).IsEmpty);
            if (!(bool)Invoke(controller, "Start", testAnchor)!) throw new Exception(name + " could not start for control count");
            scope.UpdateLayout();
            var activeControls = Descendants<Control>(scope).Count(c => c.IsVisible && !VisibleBounds(c, scope).IsEmpty);
            Invoke(controller, "Stop");
            result[name] = new { samples, idleControls, activeControls, editorControls = Descendants<Control>(editor).Count(c => c.IsVisible && !VisibleBounds(c, window).IsEmpty) };
        }
        try
        {
            await Measure("preview", Field(preview, "_autoScroll")!, pdfScroll, window);
            host.Show(); await panel.SetProjectAsync(Path.Combine(output, "performance.tex"));
            var book = (AiNotebook)Field(panel, "_book")!;
            for (var i = 0; i < 120; i++) book.Turns.Add(new("Synthetic question " + i, "Synthetic answer.") { SentAt = DateTimeOffset.UnixEpoch });
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Measure("ai", Field(panel, "_autoScroll")!, (ScrollViewer)panel.FindName("MessagesScroll"), host);
            File.WriteAllText(Path.Combine(output, "auto-scroll-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        finally { host.Close(); }
    }
}
