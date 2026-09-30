using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    // Read-only diagnostic: live compilation is disabled in the isolated test profile.
    private static async Task ProfileOutlineAsync(MainWindow window, string path, string? pdfPath)
    {
        await CallAsync(window, "OpenAsync", path);
        var project = (TexProject)Field(window, "_project")!;
        var entries = LatexIndexer.Build(project.Files).Outline;
        var editor = (LatexEditor)window.FindName("Editor");
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        if (pdfPath is not null)
        {
            await ((PdfPreview)window.FindName("Preview")).LoadAsync(pdfPath);
            window.GetType().GetField("_pdfPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, pdfPath);
            window.GetType().GetField("_pdfRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Field(window, "_revision"));
        }
        foreach (var entry in entries.Reverse().Concat(entries))
        {
            var item = Descendants<TreeViewItem>(outline).Single(i => i.Header is OutlineNode node && node.Entry == entry);
            item.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
            var heading = (Button)item.Template.FindName("HeadingButton", item);
            var watch = Stopwatch.StartNew();
            DoubleClickHeading(window, heading);
            var navigate = watch.Elapsed.TotalMilliseconds;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
            var frame = watch.Elapsed.TotalMilliseconds;
            if (editor.CurrentLocation?.Line != entry.Line) throw new Exception("Outline click did not reach the requested line.");
            if (pdfPath is not null) await CallAsync(window, "ForwardAsync", false);
            Console.WriteLine($"PROFILE line={entry.Line} click={navigate:F1}ms frame={frame:F1}ms withSync={watch.Elapsed.TotalMilliseconds:F1}ms");
        }
    }
}
