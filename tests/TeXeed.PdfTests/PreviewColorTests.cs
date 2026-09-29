using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static void InvertedRaster()
    {
        using var document = new PdfDocument(Fixture(1));
        var normal = document.Render(0, 1); var dark = document.Render(0, 1, invertColors: true);
        var expected = Pixels(normal); var actual = Pixels(dark);
        Require(Pixels(PdfDocument.ApplyDarkColors(normal)).SequenceEqual(actual), "Recoloring differs from native dark rendering");
        Require(Pixels(normal).SequenceEqual(expected), "Recoloring modified its source bitmap");
        var samples = BitmapSource.Create(3, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 128, 128, 128, 123 }, 12);
        var mapped = Pixels(PdfDocument.ApplyDarkColors(samples));
        Require(mapped.Take(4).SequenceEqual(new byte[] { 37, 37, 44, 255 }), "White paper is not Ristretto brown-black");
        Require(mapped.Skip(4).Take(4).SequenceEqual(new byte[] { 200, 204, 216, 255 }), "Black ink is not soft warm white");
        Require(mapped[8] > 37 && mapped[8] < 200 && mapped[11] == 123, "Gray edges or alpha were lost");
        var ramp = Enumerable.Range(0, 256).SelectMany(v => new byte[] { (byte)v, (byte)v, (byte)v, 255 }).ToArray();
        var smooth = Pixels(PdfDocument.ApplyDarkColors(BitmapSource.Create(256, 1, 96, 96, PixelFormats.Bgra32, null, ramp, 1024)));
        for (var x = 1; x < 256; x++)
            for (var c = 0; c < 3; c++)
                Require(smooth[x * 4 + c] <= smooth[(x - 1) * 4 + c] && smooth[(x - 1) * 4 + c] - smooth[x * 4 + c] <= 1,
                    "Grayscale mapping has a discontinuity or reverses the antialiasing gradient");
    }

    private static async Task InversionContinuity()
    {
        var path = SaveFixture(8); var originalFile = File.ReadAllBytes(path);
        using var preview = new PdfPreview { InvertColors = true };
        var window = Host(preview);
        BitmapSource FirstRaster()
        {
            var visual = ((IDictionary)Field(preview, "_visuals")).Values.Cast<object>().First();
            return (BitmapSource)((Image)visual.GetType().GetProperty("Image")!.GetValue(visual)!).Source;
        }
        void CheckColors(bool inverted)
        {
            foreach (var visual in ((IDictionary)Field(preview, "_visuals")).Values.Cast<object>())
            {
                var image = (Image)visual.GetType().GetProperty("Image")!.GetValue(visual)!;
                var pixel = new byte[4]; ((BitmapSource)image.Source).CopyPixels(new Int32Rect(1, 1, 1, 1), pixel, 4, 0);
                Require(pixel.SequenceEqual(inverted ? new byte[] { 37, 37, 44, 255 } : new byte[] { 255, 255, 255, 255 }), "Visible page has stale colors");
            }
        }
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout(); await Render(preview); CheckColors(true);
            preview.ShowLocation(new PdfLocation(3, 80, 100), viewportFraction: .5); window.UpdateLayout(); await Render(preview);
            var point = PagePointOnViewport(preview, 2, new Point(80, 100));
            var source = FirstRaster(); preview.InvertColors = false;
            Require(ReferenceEquals(source, FirstRaster()), "Switching colors blanked the existing bitmap");
            await Render(preview); CheckColors(false);
            var originalRaster = Pixels(FirstRaster());
            for (var repeat = 0; repeat < 3; repeat++)
            {
                preview.InvertColors = true; await Render(preview); CheckColors(true);
                preview.InvertColors = false; await Render(preview); CheckColors(false);
                Require(Pixels(FirstRaster()).SequenceEqual(originalRaster), "Repeated color switches accumulated color loss");
            }
            SamePoint(PagePointOnViewport(preview, 2, new Point(80, 100)), point, "Color switch moved the reading position");
            preview.InvertColors = true; var pending = Render(preview);
            preview.InvertColors = false; preview.SetZoom(2, point); preview.InvertColors = true;
            await pending; await Render(preview); CheckColors(true);
            SamePoint(PagePointOnViewport(preview, 2, new Point(80, 100)), point, "Color/zoom race moved the pointer anchor");
            var scroll = (ScrollViewer)Field(preview, "_scroll"); scroll.ScrollToBottom(); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background); await Render(preview); CheckColors(true);
            await preview.LoadAsync(path); window.UpdateLayout(); await Render(preview); CheckColors(true);
            Require(File.ReadAllBytes(path).SequenceEqual(originalFile), "Display inversion modified the PDF file");
        }
        finally { window.Close(); File.Delete(path); }
    }
}
