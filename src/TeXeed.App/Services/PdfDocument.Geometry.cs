using System.Collections.Concurrent;
using System.IO;

namespace TeXeed.App.Services;

internal sealed partial class PdfDocument
{
    private sealed record GraphicPageResult((double Width, double Height)? Size);
    private static readonly ConcurrentDictionary<(string Path, int Page, long Length, long Ticks), GraphicPageResult> GraphicPageSizes = new();

    // Graphic synchronization uses the physical, unrotated page. The view transform
    // confirms PDFium is not applying an inherited CropBox or a page rotation.
    internal static (double Width, double Height)? GraphicPageSize(string path, int pageNumber)
    {
        try
        {
            if (pageNumber < 1) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 128L * 1024 * 1024) return null;
            var key = (info.FullName, pageNumber, info.Length, info.LastWriteTimeUtc.Ticks);
            if (GraphicPageSizes.TryGetValue(key, out var cached)) return cached.Size;
            using var document = new PdfDocument(File.ReadAllBytes(info.FullName));
            var result = document.UncroppedPageSize(pageNumber - 1);
            if (GraphicPageSizes.Count >= 128) GraphicPageSizes.Clear();
            GraphicPageSizes[key] = new(result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return null; }
    }

    private (double Width, double Height)? UncroppedPageSize(int index)
    {
        lock (Gate)
        {
            if (index < 0 || index >= Pages.Count || _document == IntPtr.Zero) return null;
            var page = Native.FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero) return null;
            try
            {
                if (Native.FPDFPage_GetRotation(page) != 0 || Native.FPDF_GetPageBoundingBox(page, out var shown) == 0) return null;
                // GetMediaBox only reads the page dictionary and misses inherited boxes.
                // In this in-memory copy, temporarily expand the crop and reload the page;
                // the resulting intersection exposes the effective inherited media box.
                Native.FPDFPage_SetCropBox(page, -1_000_000, -1_000_000, 1_000_000, 1_000_000);
                Native.FPDF_ClosePage(page);
                page = Native.FPDF_LoadPage(_document, index);
                if (page == IntPtr.Zero || Native.FPDF_GetPageBoundingBox(page, out var media) == 0 ||
                    Math.Abs(media.Left) > .01 || Math.Abs(media.Bottom) > .01 || media.Right <= 0 || media.Top <= 0 ||
                    Math.Abs(shown.Left - media.Left) > .01 || Math.Abs(shown.Bottom - media.Bottom) > .01 ||
                    Math.Abs(shown.Right - media.Right) > .01 || Math.Abs(shown.Top - media.Top) > .01) return null;
                var map = _viewToPage[index];
                if (Math.Abs(map.OffsetX) > .01 || Math.Abs(map.OffsetY) > .01 ||
                    Math.Abs(map.M12) > .01 || Math.Abs(map.M21) > .01 ||
                    Math.Abs(map.M11 - media.Right) > .01 || Math.Abs(map.M22 - media.Top) > .01) return null;
                return (media.Right, media.Top);
            }
            finally { if (page != IntPtr.Zero) Native.FPDF_ClosePage(page); }
        }
    }
}
