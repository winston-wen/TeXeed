using System.Collections;
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
    // Existing compiled project only: never save or compile the user's source.
    private static async Task CheckChapterFollowAsync(MainWindow window, string main, string output)
    {
        var settings = (AppSettings)Field(window, "_settings")!;
        window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
            settings with { LivePreview = false, FollowMode = PreviewFollowMode.Viewport, WordWrap = true });
        await CallAsync(window, "OpenAsync", main);
        var project = (TexProject)Field(window, "_project")!;
        var pdf = Path.Combine(project.Root, ".texeed", "preview", Path.GetFileNameWithoutExtension(main) + ".pdf");
        window.GetType().GetField("_pdfPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, pdf);
        window.GetType().GetField("_pdfRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Field(window, "_revision"));
        var editor = (LatexEditor)window.FindName("Editor");
        var preview = (PdfPreview)window.FindName("Preview");
        await preview.LoadAsync(pdf); await WaitForPreviewRenderedAsync(window);
        var scroll = (ScrollViewer)Field(preview, "_scroll")!;
        var pages = (Canvas)Field(preview, "_pages")!;
        var sync = new SyncTexService();
        foreach (var crop in new[] { true, false })
        foreach (var chapter in new[] { "02.tex", "01.tex" })
        {
            preview.AutoCrop = crop;
            await WaitForPreviewRenderedAsync(window);
            var file = project.Files.Single(f => f.Name == chapter);
            Invoke(window, "ActivateFile", file.Path);
            editor.ScrollToVerticalOffset(0);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var caret = editor.CaretOffset;
            var location = editor.ViewportFollowLocation!;
            var start = await sync.ForwardViewportAsync(project, pdf, new(file.Path, 1, 1), settings)
                ?? throw new Exception("Chapter start has no SyncTeX mapping");
            await CallAsync(window, "ForwardAsync", false); window.UpdateLayout();
            var layouts = (IList)Field(preview, "_layouts")!;
            var layout = layouts[start.Page - 1]!;
            var top = (double)layout.GetType().GetProperty("Top")!.GetValue(layout)!;
            await WaitUntilAsync(() => Math.Abs(pages.TranslatePoint(new Point(0, top + 1), scroll).Y) < 2,
                $"Chapter {chapter} page {start.Page} still has its opening clipped at source top (crop={crop})");
            if (location.Line != 1 || location.Column != 1 || editor.CaretOffset != caret)
                throw new Exception("Following chapter start did not retain source/caret position");
            await WaitForPreviewRenderedAsync(window);
            Capture(window, Path.Combine(output, $"chapter-{chapter}-{crop}.png"));
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 5);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var center = editor.ViewportLocation!;
            var point = await sync.ForwardViewportAsync(project, pdf, center, settings) ?? throw new Exception("No wrapped chapter mapping");
            await CallAsync(window, "ForwardAsync", false);
            await WaitUntilAsync(() => Math.Abs(scroll.VerticalOffset - PdfOffset(preview, point, .5)) < 2,
                "Scrolling away from source top did not restore center following");
            editor.ScrollToVerticalOffset(0);
            window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await WaitUntilAsync(() => Math.Abs(pages.TranslatePoint(new Point(0, top + 1), scroll).Y) < 2,
                "Automatic follow did not return to the chapter page start");
        }
        var modeBox = (ComboBox)window.FindName("FollowModeBox");
        modeBox.SelectedValue = PreviewFollowMode.Cursor;
        var caretPoint = await sync.ForwardAsync(project, pdf, editor.CurrentLocation!, settings) ?? throw new Exception("Cursor has no SyncTeX mapping");
        await CallAsync(window, "ForwardAsync", false);
        await WaitUntilAsync(() => Math.Abs(scroll.VerticalOffset - PdfOffset(preview, caretPoint with { Y = caretPoint.Y + caretPoint.Height / 2 }, .5)) < 2,
            "Source-top rule incorrectly overrode cursor mode centering");
        modeBox.SelectedValue = PreviewFollowMode.None;
        var heldOffset = scroll.VerticalOffset;
        editor.ScrollToVerticalOffset(20); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        editor.ScrollToVerticalOffset(0); window.UpdateLayout();
        await AssertPdfOffsetStableAsync(scroll, heldOffset, "Source-top rule ignored disabled following");
        modeBox.SelectedValue = PreviewFollowMode.Viewport;
        await CallAsync(window, "ForwardAsync", true);
        await WaitUntilAsync(() => Math.Abs(scroll.VerticalOffset - PdfOffset(preview, caretPoint, .35)) < 2,
            "Source-top rule overrode explicit Ctrl+J positioning");
        Console.WriteLine("PASS: existing chapter 01/02 starts, cropped/full page tops, wrapped center following and automatic return to source top");
        Console.WriteLine("PASS: source-top rule preserves cursor mode, disabled following and explicit source navigation");
    }
}
