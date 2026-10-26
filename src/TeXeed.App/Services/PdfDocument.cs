using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.Core;

namespace TeXeed.App.Services;

internal enum PdfPrintContent { All, Document, Annotations }

// PDFium is not thread safe: every call, including close, shares one gate.
internal sealed partial class PdfDocument : IDisposable
{
    internal static readonly Color DarkPaperColor = Color.FromRgb(44, 37, 37); // Ristretto #2C2525
    internal static readonly Color DarkInkColor = Color.FromRgb(216, 204, 200); // Softer warm ink #D8CCC8
    private static readonly byte[] DarkBlue = ColorRamp(DarkPaperColor.B, DarkInkColor.B);
    private static readonly byte[] DarkGreen = ColorRamp(DarkPaperColor.G, DarkInkColor.G);
    private static readonly byte[] DarkRed = ColorRamp(DarkPaperColor.R, DarkInkColor.R);
    private static readonly object Gate = new();
    private static bool _initialized;
    private IntPtr _document;
    private IntPtr _memory;
    private readonly List<Matrix> _viewToPage = [];
    public IReadOnlyList<Size> Pages { get; }
    public string Fingerprint { get; }

    private readonly IReadOnlyList<TexPageLayout> _texLayouts;

    public PdfDocument(byte[] data, IReadOnlyList<TexPageLayout>? texLayouts = null)
    {
        _texLayouts = texLayouts ?? [];
        ArgumentNullException.ThrowIfNull(data);
        Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        if (data.Length == 0) throw new IOException("PDF 文件为空。");
        lock (Gate)
        {
            if (!_initialized) { Native.FPDF_InitLibrary(); _initialized = true; }
            _memory = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, _memory, data.Length);
            _document = Native.FPDF_LoadMemDocument64(_memory, (nuint)data.Length, IntPtr.Zero);
            if (_document == IntPtr.Zero) { var error = Native.FPDF_GetLastError(); Dispose(); throw new IOException($"PDF 无法打开（PDFium 错误 {error}）。"); }
            try
            {
                var count = Native.FPDF_GetPageCount(_document);
                if (count <= 0 || count > 10000) throw new IOException("PDF 页数无效或超过 10,000 页。");
                var pages = new List<Size>(count);
                for (var i = 0; i < count; i++)
                {
                    var page = Native.FPDF_LoadPage(_document, i);
                    if (page == IntPtr.Zero) throw new IOException($"无法读取 PDF 第 {i + 1} 页。");
                    try
                    {
                        var width = Native.FPDF_GetPageWidth(page);
                        var height = Native.FPDF_GetPageHeight(page);
                        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                            throw new IOException($"PDF 第 {i + 1} 页尺寸无效。");
                        pages.Add(new Size(width, height));
                        _viewToPage.Add(CoordinateTransform(page, height));
                    }
                    finally { Native.FPDF_ClosePage(page); }
                }
                Pages = pages;
            }
            catch { Dispose(); throw; }
        }
    }

    public BitmapSource Render(int index, double pixelsPerPoint, int maxPixels = 8 * 1024 * 1024, bool invertColors = false, PdfPageCrop? crop = null)
    {
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        if (maxPixels < 1) throw new ArgumentOutOfRangeException(nameof(maxPixels));
        if (crop is not null) return RenderCropped(index, pixelsPerPoint, maxPixels, invertColors, crop);
        return RenderRaster(index, pixelsPerPoint, maxPixels, invertColors, printing: false);
    }

    internal BitmapSource RenderForPrint(int index, double pixelsPerPoint, int maxPixels = 32 * 1024 * 1024, PdfPrintContent content = PdfPrintContent.All) =>
        RenderRaster(index, pixelsPerPoint, Math.Min(maxPixels, 32 * 1024 * 1024), invertColors: false, printing: true, content);

    private BitmapSource RenderRaster(int index, double pixelsPerPoint, int maxPixels, bool invertColors, bool printing, PdfPrintContent content = PdfPrintContent.All)
    {
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerPoint));
        if (maxPixels < 1) throw new ArgumentOutOfRangeException(nameof(maxPixels));
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);
            var size = Pages[index];
            var scale = Math.Min(pixelsPerPoint, Math.Min((printing ? 10000 : 3200) / Math.Max(size.Width, size.Height), Math.Sqrt(maxPixels / (size.Width * size.Height))));
            var width = Math.Max(1, (int)Math.Floor(size.Width * scale));
            var height = Math.Max(1, (int)Math.Floor(size.Height * scale));
            if ((long)width * height > maxPixels)
            {
                // A very thin page can hit the one-pixel minimum on its short
                // side, so enforce the area budget once more after rounding.
                if (width >= height) width = Math.Max(1, maxPixels / height);
                else height = Math.Max(1, maxPixels / width);
            }
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) throw new IOException("无法渲染 PDF 页面。");
            var bitmap = Native.FPDFBitmap_Create(width, height, 1);
            if (bitmap == IntPtr.Zero) { Native.FPDF_ClosePage(page); throw new OutOfMemoryException("PDF 页面渲染缓冲区分配失败。"); }
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                // Printing uses PDFium's print mode and annotations, without LCD
                // subpixel rendering or any preview recoloring/crop transforms.
                var hidden = new List<IntPtr>();
                try
                {
                    if (printing && content == PdfPrintContent.Annotations)
                        for (var i = 0; i < Native.FPDFPage_CountObjects(page); ++i)
                        {
                            var obj = Native.FPDFPage_GetObject(page, i);
                            if (Native.FPDFPageObj_GetIsActive(obj, out var active) == 0) throw new IOException("无法读取 PDF 内容状态。");
                            if (active == 0) continue;
                            if (Native.FPDFPageObj_SetIsActive(obj, 0) == 0) throw new IOException("无法分离 PDF 批注。");
                            hidden.Add(obj);
                        }
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, printing ? (content == PdfPrintContent.Document ? 0x800 : 0x801) : 3);
                }
                finally { foreach (var obj in hidden) Native.FPDFPageObj_SetIsActive(obj, 1); }
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[stride * height];
                Marshal.Copy(Native.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                if (invertColors) ApplyDarkPalette(pixels);
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                image.Freeze();
                return image;
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); Native.FPDF_ClosePage(page); }
        }
    }

    // Frozen rasters can be recolored on the worker thread without invoking PDFium again.
    internal static BitmapSource ApplyDarkColors(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) throw new ArgumentException("Expected a BGRA PDF raster.", nameof(source));
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        ApplyDarkPalette(pixels);
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte[] ColorRamp(byte paper, byte ink) => Enumerable.Range(0, 256)
        .Select(value => (byte)(ink - ((ink - paper) * value + 127) / 255)).ToArray();

    private static void ApplyDarkPalette(byte[] pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = DarkBlue[pixels[offset]];
            pixels[offset + 1] = DarkGreen[pixels[offset + 1]];
            pixels[offset + 2] = DarkRed[pixels[offset + 2]];
            // Alpha stays unchanged. Only the preview raster changes, never PDF bytes.
        }
    }

    // SyncTeX uses unrotated paper points from the media box's top-left. PDFium
    // maps through page rotation and crop boxes, so clicks and highlights must
    // use that same transform rather than assuming a simple width/height ratio.
    public Point ViewToPage(int index, Point point, Size viewSize)
    {
        ValidateView(viewSize);
        return _viewToPage[index].Transform(new Point(point.X / viewSize.Width, point.Y / viewSize.Height));
    }

    public Point PageToView(int index, Point point, Size viewSize)
    {
        ValidateView(viewSize);
        var transform = _viewToPage[index];
        transform.Invert();
        var normalized = transform.Transform(point);
        return new Point(normalized.X * viewSize.Width, normalized.Y * viewSize.Height);
    }

    private static void ValidateView(Size size)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    private static Matrix CoordinateTransform(IntPtr page, double height)
    {
        const int extent = 1_000_000;
        var left = 0f;
        var top = (float)height;
        if (Native.FPDFPage_GetMediaBox(page, out var mediaLeft, out _, out _, out var mediaTop) != 0)
        { left = mediaLeft; top = mediaTop; }
        Point Convert(int x, int y)
        {
            if (Native.FPDF_DeviceToPage(page, 0, 0, extent, extent, 0, x, y, out var px, out var py) == 0)
                throw new IOException("PDF 页面坐标转换失败。");
            return new Point(px - left, top - py);
        }
        var origin = Convert(0, 0);
        var across = Convert(extent, 0);
        var down = Convert(0, extent);
        var result = new Matrix(across.X - origin.X, across.Y - origin.Y,
            down.X - origin.X, down.Y - origin.Y, origin.X, origin.Y);
        if (!result.HasInverse) throw new IOException("PDF 页面坐标无效。");
        return result;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_document != IntPtr.Zero) { Native.FPDF_CloseDocument(_document); _document = IntPtr.Zero; }
            if (_memory != IntPtr.Zero) { Marshal.FreeHGlobal(_memory); _memory = IntPtr.Zero; }
        }
    }

    private static class Native
    {
        private const string Dll = "pdfium";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint length, IntPtr password);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDF_GetLastError();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetType(IntPtr pageObject);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern double FPDF_GetPageWidth(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern double FPDF_GetPageHeight(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_CountObjects(IntPtr page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetIsActive(IntPtr obj, out int active);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_SetIsActive(IntPtr obj, int active);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}
