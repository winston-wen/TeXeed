using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPdfColorsAsync(MainWindow window, string output)
    {
        var preview = (PdfPreview)window.FindName("Preview");
        var toggle = (CheckBox)window.FindName("InvertPdfBox");
        var pdf = (string)Field(window, "_pdfPath")!; var original = File.ReadAllBytes(pdf);
        var font = ((AppSettings)Field(window, "_settings")!).EditorFontFamily;
        foreach (var inverted in new[] { true, false, true })
        {
            Invoke(window, "ApplyPreviewOptions", new PreviewOptions(preview.Zoom, preview.IsAutoWidth, ((AppSettings)Field(window, "_settings")!).FollowMode, inverted, preview.AutoCrop));
            await WaitForPreviewRenderedAsync(window);
            if (preview.InvertColors != inverted) throw new Exception("PDF color toggle did not apply");
            var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json")))!;
            if (persisted.InvertPdfColors != inverted || persisted.EditorFontFamily != font) throw new Exception("Color switch lost its preference or another setting");
            foreach (var image in Descendants<Image>(preview).Where(i => i.Source is BitmapSource))
            {
                var pixel = new byte[4]; ((BitmapSource)image.Source).CopyPixels(new Int32Rect(1, 1, 1, 1), pixel, 4, 0);
                if (!pixel.SequenceEqual(inverted ? new byte[] { 37, 37, 44, 255 } : new byte[] { 255, 255, 255, 255 }))
                    throw new Exception("PDF paper has the wrong display color");
            }
            Capture(window, Path.Combine(output, inverted ? "pdf-inverted.png" : "pdf-original.png"));
        }
        if (!File.ReadAllBytes(pdf).SequenceEqual(original)) throw new Exception("Preview colors modified the exported source PDF");
        Console.WriteLine("PASS: PDF inversion settings, persistent preference, original file preservation");
    }
}
