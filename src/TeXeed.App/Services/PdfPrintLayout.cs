using System.Printing;
using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Services;

// All dimensions here are WPF DIPs (96/in); PDF source sizes are points (72/in).
internal sealed record PdfPrintLayout(Size Paper, Rect Printable, bool ActualSize = false)
{
    internal static readonly Size A4 = new(210 * 96 / 25.4, 297 * 96 / 25.4);

    internal static PdfPrintLayout FromPrinter(PrintDialog dialog, Size fallback, bool actualSize)
    {
        var ticket = dialog.PrintTicket;
        var media = ticket?.PageMediaSize;
        var width = media?.Width ?? fallback.Width;
        var height = media?.Height ?? fallback.Height;
        var landscape = ticket?.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
        // PrintTicket media sizes are unrotated. Capabilities below use the
        // requested orientation, including the hardware's unprintable margins.
        if (landscape && width < height || !landscape && width > height) (width, height) = (height, width);
        var paper = new Size(width, height);
        var area = Inset(paper, 24); // A safe preview fallback when no printer exists.
        if (dialog.PrintQueue is { } queue)
        {
            var capability = queue.GetPrintCapabilities(ticket);
            if (capability.OrientedPageMediaWidth is > 0 && capability.OrientedPageMediaHeight is > 0)
                paper = new Size(capability.OrientedPageMediaWidth.Value, capability.OrientedPageMediaHeight.Value);
            if (capability.PageImageableArea is { } imageable)
                area = new Rect(imageable.OriginWidth, imageable.OriginHeight, imageable.ExtentWidth, imageable.ExtentHeight);
            else area = Inset(paper, 24);
        }
        area.Intersect(new Rect(paper));
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) throw new InvalidOperationException("打印机未提供有效的可打印区域。");
        return new(paper, area, actualSize);
    }

    internal Rect Place(Size pdfPoints)
    {
        var width = pdfPoints.Width * 96 / 72;
        var height = pdfPoints.Height * 96 / 72;
        var scale = ActualSize ? 1 : Math.Min(Printable.Width / width, Printable.Height / height);
        return new Rect(Printable.X + (Printable.Width - width * scale) / 2,
            Printable.Y + (Printable.Height - height * scale) / 2, width * scale, height * scale);
    }

    internal static Rect Inset(Size paper, double margin) =>
        new(margin, margin, Math.Max(1, paper.Width - 2 * margin), Math.Max(1, paper.Height - 2 * margin));

    internal static int[] Pages(int count, PageRangeSelection selection, PageRange range, int currentPage)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (selection == PageRangeSelection.CurrentPage) return [Math.Clamp(currentPage, 1, count) - 1];
        if (selection == PageRangeSelection.AllPages) return Enumerable.Range(0, count).ToArray();
        if (selection != PageRangeSelection.UserPages || range.PageFrom < 1 || range.PageTo < range.PageFrom || range.PageTo > count)
            throw new ArgumentException($"打印页码必须在 1–{count} 内，且起始页不能大于结束页。");
        return Enumerable.Range(range.PageFrom - 1, range.PageTo - range.PageFrom + 1).ToArray();
    }
}
