using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPdfAnnotationSelectionAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "pdf-annotation-selection"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}Preview selection\\end{document}\n");
        await CallAsync(window, "OpenAsync", main);
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(folder, "selection.pdf"); WriteThemePdf(pdf);
        preview.SetCropOptions(false, false); await preview.LoadAsync(pdf); window.UpdateLayout();
        var layouts = ((IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var first = layouts[0];
        var left = (double)first.GetType().GetProperty("Left")!.GetValue(first)!;
        var top = (double)first.GetType().GetProperty("Top")!.GetValue(first)!;
        var original = new Point(left + 35, top + 48);
        preview.CaptureContextLocation(original);
        var storedLocation = (PdfLocation?)Field(preview, "_contextLocation");
        if (storedLocation is null || storedLocation.Page != 1) throw new Exception("PDF menu lost its right-click page location");
        PdfLocation? inverse = null;
        preview.InverseRequested += (_, position) => inverse = position;
        var menu = preview.ContextMenu;
        var jump = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "跳转到源码"));
        var annotate = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "AI 圈点"));
        jump.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (inverse != storedLocation) throw new Exception("PDF menu jumped to a position other than the stored right-click location");
        var second = layouts[1];
        var secondLeft = (double)second.GetType().GetProperty("Left")!.GetValue(second)!;
        var secondTop = (double)second.GetType().GetProperty("Top")!.GetValue(second)!;
        preview.CaptureContextLocation(new Point(secondLeft + 45, secondTop + 60)); inverse = null;
        jump.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (inverse?.Page != 2) throw new Exception("PDF menu did not retain the selected page");
        preview.CaptureContextLocation(new Point(-100, -100)); inverse = null;
        jump.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (inverse is not null) throw new Exception("PDF menu jumped from outside the page");

        AiEntry? captured = null;
        preview.AiSelectionConfirmed += (_, entry) => captured = entry;
        var selectionField = typeof(PdfPreview).GetField("_aiSelection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        selectionField.SetValue(preview, new Rect(left + 30, top + 50, 0, 0));
        await preview.ConfirmAiSelectionAsync();
        if (captured is not null) throw new Exception("A point created a new PDF annotation");

        typeof(PdfPreview).GetField("_aiStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(preview, new Point(left + 30, top + 50));
        Invoke(preview, "UpdateAiSelection", new Point(left + 130, top + 100));
        await WaitForPreviewRenderedAsync(window);
        Capture(window, Path.Combine(output, "pdf-annotation-selected-before-menu.png"));
        preview.CaptureContextLocation(new Point(left + 45, top + 65));
        if (((Rect)selectionField.GetValue(preview)!).IsEmpty) throw new Exception("Right-click inside a PDF selection discarded it");
        annotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => captured is not null, "Selecting a PDF area before annotation did not create an entry");
        if (captured!.IsPoint || captured.PdfBlocks.Count == 0 || captured.PdfBlocks.Any(block => block.Width <= 0 || block.Height <= 0))
            throw new Exception("PDF area annotation did not preserve an actual selected region");

        captured = null;
        typeof(PdfPreview).GetField("_aiStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(preview, new Point(left + 30, top + 50));
        Invoke(preview, "UpdateAiSelection", new Point(left + 130, top + 100));
        preview.CaptureContextLocation(new Point(secondLeft + 45, secondTop + 60)); inverse = null;
        if (!((Rect)selectionField.GetValue(preview)!).IsEmpty) throw new Exception("Right-click outside a PDF selection retained the wrong area");
        jump.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (inverse?.Page != 2) throw new Exception("Jump after an outside right-click did not use the new PDF location");
        annotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (captured is not null || !preview.IsAiSelecting || ((Canvas)Field(preview, "_pages")!).Cursor != Cursors.Cross)
            throw new Exception("Starting PDF annotation did not enable the selection cursor");
        selectionField.SetValue(preview, new Rect(left + 40, top + 60, 70, 35));
        await preview.ConfirmAiSelectionAsync();
        if (captured is null || captured.IsPoint) throw new Exception("Selecting after starting PDF annotation failed");
        preview.SetAiMarking(false);
        var oldPoint = new AiEntry { PdfRevision = captured.PdfRevision, IsPoint = true };
        var originalBlock = captured.PdfBlocks[0];
        oldPoint.PdfBlocks.Add(originalBlock with { PointX = originalBlock.X, PointY = originalBlock.Y });
        if (preview.AnnotationPosition(oldPoint) is null) throw new Exception("Old PDF point annotations are no longer readable");
        Capture(window, Path.Combine(output, "pdf-annotation-selection.png"));
        Console.WriteLine("PASS: PDF right-click location, two area annotation orders, point rejection and old point compatibility");
    }
}
