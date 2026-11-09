using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Xps.Packaging;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint PrintWindowStyle(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
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
        Check(PdfPrintPages.Parse("3, 1-3，2", 3).SequenceEqual([2, 0, 1]), "Range parser must preserve order and remove duplicate pages");
        Check(PdfPrintPages.Parse("3-1", 3).SequenceEqual([2, 1, 0]), "Descending range was sorted unexpectedly");
        foreach (var text in new[] { "", "0", "4", "1,,2", "1-2-3", "NaN" })
        {
            try { PdfPrintPages.Parse(text, 3); throw new Exception("Invalid custom range accepted: " + text); }
            catch (ArgumentException) { ++checks; }
        }
        Check(PdfPrintPages.Describe([0, 2, 4, 6, 8, 12], 1) == "（仅奇数）1-9、13", "Odd-page compression is ambiguous");
        Check(PdfPrintPages.Describe([13, 9, 7, 5, 3, 1], 2) == "（仅偶数）14、10-2", "Reversed even-page compression is wrong");
        Check(PdfPrintPages.Filter([0, 1, 2], 2, true).SequenceEqual([1]), "Parity filter must use PDF's one-based page numbers");
        PdfPrintLayout FixedLayout(Size _) => layout;
        var options = new PdfPrintOptions();
        var fitted = PdfPrintPlan.Build(document.Pages, [0, 1, 2], options, FixedLayout);
        Check(fitted.Length == 3 && fitted.All(f => f.Items.All(i => f.Layout.Printable.Contains(i.Target))), "Single-page plan exceeded the printable area");
        var enlarged = PdfPrintPlan.Build(document.Pages, [0], options with { ScaleMode = PdfPrintScale.Custom, Scale = 150 }, FixedLayout);
        Check(Math.Abs(enlarged[0].Items[0].Target.Width - 1224) < .01, "Custom scale is not based on original physical PDF size");
        var cropped = PdfPrintPlan.Build(document.Pages, [1], options with { ViewRegion = new Rect(100, 100, 200, 120) }, FixedLayout);
        Check(cropped[0].Items[0].Source == new Rect(100, 100, 200, 120), "Current view lost its original PDF rectangle");
        var actualCropOptions = options with { ViewRegion = new Rect(100, 100, 200, 120), ScaleMode = PdfPrintScale.Actual };
        var actualCrop = PdfPrintPlan.Build(document.Pages, [1], actualCropOptions, FixedLayout);
        var cropBitmap = new RenderTargetBitmap(800, 1200, 96, 96, PixelFormats.Pbgra32); var cropVisual = new DrawingVisual();
        using (var dc = cropVisual.RenderOpen()) dc.DrawDrawing(PdfPrintDrawing.Render(document, actualCrop[0], actualCropOptions, 96)); cropBitmap.Render(cropVisual);
        var cropTarget = actualCrop[0].Items[0].Target;
        cropBitmap.CopyPixels(new Int32Rect((int)(cropTarget.X - 20), (int)(cropTarget.Y + 20), 1, 1), pixel, 4, 0);
        Check(pixel.Take(3).All(v => v == 255), "View printing exposed content outside the selected source rectangle");
        var nup = PdfPrintPlan.Build(document.Pages, [0, 1, 2], options with { Mode = PdfPrintMode.Multiple, NUp = 2 }, FixedLayout);
        Check(nup.Length == 2 && nup[0].Items.Count == 2 && nup[1].Items.Count == 1, "N-up dropped the partially filled final sheet");
        Check(nup.SelectMany(f => f.Items).All(i => i.Clip.Contains(i.Target)), "N-up page exceeds its own cell");
        var eightSizes = Enumerable.Repeat(document.Pages[0], 8).ToArray();
        var booklet = PdfPrintPlan.Build(eightSizes, Enumerable.Range(0, 8).ToArray(), options with { Mode = PdfPrintMode.Booklet }, FixedLayout);
        Check(booklet.SelectMany(f => f.Items).Select(i => i.Page).SequenceEqual([7, 0, 1, 6, 5, 2, 3, 4]), "Booklet imposition is not foldable");
        var rightBooklet = PdfPrintPlan.Build(eightSizes, Enumerable.Range(0, 8).ToArray(), options with { Mode = PdfPrintMode.Booklet, RightBinding = true }, FixedLayout);
        Check(rightBooklet[0].Items.Select(i => i.Page).SequenceEqual([0, 7]), "Right binding did not mirror the page pair");
        var padded = PdfPrintPlan.Build(document.Pages, [0, 1, 2], options with { Mode = PdfPrintMode.Booklet }, FixedLayout);
        Check(padded.Length == 2 && padded[0].Items.Count == 1 && padded[1].Items.Count == 2, "Booklet did not pad to a multiple of four");
        var poster = PdfPrintPlan.Build(document.Pages, [0], options with { Mode = PdfPrintMode.Poster, PosterScale = 180 }, FixedLayout);
        Check(poster.Length > 1 && poster.All(f => f.Items.Single().Page == 0), "Poster must tile the selected source page");
        Check(poster[1].Items[0].Target.X < poster[0].Items[0].Target.X, "Poster tile offset did not advance");
        var uncollated = PdfPrintPlan.Copies(fitted, 2, false, true);
        Check(uncollated.Select(f => f.Items.FirstOrDefault()?.Page ?? -1).SequenceEqual([0, 1, 0, 1, 2, -1, 2, -1]), "Uncollated duplex copies must duplicate sheets, not individual faces");
        var collated = PdfPrintPlan.Copies(fitted, 2, true, true);
        Check(collated.Select(f => f.Items.FirstOrDefault()?.Page ?? -1).SequenceEqual([0, 1, 2, -1, 0, 1, 2, -1]), "Odd-page duplex copies can print on the previous copy's blank back");
        var grayDrawing = PdfPrintDrawing.Render(document, fitted[0], options with { Gray = true }, 96);
        var grayBitmap = new RenderTargetBitmap(800, 1200, 96, 96, PixelFormats.Pbgra32); var grayVisual = new DrawingVisual();
        using (var dc = grayVisual.RenderOpen()) dc.DrawDrawing(grayDrawing); grayBitmap.Render(grayVisual);
        grayBitmap.CopyPixels(new Int32Rect(400, 500, 1, 1), pixel, 4, 0);
        Check(pixel[0] == pixel[1] && pixel[1] == pixel[2] && pixel[0] < 250, "Grayscale is not applied to the actual output drawing");
        var onlyNotes = document.RenderForPrint(0, 1, content: PdfPrintContent.Annotations);
        onlyNotes.CopyPixels(new Int32Rect(300, 300, 1, 1), pixel, 4, 0);
        Check(pixel.Take(3).All(v => v == 255), "Annotations-only output still contains document graphics");
        onlyNotes.CopyPixels(new Int32Rect(40, 752, 1, 1), pixel, 4, 0);
        Check(pixel[1] > 220 && pixel[0] < 20 && pixel[2] < 20, "Annotations-only output lost the printable annotation appearance");
        var withoutNotes = document.RenderForPrint(0, 1, content: PdfPrintContent.Document);
        withoutNotes.CopyPixels(new Int32Rect(40, 752, 1, 1), pixel, 4, 0);
        Check(pixel.Take(3).All(v => v == 255), "Document-only output still includes PDF annotations");
        var restored = document.RenderForPrint(0, 1);
        restored.CopyPixels(new Int32Rect(300, 300, 1, 1), pixel, 4, 0);
        Check(pixel[2] > 220 && pixel[0] < 20, "Annotations-only render changed subsequent document output");

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
        var composedPath = Path.Combine(output, "print-imposed-pages.xps"); File.Delete(composedPath);
        using (var xps = new XpsDocument(composedPath, FileAccess.ReadWrite))
        {
            var tickets = nup.Select(_ => new System.Printing.PrintTicket { PageMediaSize = new System.Printing.PageMediaSize(layout.Paper.Width, layout.Paper.Height) }).ToArray();
            await PdfPrintJob.WriteAsync(XpsDocument.CreateXpsDocumentWriter(xps), new PdfPrintPaginator(document, nup, options), new System.Printing.PrintTicket(), tickets).WaitAsync(TimeSpan.FromSeconds(40));
        }
        using (var xps = new XpsDocument(composedPath, FileAccess.Read))
        {
            var sequence = xps.GetFixedDocumentSequence();
            Check(sequence.DocumentPaginator.PageCount == 2, "Shared-plan paginator did not serialize all N-up faces");
            using var fixedDoc = sequence.DocumentPaginator.GetPage(0);
            Check(fixedDoc.Size == layout.Paper, "Page-level print ticket changed the imposed paper dimensions");
        }
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
        Check(preview.CapturePrintView() is { IsEmpty: false } view && new Rect(document.Pages[1]).Contains(view), "Reading view did not capture a valid original PDF region");
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
            var dialog = new PdfPrintWindow(path, "Print regression · mixed page sizes", 2, viewRegion: new Rect(100, 100, 200, 120)) { Owner = window };
            dialog.Show();
            await WaitUntilAsync(() => (bool)Field(dialog, "_ready")! && ((Image)dialog.FindName("PageImage")).Source is not null,
                "Print preview failed to load original PDF");
            var hwnd = new WindowInteropHelper(dialog).Handle;
            Check((PrintWindowStyle(hwnd, -16) & 0x30000) == 0 && CaptionMessage(hwnd, 0x7f, 0, 0) == 0,
                "Print native caption must contain plain title text and a close button only");
            Check(Math.Abs(((ComboBox)dialog.FindName("PrinterBox")).TranslatePoint(new Point(), dialog).X - ((TextBox)dialog.FindName("CopiesInput")).TranslatePoint(new Point(), dialog).X) < .1, "Printer and copies inputs are not aligned");
            var flow = (WrapPanel)dialog.FindName("ParityOptions");
            Check(flow.Children.OfType<ContentControl>().Select(c => c.Content).SequenceEqual(new[] { "不分奇偶", "奇数页", "偶数页", "逆序打印" }), "Parity options have wrong text or flow order");
            flow.Width = 180; dialog.UpdateLayout();
            Check(((CheckBox)dialog.FindName("ReverseCheck")).TranslatePoint(new Point(), flow).Y > 0, "Parity options did not wrap when the row ran out of width");
            Check(((TextBlock)dialog.FindName("ActualPagesLabel")).TranslatePoint(new Point(), dialog).Y > flow.TranslatePoint(new Point(0, flow.ActualHeight), dialog).Y, "Actual page list must remain on its own row after wrapping");
            flow.Width = double.NaN; dialog.UpdateLayout();
            Check(((TextBox)dialog.FindName("PageInput")).Text == "2", "Print preview did not start at the current page");
            Check(!ReferenceEquals(Field(dialog, "_document"), Field(preview, "_document")), "Printing reused the changing preview document");
            ((Button)dialog.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await (Task)Field(dialog, "_renderTask")!;
            Check(((TextBox)dialog.FindName("PageInput")).Text == "3" && !((Button)dialog.FindName("NextButton")).IsEnabled,
                "Preview next-page navigation did not stop at the last page");
            ((ComboBox)dialog.FindName("OrientationBox")).SelectedIndex = 2;
            await CallAsync(dialog, "FlushAsync");
            var sheet = (Canvas)dialog.FindName("Sheet");
            Check(sheet.Width > sheet.Height, "Landscape paper selection did not update print preview");
            ((RadioButton)dialog.FindName("ActualScale")).IsChecked = true;
            await CallAsync(dialog, "FlushAsync");
            Check(((PdfPrintOptions)Field(dialog, "_options")!).ScaleMode == PdfPrintScale.Actual, "Actual-size selection did not update print layout");
            Capture(dialog, Path.Combine(output, "print-preview-" + theme + ".png"));
            ((TextBox)dialog.FindName("RangeInput")).Text = "3, 1-2";
            ((RadioButton)dialog.FindName("OddParity")).IsChecked = true;
            ((CheckBox)dialog.FindName("ReverseCheck")).IsChecked = true;
            await CallAsync(dialog, "FlushAsync");
            Check(((TextBlock)dialog.FindName("ActualPagesLabel")).Text == "实际页码：（仅奇数）1、3", "Actual ordered pages do not reflect custom range/parity/reverse");
            ((RadioButton)dialog.FindName("AllParity")).IsChecked = true; ((RadioButton)dialog.FindName("AllRange")).IsChecked = true;
            ((CheckBox)dialog.FindName("ReverseCheck")).IsChecked = false;
            ((TabControl)dialog.FindName("LayoutTabs")).SelectedIndex = 1;
            await CallAsync(dialog, "FlushAsync");
            Check(((PdfPrintFace[])Field(dialog, "_faces")!).Length == 1, "Four-up UI does not use the shared imposition plan");
            Capture(dialog, Path.Combine(output, "print-multiple-" + theme + ".png"));
            ((TabControl)dialog.FindName("LayoutTabs")).SelectedIndex = 2;
            await CallAsync(dialog, "FlushAsync");
            Check(((PdfPrintFace[])Field(dialog, "_faces")!).Length == 2, "Booklet UI did not create front/back faces");
            ((TabControl)dialog.FindName("LayoutTabs")).SelectedIndex = 3;
            await CallAsync(dialog, "FlushAsync");
            Check(((PdfPrintFace[])Field(dialog, "_faces")!).Length > 3, "Poster UI did not tile pages");
            ((TabControl)dialog.FindName("LayoutTabs")).SelectedIndex = 0;
            ((RadioButton)dialog.FindName("ViewRange")).IsChecked = true;
            await CallAsync(dialog, "FlushAsync");
            Check(((PdfPrintFace[])Field(dialog, "_faces")!).Single().Items.Single().Source == new Rect(100, 100, 200, 120), "View radio printed the entire source page");
            if (theme == "ristretto")
            {
                async Task CheckChild(Action open, Func<Window, Task> inspect)
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = dialog.Dispatcher.InvokeAsync(async () =>
                    {
                        Window? child = null;
                        try
                        {
                            await WaitUntilAsync(() => dialog.OwnedWindows.Count > 0, "Print child dialog did not open");
                            child = dialog.OwnedWindows.Cast<Window>().Single();
                            Check(!IsWindowEnabled(hwnd), "Print child dialog did not lock its owner");
                            await inspect(child); child.Close();
                            await WaitUntilAsync(() => !child.IsVisible, "Print child did not finish closing"); completion.SetResult();
                        }
                        catch (Exception ex) { child?.Close(); completion.SetException(ex); }
                    });
                    open(); await completion.Task;
                    Check(IsWindowEnabled(hwnd), "Print child did not restore its owner's input");
                }
                await CheckChild(() => Invoke(dialog, "PageSetup_Click", dialog, new RoutedEventArgs()), child =>
                {
                    Capture(child, Path.Combine(output, "print-page-setup.png"));
                    Check(Descendants<TextBox>(child).Count(t => t.IsVisible) == 6, "Page setup must offer physical dimensions and all four margins");
                    return Task.CompletedTask;
                });
                await CheckChild(() => Invoke(dialog, "Advanced_Click", dialog, new RoutedEventArgs()), child =>
                {
                    Capture(child, Path.Combine(output, "print-advanced.png"));
                    Check(Descendants<ComboBox>(child).Count() == 2, "Advanced dialog is missing output resolution/color");
                    return Task.CompletedTask;
                });
                ((RadioButton)dialog.FindName("AllRange")).IsChecked = true; await CallAsync(dialog, "FlushAsync");
                var large = new PdfPrintPreviewWindow((PdfDocument)Field(dialog, "_document")!, (PdfPrintFace[])Field(dialog, "_faces")!, (PdfPrintOptions)Field(dialog, "_options")!, 0) { Owner = dialog };
                await CheckChild(() => large.ShowDialog(), async child =>
                {
                    await WaitUntilAsync(() => ((Image)Field(child, "_image")!).Source is not null, "Large print preview did not render");
                    Invoke(child, "Navigate", 2); await Task.WhenAll((List<Task>)Field(child, "_tasks")!);
                    Check(((PdfPrintPreviewWindow)child).FaceIndex == 2, "Large preview did not navigate to requested face");
                    Invoke(child, "Zoom", 1.2d); Check(!(bool)Field(child, "_automatic")!, "Large preview zoom did not exit automatic mode");
                    Check(((System.Windows.Shapes.Rectangle)Field(child, "_guide")!).Visibility == Visibility.Visible, "Large preview hid the printable boundary");
                    Capture(child, Path.Combine(output, "print-large-preview.png"));
                    Check(!Descendants<Button>(child).Any(b => Equals(b.Content, "批注")), "Prototype annotation tool leaked into the product");
                });
            }
            ((TextBox)dialog.FindName("CopiesInput")).Text = "0";
            await CallAsync(dialog, "FlushAsync");
            Check(!((Button)dialog.FindName("PrintButton")).IsEnabled, "Invalid copies still allow a physical print job");
            dialog.Close(); await Task.Delay(50);
        }
        // Closing while loading/rendering must not dispose PDFium out from
        // under a background operation or deliver a late UI update.
        var transient = new PdfPrintWindow(path, "Close during load") { Owner = window };
        transient.Show(); transient.Close(); await Task.Delay(200); ++checks;
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Printing or preview modified the PDF source file");
        Check(window.FindName("PrintMenuItem") is MenuItem && window.FindName("PrintPreviewMenuItem") is null, "Print and preview must share a single menu entry");
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
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R {(i == 0 ? "/Annots [10 0 R]" : "")} >>");
            var content = $"{color} rg 70 70 {w - 140} {h - 140} re f 0 0 0 rg BT /F1 20 Tf 20 {h - 35} Td (Original PDF - Page {i + 1}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Annot /Subtype /Square /Rect [20 20 60 60] /C [0 1 0] /F 4 /AP << /N 11 0 R >> >>");
        const string appearance = "0 1 0 rg 0 0 40 40 re f";
        objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream");
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        foreach (var obj in objects) { offsets.Add(pdf.Length); pdf.Append($"{offsets.Count} 0 obj\n{obj}\nendobj\n"); }
        var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
