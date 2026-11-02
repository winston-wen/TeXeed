using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckEditorScrollPerformanceAsync(MainWindow window, string output)
    {
        var root = Directory.GetCurrentDirectory();
        var engine = Path.Combine(root, ".tools", "tectonic", "tectonic.exe");
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var fixture = Path.Combine(output, "scroll-performance-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var lines = new List<string> { "\\documentclass{article}", "\\pagestyle{empty}", "\\begin{document}" };
        for (var page = 1; page <= 8; page++)
        {
            if (page > 1) lines.Add("\\newpage");
            for (var row = 1; row <= 40; row++) lines.Add($"\\noindent Page {page}, row {row:00}. Wheel input benchmark.\\par");
        }
        lines.Add("\\end{document}"); File.WriteAllText(main, string.Join('\n', lines) + "\n");
        var settings = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        settings.SetValue(window, new AppSettings { LivePreview = false, FollowMode = PreviewFollowMode.Viewport, Engine = TexEngine.Tectonic, EnginePath = engine, SynctexPath = Path.Combine(fixture, "absent.exe") });
        await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync"); await WaitForPreviewRenderedAsync(window);
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        var editorScroll = Descendants<ScrollViewer>(editor).First();
        var pdfScroll = (ScrollViewer)Field(preview, "_scroll")!;
        if (preview.PageCount != 8) throw new Exception("Scroll benchmark requires eight rendered pages.");
        var scenarios = new Dictionary<string, object>();
        static void Wheel(UIElement origin)
        {
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            origin.RaiseEvent(wheel);
            if (!wheel.Handled) { wheel.RoutedEvent = UIElement.MouseWheelEvent; origin.RaiseEvent(wheel); }
        }

        async Task Measure(string name, ScrollViewer target, bool editorTarget, int wheelCount, bool interruptFollow = false)
        {
            var samples = new List<object>();
            for (var run = 0; run < 9; run++)
            {
                editor.ScrollToVerticalOffset(80 * editor.TextArea.TextView.DefaultLineHeight);
                pdfScroll.ScrollToVerticalOffset(pdfScroll.ScrollableHeight * .4);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                await WaitForPreviewRenderedAsync(window);
                await Task.Delay(250);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var offset = target.VerticalOffset;
                var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                if (interruptFollow)
                {
                    Wheel(editor.TextArea);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    if (!((DispatcherTimer)Field(window, "_syncTimer")!).IsEnabled)
                        throw new Exception("Preview interrupt scenario did not queue source following.");
                }
                for (var i = 0; i < wheelCount; i++)
                {
                    var origin = editorTarget ? (UIElement)editor.TextArea : target;
                    Wheel(origin);
                }
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var layoutMs = watch.Elapsed.TotalMilliseconds;
                await Task.Delay(250);
                await WaitUntilAsync(() => !((DispatcherTimer)Field(window, "_syncTimer")!).IsEnabled && Field(window, "_forwardSyncCancellation") is null,
                    "Scroll-follow work did not settle.");
                await WaitForPreviewRenderedAsync(window);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                watch.Stop(); var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (target.VerticalOffset <= offset) throw new Exception(name + " did not move in response to the wheel event.");
                if (run >= 2) samples.Add(new { layoutMs, settledMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = bytes });
            }
            var controls = Descendants<Control>(window).Where(c => c.IsVisible).ToArray();
            scenarios[name] = new { samples, visibleControls = controls.Count(c => !VisibleBounds(c, window).IsEmpty), createdVisibleControls = controls.Length };
        }
        await Measure("editor-single", editorScroll, true, 1);
        await Measure("editor-continuous", editorScroll, true, 8);
        await Measure("preview-single", pdfScroll, false, 1);
        await Measure("preview-continuous", pdfScroll, false, 8);
        await Measure("preview-interrupt-follow", pdfScroll, false, 1, interruptFollow: true);
        Capture(window, Path.Combine(output, "editor-scroll-performance.png"));
        var result = new { fixture = "8 pages and 320 rows, viewport following; wheel events, ApplicationIdle and 250ms timer settling", scenarios };
        File.WriteAllText(Path.Combine(output, "editor-scroll-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
