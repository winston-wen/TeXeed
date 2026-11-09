using System.Windows;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;
public sealed partial class PdfPreview
{
    internal static IReadOnlyList<Rect> SelectionBands(Rect display, Size displayedPage, Size originalPage, PdfPageCrop? crop)
    {
        if (crop is null) return [new Rect(display.X / displayedPage.Width * originalPage.Width, display.Y / displayedPage.Height * originalPage.Height, display.Width / displayedPage.Width * originalPage.Width, display.Height / displayedPage.Height * originalPage.Height)];
        var x = crop.Left + display.X / displayedPage.Width * crop.Width; var width = display.Width / displayedPage.Width * crop.Width;
        var top = display.Y / displayedPage.Height * crop.Height; var bottom = display.Bottom / displayedPage.Height * crop.Height; var offset = 0d; var result = new List<Rect>();
        foreach (var band in crop.Bands)
        {
            var from = Math.Max(top, offset); var to = Math.Min(bottom, offset + band.Height);
            if (to > from) result.Add(new Rect(x, band.Top + from - offset, width, to - from)); offset += band.Height;
        }
        return result;
    }
}
