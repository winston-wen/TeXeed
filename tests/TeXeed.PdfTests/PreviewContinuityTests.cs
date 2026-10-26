using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task ZoomContinuity()
    {
        var path = SaveFixture(1);
        using var preview = new PdfPreview();
        var window = Host(preview);
        window.Show();
        try
        {
            await preview.LoadAsync(path);
            preview.SetZoom(.8);
            preview.ShowLocation(new PdfLocation(1, 40, 45), highlight: false, viewportFraction: .5);
            window.UpdateLayout(); await Render(preview);
            var visual = ((IDictionary)Field(preview, "_visuals"))[0]!;
            var image = (Image)visual.GetType().GetProperty("Image")!.GetValue(visual)!;
            var initial = (BitmapSource)image.Source;
            var blanked = false;
            var sourceProperty = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            EventHandler sourceChanged = (_, _) => blanked |= image.Source is null;
            sourceProperty.AddValueChanged(image, sourceChanged);
            try
            {
                var pointer = PagePointOnViewport(preview, 0, new Point(40, 45));
                preview.SetZoom(1.5, pointer); window.UpdateLayout();
                Require(!blanked && ReferenceEquals(image.Source, initial) && image.IsVisible,
                    "Zoom cleared the previous page before its replacement raster was ready.");
                SamePoint(RenderedPointOnViewport(preview, 0, new Point(40, 45)), pointer,
                    "The retained page image did not scale around the pointer immediately");
                var pending = Render(preview);
                Require(!blanked && image.Source is not null, "Starting a render blanked the visible page.");
                preview.SetZoom(.6, pointer);
                preview.SetZoom(2.1, pointer);
                await pending;
                Require(!blanked && image.Source is not null, "Superseding an in-flight zoom left a blank frame.");
                await Render(preview);
                var latest = image.Source as BitmapSource ?? throw new InvalidOperationException("Final zoom raster is missing.");
                var expectedWidth = (int)Math.Floor(Layout(preview, 0).Size.Width * VisualTreeHelper.GetDpi(preview).DpiScaleX);
                Require(!ReferenceEquals(latest, initial) && Math.Abs(latest.PixelWidth - expectedWidth) <= 1,
                    "The final zoom retained a low-resolution or stale raster.");
                preview.FitWidth(); window.UpdateLayout();
                Require(!blanked && image.Source is not null, "Fit width cleared the displayed page.");
                await Render(preview);
                window.Width = 480; window.UpdateLayout();
                Require(!blanked && image.Source is not null, "Resizing the preview cleared the displayed page.");
                await Render(preview);
                Require(!blanked, "A raster replacement briefly assigned a null image source.");
            }
            finally { sourceProperty.RemoveValueChanged(image, sourceChanged); }
        }
        finally { window.Close(); File.Delete(path); }
    }
}
