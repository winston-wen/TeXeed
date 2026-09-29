using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Services;

internal sealed partial class PdfDocument
{
    public IReadOnlyList<PdfPageCrop>? Crops { get; private set; }

    public void PrepareCrops(CancellationToken cancellation)
    {
        if (Crops is not null) return;
        var crops = new List<PdfPageCrop>(Pages.Count);
        for (var index = 0; index < Pages.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            // Analyze the original white-background raster once, independent of zoom and night colors.
            var raster = Render(index, 1, 1_000_000);
            crops.Add(PdfPageCrop.Detect(raster, Pages[index], cancellation, ProtectedGraphicBands(index, cancellation), TextArea(index)));
        }
        cancellation.ThrowIfCancellationRequested();
        Crops = crops;
    }

    private Rect? TextArea(int index)
    {
        if (_texLayouts.Count != Pages.Count) return null;
        var layout = _texLayouts[index];
        // Transform the recorded physical page through the PDF's rotation and CropBox mapping.
        var paper = new Rect(0, 0, layout.PaperWidth, layout.PaperHeight);
        var matrix = _viewToPage[index]; matrix.Invert();
        matrix.Scale(Pages[index].Width, Pages[index].Height);
        var viewPaper = Rect.Transform(paper, matrix);
        if (layout.PaperWidth > 0 && (Math.Abs(viewPaper.Width - Pages[index].Width) > 2 || Math.Abs(viewPaper.Height - Pages[index].Height) > 2))
            return null;
        // A quarter-turn rotates horizontal margins into vertical ones; keep those pages conservative.
        if (Math.Abs(matrix.M12) > .01 || Math.Abs(matrix.M21) > .01) return null;
        if (layout.Left < 0 || layout.Width <= 0 || !double.IsFinite(layout.Left + layout.Width)) return null;
        var area = Rect.Transform(new Rect(layout.Left, 0, layout.Width, Math.Max(1, layout.PaperHeight)), matrix);
        return area.Left >= -1 && area.Right <= Pages[index].Width + 1 ? area : null;
    }

    private IReadOnlyList<PdfCropBand> ProtectedGraphicBands(int index, CancellationToken cancellation)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) throw new IOException("无法分析页面图形。");
            try
            {
                Native.FPDFPage_GetMediaBox(page, out var mediaLeft, out _, out _, out var mediaTop);
                var bands = new List<PdfCropBand>();
                var count = Native.FPDFPage_CountObjects(page);
                for (var i = 0; i < count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var item = Native.FPDFPage_GetObject(page, i);
                    // Text is segmented by visible rows. Paths, images, shading and forms retain their internal geometry.
                    if (Native.FPDFPageObj_GetType(item) is < 2 or > 5
                        || Native.FPDFPageObj_GetBounds(item, out var left, out var bottom, out var right, out var top) == 0
                        || !float.IsFinite(left + bottom + right + top)) continue;
                    var corners = new[] { new Point(left, top), new Point(right, top), new Point(left, bottom), new Point(right, bottom) }
                        .Select(p => PageToView(index, new Point(p.X - mediaLeft, mediaTop - p.Y), Pages[index])).ToArray();
                    if (corners.Max(p => p.X) <= 0 || corners.Min(p => p.X) >= Pages[index].Width) continue;
                    var first = Math.Clamp(corners.Min(p => p.Y), 0, Pages[index].Height);
                    var last = Math.Clamp(corners.Max(p => p.Y), 0, Pages[index].Height);
                    if (last - first > 36) bands.Add(new(first, last));
                }
                return bands;
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    private BitmapSource RenderCropped(int index, double pixelsPerPoint, int maxPixels, bool inverted, PdfPageCrop crop)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);
            var original = Pages[index];
            var scale = Math.Min(pixelsPerPoint, Math.Min(3200 / Math.Max(crop.Width, crop.Height),
                Math.Min(32768 / Math.Max(original.Width, original.Height), Math.Sqrt(maxPixels / (crop.Width * crop.Height)))));
            var width = Math.Max(1, (int)Math.Floor(crop.Width * scale));
            var height = Math.Max(1, (int)Math.Floor(crop.Height * scale));
            if ((long)width * height > maxPixels)
            {
                if (width >= height) width = Math.Max(1, maxPixels / height); else height = Math.Max(1, maxPixels / width);
            }
            var pixels = new byte[width * height * 4];
            var fullWidth = Math.Max(1, (int)Math.Round(original.Width * width / crop.Width));
            var fullHeight = Math.Max(1, (int)Math.Round(original.Height * height / crop.Height));
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) throw new IOException("无法渲染裁边页面。");
            try
            {
                var offset = 0d; var start = 0;
                foreach (var band in crop.Bands)
                {
                    offset += band.Height;
                    var end = Math.Min(height, (int)Math.Round(offset / crop.Height * height));
                    var bandHeight = end - start;
                    if (bandHeight <= 0) continue;
                    var bitmap = Native.FPDFBitmap_Create(width, bandHeight, 1);
                    if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("裁边页面缓冲区分配失败。");
                    try
                    {
                        Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, bandHeight, 0xFFFFFFFF);
                        Native.FPDF_RenderPageBitmap(bitmap, page,
                            -(int)Math.Round(crop.Left / original.Width * fullWidth),
                            -(int)Math.Round(band.Top / original.Height * fullHeight), fullWidth, fullHeight, 0, 3);
                        var buffer = Native.FPDFBitmap_GetBuffer(bitmap); var stride = Native.FPDFBitmap_GetStride(bitmap);
                        for (var row = 0; row < bandHeight; row++)
                            Marshal.Copy(buffer + row * stride, pixels, (start + row) * width * 4, width * 4);
                    }
                    finally { Native.FPDFBitmap_Destroy(bitmap); }
                    start = end;
                }
                if (inverted) ApplyDarkPalette(pixels);
                var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                result.Freeze(); return result;
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }
}
