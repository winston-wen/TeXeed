using System.Windows;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Services;

internal readonly record struct PdfCropBand(double Top, double Bottom)
{
    public double Height => Bottom - Top;
}

/// <summary>A piecewise mapping in rotated, visible PDF page points; never edits the PDF.</summary>
internal sealed class PdfPageCrop(double left, double width, IReadOnlyList<PdfCropBand> bands)
{
    public double Left { get; } = left;
    public double Width { get; } = width;
    public IReadOnlyList<PdfCropBand> Bands { get; } = bands;
    public double Height { get; } = bands.Sum(b => b.Height);
    public Size Size => new(Width, Height);

    public Point ToCropped(Point point)
    {
        var y = 0d;
        foreach (var band in Bands)
        {
            if (point.Y <= band.Bottom) return new Point(Math.Clamp(point.X - Left, 0, Width), y + Math.Clamp(point.Y - band.Top, 0, band.Height));
            y += band.Height;
        }
        return new Point(Math.Clamp(point.X - Left, 0, Width), Height);
    }

    public Point ToSource(Point point)
    {
        var remaining = Math.Clamp(point.Y, 0, Height);
        foreach (var band in Bands)
        {
            if (remaining < band.Height) return new Point(Left + Math.Clamp(point.X, 0, Width), band.Top + remaining);
            remaining -= band.Height;
        }
        return new Point(Left + Math.Clamp(point.X, 0, Width), Bands[^1].Bottom);
    }

    public static PdfPageCrop Detect(BitmapSource image, Size sourceSize, CancellationToken cancellation, IReadOnlyList<PdfCropBand>? protectedBands = null)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight]; image.CopyPixels(pixels, stride, 0);
        var occupied = new bool[image.PixelHeight];
        var minX = image.PixelWidth; var maxX = -1;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var offset = y * stride + x * 4;
                if (pixels[offset] >= 250 && pixels[offset + 1] >= 250 && pixels[offset + 2] >= 250) continue;
                occupied[y] = true; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            }
        }
        // Keep genuinely empty (or almost-white) pages intact rather than guessing away content.
        if (maxX < minX) return new PdfPageCrop(0, sourceSize.Width, [new(0, sourceSize.Height)]);
        const double padding = 6, minimumGap = 36;
        var sx = sourceSize.Width / image.PixelWidth; var sy = sourceSize.Height / image.PixelHeight;
        var first = Array.FindIndex(occupied, value => value);
        var final = Array.FindLastIndex(occupied, value => value);
        // Whitespace inside a graphic/form is part of that object, not a page gap.
        // Protect it without adding new outer margins to the detected visible bounds.
        foreach (var band in protectedBands ?? [])
            for (var row = Math.Max(first, (int)Math.Floor(band.Top / sy)); row <= Math.Min(final, (int)Math.Ceiling(band.Bottom / sy)); row++)
                occupied[row] = true;
        var last = first; var start = first; var bands = new List<PdfCropBand>();
        for (var row = first + 1; row < occupied.Length; row++)
        {
            if (!occupied[row]) continue;
            if ((row - last - 1) * sy > minimumGap)
            {
                bands.Add(new(Math.Max(0, start * sy - padding), Math.Min(sourceSize.Height, (last + 1) * sy + padding)));
                start = row;
            }
            last = row;
        }
        bands.Add(new(Math.Max(0, start * sy - padding), Math.Min(sourceSize.Height, (last + 1) * sy + padding)));
        // Preserve the original horizontal center, including short-line pages and centered footers.
        // The rightmost ink only limits trimming so overhanging content cannot be clipped.
        var leftMargin = Math.Max(0, minX * sx - padding);
        var rightMargin = Math.Max(0, sourceSize.Width - (maxX + 1) * sx - padding);
        var trim = Math.Min(leftMargin, rightMargin);
        return new PdfPageCrop(trim, Math.Max(1, sourceSize.Width - 2 * trim), bands);
    }
}
