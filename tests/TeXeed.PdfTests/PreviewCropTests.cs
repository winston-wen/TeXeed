using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private const string CropContent = "1 0 0 rg\n40 260 120 12 re f\n0 0 0 rg\n40 230 120 2 re f\n0 0 1 rg\n95 10 10 8 re f\n";

    private static void CropGeometry()
    {
        using var document = new PdfDocument(Fixture(2, pageContent: CropContent));
        document.PrepareCrops(CancellationToken.None);
        var crop = document.Crops![0];
        Near(crop.Left, 34, "Left crop padding"); Near(crop.Width, 132, "Horizontal margins");
        Require(crop.Bands.Count == 2 && crop.Height < 85, "Body/footer whitespace was not compressed or normal line spacing was removed");
        foreach (var source in new[] { new Point(50, 35), new Point(50, 69), new Point(100, 286) })
        {
            SamePoint(crop.ToSource(crop.ToCropped(source)), source, "Crop mapping round-trip");
            var mapped = crop.ToCropped(source); var rendered = document.Render(0, 2, crop: crop);
            var pixel = new byte[4]; rendered.CopyPixels(new Int32Rect((int)(mapped.X * 2), (int)(mapped.Y * 2), 1, 1), pixel, 4, 0);
            var expected = source.Y > 200 ? new byte[] { 255, 0, 0, 255 } : source.Y > 60 ? new byte[] { 0, 0, 0, 255 } : new byte[] { 0, 0, 255, 255 };
            Require(pixel.SequenceEqual(expected), "A retained body mark or footer was clipped, displaced or recolored");
        }
        var tiny = document.Render(0, 500, 1, crop: crop);
        Require(tiny.PixelWidth * tiny.PixelHeight == 1, "Crop raster ignored its memory budget");
        using var blank = new PdfDocument(Fixture(1, pageContent: "")); blank.PrepareCrops(CancellationToken.None);
        Require(blank.Crops![0].Size == blank.Pages[0], "A blank page must have a safe nonempty layout");
        using var colored = new PdfDocument(Fixture(1, pageContent: "0.96 g 0 0 200 300 re f")); colored.PrepareCrops(CancellationToken.None);
        Require(colored.Crops![0].Size == colored.Pages[0], "Colored page background was incorrectly discarded");
        // A single chart/path may have disconnected strokes with a large intentional gap.
        // Keep their spacing even though a raster-only detector would collapse it.
        using var graphic = new PdfDocument(Fixture(1, pageContent: "0 0 0 rg 40 250 120 5 re 40 90 120 5 re f"));
        graphic.PrepareCrops(CancellationToken.None);
        Require(graphic.Crops![0].Bands.Count == 1 && graphic.Crops[0].Height >= 165,
            "Internal whitespace changed a vector graphic's geometry");
        foreach (var rotation in new[] { 90, 180, 270 })
        {
            using var rotated = new PdfDocument(Fixture(1, rotation, crop: true, pageContent: CropContent));
            rotated.PrepareCrops(CancellationToken.None); var rc = rotated.Crops![0];
            var source = new Point(50, 35);
            var view = rotated.PageToView(0, source, rotated.Pages[0]);
            SamePoint(rotated.ViewToPage(0, rc.ToSource(rc.ToCropped(view)), rotated.Pages[0]), source, "Rotated crop inverse");
            var raster = rotated.Render(0, 2, crop: rc); var mapped = rc.ToCropped(view);
            var pixel = new byte[4]; raster.CopyPixels(new Int32Rect((int)(mapped.X * 2), (int)(mapped.Y * 2), 1, 1), pixel, 4, 0);
            Require(pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 }), "Rotated cropped raster lost a content mark");
        }
    }

    private static async Task CropPreview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"texeed-crop-test-{Guid.NewGuid():N}.pdf");
        var original = Fixture(60, pageContent: CropContent); File.WriteAllBytes(path, original);
        using var preview = new PdfPreview { AutoCrop = true };
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout(); await Render(preview);
            var doc = (PdfDocument)Field(preview, "_document"); var scroll = (ScrollViewer)Field(preview, "_scroll");
            Require(doc.Crops!.Count == 60, "Not all pages were analyzed");
            Require(Layout(preview, 0).Size.Width > preview.ActualWidth - 75 && scroll.ScrollableWidth < 1, "Cropped content did not automatically fill the viewport width");
            Require(Layout(preview, 1).Top - Layout(preview, 0).Size.Height <= 10, "Cropped pages are not continuously stacked");
            Require(((IDictionary)Field(preview, "_visuals")).Count < 12, "Crop mode defeated page virtualization");
            var source = new Point(100, 286);
            preview.ShowLocation(new PdfLocation(20, source.X, source.Y, 2, 2), viewportFraction: .5); window.UpdateLayout(); await Render(preview);
            var pointer = PagePointOnViewport(preview, 19, source);
            Require(Math.Abs(pointer.Y - scroll.ViewportHeight / 2) < .25, "Footer no longer centers after blank-space compression");
            var display = (Point)typeof(PdfPreview).GetMethod("PageToDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, [19, source])!;
            var inverse = (Point)typeof(PdfPreview).GetMethod("DisplayToPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, [19, display])!;
            SamePoint(inverse, source, "Preview inverse source coordinate");
            foreach (var zoom in new[] { 1d, 4d, 2d, 7d })
            {
                preview.SetZoom(zoom, pointer); window.UpdateLayout(); await Render(preview);
                SamePoint(PagePointOnViewport(preview, 19, source), pointer, "Cropped pointer zoom");
            }
            preview.FitWidth(); window.UpdateLayout(); await Render(preview);
            pointer = PagePointOnViewport(preview, 19, source);
            await preview.LoadAsync(path); window.UpdateLayout(); await Render(preview);
            SamePoint(PagePointOnViewport(preview, 19, source), pointer, "Cropped PDF refresh lost reading position");
            preview.InvertColors = true; await Render(preview);
            SamePoint(PagePointOnViewport(preview, 19, source), pointer, "Night colors changed crop coordinates");
            preview.AutoCrop = false; window.UpdateLayout(); await Render(preview);
            Require(Layout(preview, 0).Size.Height / Layout(preview, 0).Size.Width > 1.4, "Disabling crop did not restore full page dimensions");
            preview.AutoCrop = true; window.UpdateLayout(); await Render(preview);
            Require(Layout(preview, 0).Size.Height / Layout(preview, 0).Size.Width < .7, "Re-enabling crop lost its cached geometry");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "Automatic cropping changed the PDF file");
            preview.Clear(); preview.AutoCrop = false; await preview.LoadAsync(path); preview.AutoCrop = true;
            var analysis = (Task)Field(preview, "_cropTask"); preview.Clear(); await analysis;
            Require(preview.PageCount == 0 && ((IDictionary)Field(preview, "_visuals")).Count == 0, "Canceled crop analysis revived a cleared document");
        }
        finally { window.Close(); File.Delete(path); }
    }
}
