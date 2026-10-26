using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static int _passed;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                Check("native PDFium loads, sizes and rasterizes known color marks", NativeRender);
                Check("EDID sizes and physical pixels convert independently of UI scaling", PhysicalDimensions);
                await CheckAsync("automatic width observes the centimeter cap across display density and manual zoom", PhysicalWidthCap);
                Check("Ristretto PDF palette preserves alpha and smooth antialiasing gradients", InvertedRaster);
                await CheckAsync("color switching preserves rasters and anchors across zoom races, scroll and reload", InversionContinuity);
                Check("automatic crop preserves content, footer, short gaps, rotation and bounded rasters", CropGeometry);
                await CheckAsync("cropped continuous preview fills width and preserves source anchors, zoom and lifecycle", CropPreview);
                Check("inverse mapping respects page rotation and crop boxes", Coordinates);
                Check("raster limits and render/dispose concurrency remain bounded", RasterLifecycle);
                await CheckAsync("resize during load retains the newest PDF", LoadingRace);
                await CheckAsync("only visible PDF pages own visual trees and raster buffers", Virtualization);
                await CheckAsync("zoom keeps page content visible until a current-resolution raster replaces it", ZoomContinuity);
                await CheckAsync("pointer zoom preserves PDF content across horizontal scrolling and repeated scales", PointerZoom);
                await CheckAsync("zoom anchors respect rotated/cropped pages and page gaps", RotatedAndGapZoom);
                await CheckAsync("first and last PDF content can center without clipping the scroll range", CenteredNavigation);
                await CheckAsync("toolbar zoom and fit width retain the viewport's reading position", ToolbarZoom);
                await CheckAsync("fit-width resize and preview-column changes retain centered content", ResizeReadingPosition);
                await CheckAsync("clear/dispose cancel pending loads and renders", PreviewLifecycle);
                Console.WriteLine($"PASS: {_passed} native PDF checks");
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static void Check(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static async Task CheckAsync(string name, Func<Task> action) { await action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < .001, $"{message}: {actual} != {expected}");

    private static void NativeRender()
    {
        using var document = new PdfDocument(Fixture(3));
        Require(document.Pages.Count == 3, "Page count is incorrect.");
        Near(document.Pages[0].Width, 200, "Page width"); Near(document.Pages[0].Height, 300, "Page height");
        var bitmap = document.Render(0, 1);
        Require(bitmap.IsFrozen && bitmap.PixelWidth == 200 && bitmap.PixelHeight == 300, "Raster is not a frozen 200×300 image.");
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(25, 35, 1, 1), pixel, 4, 0);
        Require(pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 255 && pixel[3] == 255, "PDF content was not rendered at the expected top-left coordinates.");
        bitmap.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0);
        Require(pixel.All(x => x == 255), "Page background is not opaque white.");
    }

    private static void Coordinates()
    {
        using var normal = new PdfDocument(Fixture(1));
        var topLeft = normal.ViewToPage(0, new Point(40, 60), new Size(400, 600));
        Near(topLeft.X, 20, "Normal inverse X"); Near(topLeft.Y, 30, "Normal inverse Y");
        using var rotated = new PdfDocument(Fixture(1, rotation: 90));
        Near(rotated.Pages[0].Width, 300, "Rotated width"); Near(rotated.Pages[0].Height, 200, "Rotated height");
        var rotatedPoint = rotated.PageToView(0, new Point(20, 30), new Size(300, 200));
        Near(rotatedPoint.X, 270, "Rotated forward X"); Near(rotatedPoint.Y, 20, "Rotated forward Y");
        var inverse = rotated.ViewToPage(0, rotatedPoint, new Size(300, 200));
        Near(inverse.X, 20, "Rotated inverse X"); Near(inverse.Y, 30, "Rotated inverse Y");
        using var cropped = new PdfDocument(Fixture(1, crop: true));
        Near(cropped.Pages[0].Width, 180, "Cropped width"); Near(cropped.Pages[0].Height, 260, "Cropped height");
        var croppedPoint = cropped.PageToView(0, new Point(20, 30), new Size(180, 260));
        Near(croppedPoint.X, 10, "Cropped forward X"); Near(croppedPoint.Y, 10, "Cropped forward Y");
        inverse = cropped.ViewToPage(0, croppedPoint, new Size(180, 260));
        Near(inverse.X, 20, "Cropped inverse X"); Near(inverse.Y, 30, "Cropped inverse Y");
    }

    private static void RasterLifecycle()
    {
        var document = new PdfDocument(Fixture(1));
        var bitmap = document.Render(0, 1000, 100_000);
        Require((long)bitmap.PixelWidth * bitmap.PixelHeight <= 100_000, "Pixel budget was exceeded.");
        var singlePixel = document.Render(0, 1000, 1);
        Require(singlePixel.PixelWidth == 1 && singlePixel.PixelHeight == 1, "The minimum raster exceeded a one-pixel budget.");
        foreach (var scale in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            try { document.Render(0, scale); throw new InvalidOperationException("Invalid scale accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try { document.Render(0, 2); }
            catch (ObjectDisposedException) { }
        })).ToArray();
        document.Dispose(); Task.WaitAll(tasks); document.Dispose();
        try { document.Render(0, 1); throw new InvalidOperationException("Disposed native PDF rendered."); }
        catch (ObjectDisposedException) { }
    }

    private static async Task LoadingRace()
    {
        var first = SaveFixture(1);
        var newest = SaveFixture(7);
        using var preview = new PdfPreview();
        try
        {
            await preview.LoadAsync(first);
            var pending = preview.LoadAsync(newest);
            preview.SetZoom(1.4); preview.FitWidth(); preview.SetZoom(.8);
            await pending;
            Require(preview.PageCount == 7, "Resizing invalidated the pending document load.");
            var layouts = (IList)Field(preview, "_layouts");
            Require(layouts.Count == 7, "Loading before layout did not size every page.");
            var older = preview.LoadAsync(newest);
            var latest = preview.LoadAsync(first);
            await Task.WhenAll(older, latest);
            Require(preview.PageCount == 1, "An older concurrent load replaced the latest requested PDF.");
        }
        finally { System.IO.File.Delete(first); System.IO.File.Delete(newest); }
    }

    private static async Task Virtualization()
    {
        var path = SaveFixture(250);
        using var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            await Render(preview);
            var visuals = (IDictionary)Field(preview, "_visuals");
            Require(visuals.Count is > 0 and < 10, "Preview allocated page visuals outside the nearby viewport.");
            var scroll = (ScrollViewer)Field(preview, "_scroll");
            scroll.ScrollToBottom(); window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background);
            await Render(preview);
            visuals = (IDictionary)Field(preview, "_visuals");
            Require(visuals.Contains(249) && visuals.Count < 10 && !visuals.Contains(0), "Scrolling did not retire old pages and realize the last page.");
            long pixels = 0;
            foreach (var visual in visuals.Values)
            {
                var image = (Image)visual!.GetType().GetProperty("Image")!.GetValue(visual)!;
                Require(image.Source is BitmapSource, "A visible page stayed blank after rendering.");
                var bitmap = (BitmapSource)image.Source;
                pixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
            }
            Require(pixels <= 16 * 1024 * 1024, "Cached page raster budget was exceeded.");
            preview.ShowLocation(new PdfLocation(1, 20, 30, 40, 30));
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); await Render(preview);
            Require(((IDictionary)Field(preview, "_visuals")).Contains(0), "Forward synchronization did not realize a remote page.");
        }
        finally { window.Close(); System.IO.File.Delete(path); }
    }

    private static async Task PreviewLifecycle()
    {
        var path = SaveFixture(50);
        var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            var pending = preview.LoadAsync(path); preview.Clear(); await pending;
            Require(preview.PageCount == 0, "Cleared preview adopted a pending load.");
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            var rendering = Render(preview); preview.Dispose(); await rendering;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(preview.PageCount == 0, "Disposal retained the document.");
            Require(!((DispatcherTimer)Field(preview, "_renderTimer")).IsEnabled, "Rendering restarted after disposal.");
            Require(((IDictionary)Field(preview, "_visuals")).Count == 0, "Disposal retained raster visuals.");
        }
        finally { preview.Dispose(); window.Close(); System.IO.File.Delete(path); }
    }

    private static Window Host(PdfPreview preview) => new()
    {
        Content = preview, Width = 680, Height = 600, Left = -10000, Top = -10000,
        Opacity = 0, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
    };

    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static Task Render(PdfPreview preview) => (Task)typeof(PdfPreview).GetMethod("RenderVisibleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(preview, null)!;
    private static string SaveFixture(int pages, int rotation = 0, bool crop = false, bool varied = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"texeed-pdf-test-{Guid.NewGuid():N}.pdf");
        System.IO.File.WriteAllBytes(path, Fixture(pages, rotation, crop, varied)); return path;
    }

    private static byte[] Fixture(int pages, int rotation = 0, bool crop = false, bool varied = false, string? pageContent = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages} /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{3 + i * 2} 0 R"))}] >>"
        };
        for (var i = 0; i < pages; i++)
        {
            var width = varied && i % 2 != 0 ? 360 : 200;
            var cropBox = crop ? $"/CropBox [10 20 {width - 10} 280]" : "";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} 300] /Rotate {rotation} {cropBox} /Resources << >> /Contents {4 + i * 2} 0 R >>");
            var content = pageContent ?? "1 0 0 rg\n20 240 40 30 re f\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        using var stream = new MemoryStream();
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
