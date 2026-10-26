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
    // Read an existing compiled project without compiling, saving, or copying its private source.
    private static async Task CheckExistingFollowAsync(MainWindow window, string main, string output)
    {
        var settings = (AppSettings)Field(window, "_settings")!;
        typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
            settings with { LivePreview = false, FollowMode = PreviewFollowMode.Viewport });
        await CallAsync(window, "OpenAsync", main);
        var pdf = Path.Combine(Path.GetDirectoryName(main)!, ".texeed", "preview", Path.GetFileNameWithoutExtension(main) + ".pdf");
        typeof(MainWindow).GetField("_pdfPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, pdf);
        typeof(MainWindow).GetField("_pdfRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Field(window, "_revision"));
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        preview.AutoCrop = true; await preview.LoadAsync(pdf); await WaitForPreviewRenderedAsync(window);
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        var caret = editor.CaretOffset;
        var positions = new List<string>();
        foreach (var crop in new[] { true, false })
        foreach (var direction in new[] { 1, -1 })
        {
            preview.AutoCrop = crop; await WaitForPreviewRenderedAsync(window);
            double? previous = null;
            foreach (var sourceLine in direction > 0 ? Enumerable.Range(68, 17) : Enumerable.Range(68, 17).Reverse())
            {
                var view = editor.TextArea.TextView;
                editor.ScrollToVerticalOffset((sourceLine - .5) * view.DefaultLineHeight - view.ActualHeight / 2);
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await CallAsync(window, "ForwardAsync", false);
                window.UpdateLayout(); await Task.Delay(160);
                var location = editor.ViewportLocation!;
                var offset = scroll.VerticalOffset;
                positions.Add($"crop={crop}, direction={direction}, source={location.Line}, pdfOffset={offset:F2}");
                File.WriteAllLines(Path.Combine(output, "follow-offsets.txt"), positions);
                if (previous is { } last && (offset - last) * direction < -30)
                    throw new Exception($"PDF jumped backward scrolling source {location.Line}: {last:F2} -> {offset:F2}");
                previous = offset;
            }
        }
        var project = (TexProject)Field(window, "_project")!;
        var sync = new SyncTexService();
        var end = new SourceLocation(main, editor.Document.LineCount);
        var endPoint = await sync.ForwardViewportAsync(project, pdf, end, settings)
            ?? throw new Exception("Document end has no SyncTeX mapping");
        if (endPoint.Page != preview.PageCount) throw new Exception("Fixture must map its last source line to its last PDF page");
        foreach (var crop in new[] { true, false })
        {
            preview.AutoCrop = crop; await WaitForPreviewRenderedAsync(window);
            editor.ScrollToVerticalOffset(0);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            editor.ScrollToVerticalOffset(editor.ExtentHeight);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var anchor = editor.ViewportLocation ?? throw new Exception("Document end has no viewport anchor");
            if (anchor.Line != editor.Document.LineCount)
                throw new Exception($"Source scrolled to bottom but anchor is line {anchor.Line}, expected {editor.Document.LineCount}");
            var view = editor.TextArea.TextView;
            var finalRowY = view.VisualLines[^1].VisualTop - view.ScrollOffset.Y;
            if (finalRowY < -1 || finalRowY > view.ActualHeight / 4)
                throw new Exception($"Final source row must be in the top quarter after overscrolling (y={finalRowY:F1})");
            // Observe the normal debounced follow event, rather than forcing a forward request.
            await WaitUntilAsync(() => Math.Abs(scroll.VerticalOffset - PdfOffset(preview, endPoint, .5)) < 2,
                $"Source bottom did not reveal final PDF page {endPoint.Page} (crop={crop})");
            await AssertPdfOffsetStableAsync(scroll, scroll.VerticalOffset, "Final page follow was overwritten");
        }
        if (editor.CaretOffset != caret) throw new Exception("Source scroll moved the caret");
        Console.WriteLine("PASS: existing PDF follows source lines 68-84 in both directions across page 1/2, with/without cropping, without backward jumps or caret movement");
        Console.WriteLine("PASS: native overscroll brings final source row near the top and automatically reveals final PDF page, with/without cropping and without caret movement");
    }
}
