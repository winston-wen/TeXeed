using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Services;

internal static class PdfPrintDrawing
{
    // The same frozen drawing is used by WPF screen preview and XPS output.
    // No cache retains the whole book: at most one face's source rasters live here.
    internal static DrawingGroup Render(PdfDocument document, PdfPrintFace face, PdfPrintOptions options, int dpi, CancellationToken token = default)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(face.Layout.Paper));
            if (face.Rotate180) dc.PushTransform(new RotateTransform(180, face.Layout.Paper.Width / 2, face.Layout.Paper.Height / 2));
            foreach (var item in face.Items)
            {
                token.ThrowIfCancellationRequested();
                var size = document.Pages[item.Page];
                var scale = item.Target.Width / item.Source.Width;
                var budget = (dpi <= 120 ? 4 : 32) * 1024 * 1024 / Math.Max(1, face.Items.Count);
                var image = document.RenderForPrint(item.Page, scale * dpi / 96, budget, options.Content);
                BitmapSource raster = image;
                if (options.Gray) { raster = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0); raster.Freeze(); }
                if (options.Monochrome) { raster = new FormatConvertedBitmap(image, PixelFormats.BlackWhite, null, 0); raster.Freeze(); }
                // The source crop is a real clipping boundary, not merely an
                // offset: actual-size view printing must not reveal its neighbors.
                dc.PushClip(new RectangleGeometry(Rect.Intersect(item.Clip, item.Target)));
                dc.DrawImage(raster, new Rect(item.Target.X - item.Source.X * scale, item.Target.Y - item.Source.Y * scale, size.Width * scale, size.Height * scale));
                if (item.Border) dc.DrawRectangle(null, new Pen(Brushes.Black, .5), item.Target);
                dc.Pop();
            }
            if (face.CutMarks)
            {
                var r = face.Layout.Printable; var pen = new Pen(Brushes.Gray, .5) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(null, pen, r);
                var inset = options.OverlapMm * 96 / 25.4;
                dc.DrawLine(pen, new Point(r.Right - inset, r.Top), new Point(r.Right - inset, r.Bottom));
                dc.DrawLine(pen, new Point(r.Left, r.Bottom - inset), new Point(r.Right, r.Bottom - inset));
            }
            if (face.Rotate180) dc.Pop();
        }
        drawing.Freeze(); return drawing;
    }
}
