using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckPdfPrintingAsync(MainWindow window, string output)
    {
        var checks = 0;
        void Check(bool passed, string message) { if (!passed) throw new Exception(message); ++checks; }
        var path = Path.Combine(output, "print-source.pdf"); WritePrintPdf(path);
        var original = File.ReadAllBytes(path);
        using var document = new PdfDocument(original);
        Check(document.Pages.Count == 3 && document.Pages[1].Width > document.Pages[1].Height, "Mixed-size print fixture was not read");
        var layout = new PdfPrintLayout(PdfPrintLayout.A4, PdfPrintLayout.Inset(PdfPrintLayout.A4, 24));
        foreach (var page in document.Pages)
        {
            var target = layout.Place(page);
            Check(layout.Printable.Contains(target) && Math.Abs(target.Width / target.Height - page.Width / page.Height) < .0001,
                "Fit-to-paper changed aspect ratio or exceeded the printable area");
        }
        Check(PdfPrintLayout.Pages(3, PageRangeSelection.CurrentPage, new PageRange(1, 3), 2).SequenceEqual([1]), "Current page selection ignored reading position");
        Check(PdfPrintLayout.Pages(3, PageRangeSelection.UserPages, new PageRange(2, 3), 1).SequenceEqual([1, 2]), "Page range is off by one");
        Check(PdfPrintLayout.Pages(3, PageRangeSelection.AllPages, default, 2).SequenceEqual([0, 1, 2]), "All-pages selection is incomplete");
        foreach (var range in new[] { new PageRange(0, 3), new PageRange(3, 2), new PageRange(1, 4) })
        {
            try { PdfPrintLayout.Pages(3, PageRangeSelection.UserPages, range, 1); throw new Exception("Invalid range accepted"); }
            catch (ArgumentException) { ++checks; }
        }
        var printRaster = document.RenderForPrint(0, 300d / 72);
        Check(printRaster.PixelWidth == 2550 && printRaster.PixelHeight == 3300, "Printing is still limited to the 3200px display raster");
        var pixel = new byte[4]; printRaster.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Check(pixel.SequenceEqual(new byte[] { 255, 255, 255, 255 }), "Print raster inherited dark preview colors");
        var paginator = new PdfPrintPaginator(document, layout, [1, 2]);
        Check(paginator.PageCount == 2 && paginator.IsPageCountValid, "Selected-page paginator has wrong count");
        Check(paginator.GetPage(-1) == DocumentPage.Missing && paginator.GetPage(2) == DocumentPage.Missing, "Paginator must return Missing outside the range");
        using (var page = paginator.GetPage(0))
        {
            Check(page.Size == layout.Paper && page.ContentBox == layout.Printable, "Paginator lost printer hardware margins");
            var bitmap = new RenderTargetBitmap((int)layout.Paper.Width, (int)layout.Paper.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page.Visual);
            bitmap.CopyPixels(new Int32Rect((int)layout.Paper.Width / 2, (int)layout.Paper.Height / 2, 1, 1), pixel, 4, 0);
            Check(pixel[0] > 220 && pixel[2] < 20, "Paginator printed the wrong source page (expected blue page 2)");
            using var image = File.Create(Path.Combine(output, "print-paginator-page-2.png"));
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(image);
        }
        var actual = layout with { ActualSize = true };
        Check(Math.Abs(actual.Place(document.Pages[0]).Width - 816) < .01, "Actual-size printing must use 96/72, regardless of display DPI or preview zoom");

        // Exercise the same asynchronous serialization path as the printer,
        // writing to a local XPS package without sending paper to a device.
        var xpsPath = Path.Combine(output, "print-selected-pages.xps");
        File.Delete(xpsPath);
        using (var xps = new XpsDocument(xpsPath, FileAccess.ReadWrite))
        {
            var writer = XpsDocument.CreateXpsDocumentWriter(xps);
            await PdfPrintJob.WriteAsync(writer, paginator).WaitAsync(TimeSpan.FromSeconds(40));
        }
        using (var xps = new XpsDocument(xpsPath, FileAccess.Read))
            Check(xps.GetFixedDocumentSequence().DocumentPaginator.PageCount == 2, "Spool output did not preserve selected pages");
        var cancelledPath = Path.Combine(output, "print-cancelled.xps"); File.Delete(cancelledPath);
        using (var xps = new XpsDocument(cancelledPath, FileAccess.ReadWrite))
        {
            var writer = XpsDocument.CreateXpsDocumentWriter(xps);
            var pending = PdfPrintJob.WriteAsync(writer, new PdfPrintPaginator(document, layout, [0, 1, 2]));
            writer.CancelAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(20)); throw new Exception("Cancelled spool unexpectedly completed"); }
            catch (OperationCanceledException) { ++checks; }
        }

        var preview = (PdfPreview)window.FindName("Preview");
        preview.InvertColors = true; preview.SetCropOptions(true, true); await preview.LoadAsync(path);
        preview.ShowLocation(new TeXeed.Core.PdfLocation(2, 200, 200), false);
        window.UpdateLayout();
        Check(preview.VisiblePage == 2, "Print current-page lookup does not follow the PDF reading position");
        var aiIcon = Descendants<Button>((FrameworkElement)window.FindName("WorkspaceIconBar")).Single(b => Equals(b.Tag, "ai-mark"));
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme); window.UpdateLayout();
            var iconBitmap = new RenderTargetBitmap(288, 256, 768, 768, PixelFormats.Pbgra32);
            var iconDrawing = new DrawingVisual();
            using (var drawing = iconDrawing.RenderOpen())
            {
                drawing.DrawRectangle(ThemeManager.Brush("SidebarBackground"), null, new Rect(0, 0, 36, 32));
                drawing.DrawRectangle(new VisualBrush(aiIcon), null, new Rect(0, 0, 36, 32));
            }
            iconBitmap.Render(iconDrawing);
            using (var iconFile = File.Create(Path.Combine(output, "ai-icon-" + theme + ".png")))
            { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(iconBitmap)); encoder.Save(iconFile); }
            Check(Descendants<System.Windows.Shapes.Path>(aiIcon).Count() == 2, "AI icon must layer the letters and crossing pencil separately");
            Capture(window, Path.Combine(output, "workspace-icons-" + theme + ".png"));
            var dialog = new PdfPrintWindow(path, "Print regression · mixed page sizes", 2) { Owner = window };
            dialog.Show();
            await WaitUntilAsync(() => (bool)Field(dialog, "_ready")! && ((Image)dialog.FindName("PageImage")).Source is not null,
                "Print preview failed to load original PDF");
            Check(((TextBox)dialog.FindName("PageInput")).Text == "2", "Print preview did not start at the current page");
            Check(!ReferenceEquals(Field(dialog, "_document"), Field(preview, "_document")), "Printing reused the changing preview document");
            ((Button)dialog.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await (Task)Field(dialog, "_renderTask")!;
            Check(((TextBox)dialog.FindName("PageInput")).Text == "3" && !((Button)dialog.FindName("NextButton")).IsEnabled,
                "Preview next-page navigation did not stop at the last page");
            ((ComboBox)dialog.FindName("OrientationBox")).SelectedIndex = 1;
            await (Task)Field(dialog, "_renderTask")!;
            var sheet = (Canvas)dialog.FindName("Sheet");
            Check(sheet.Width > sheet.Height, "Landscape paper selection did not update print preview");
            ((ComboBox)dialog.FindName("ScaleBox")).SelectedIndex = 1;
            await (Task)Field(dialog, "_renderTask")!;
            Check(((PdfPrintLayout)Field(dialog, "_layout")!).ActualSize, "Actual-size selection did not update print layout");
            Capture(dialog, Path.Combine(output, "print-preview-" + theme + ".png"));
            dialog.Close(); await Task.Delay(50);
        }
        // Closing while loading/rendering must not dispose PDFium out from
        // under a background operation or deliver a late UI update.
        var transient = new PdfPrintWindow(path, "Close during load") { Owner = window };
        transient.Show(); transient.Close(); await Task.Delay(200); ++checks;
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Printing or preview modified the PDF source file");
        Check(window.FindName("PrintMenuItem") is MenuItem && window.FindName("PrintPreviewMenuItem") is MenuItem, "Printing menu commands are missing");
        ThemeManager.Apply("ristretto");
        Console.WriteLine($"PASS: {checks} PDF printing checks (range, current page, 300 dpi, original colors, margins, async XPS, preview, close, icon themes)");
    }

    private static void WritePrintPdf(string path)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var sizes = new[] { (612, 792, "1 0 0"), (792, 612, "0 0 1"), (288, 432, "0 0.6 0") };
        for (var i = 0; i < sizes.Length; ++i)
        {
            var (w, h, color) = sizes[i];
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
            var content = $"{color} rg 70 70 {w - 140} {h - 140} re f 0 0 0 rg BT /F1 20 Tf 20 {h - 35} Td (Original PDF - Page {i + 1}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        foreach (var obj in objects) { offsets.Add(pdf.Length); pdf.Append($"{offsets.Count} 0 obj\n{obj}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
