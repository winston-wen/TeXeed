using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TeXeed.App.Services;

// Render lazily: a 625-page book must not retain 625 full-resolution rasters.
// The same placement function drives the preview and the print spooler.
internal sealed class PdfPrintPaginator(PdfDocument document, PdfPrintLayout layout, IReadOnlyList<int> pages) : DocumentPaginator
{
    private PdfPrintFace[]? _faces;
    private PdfPrintOptions _options = new();
    internal PdfPrintPaginator(PdfDocument document, PdfPrintFace[] faces, PdfPrintOptions options)
        : this(document, faces[0].Layout, []) { _faces = faces; _options = options; }
    internal PdfPrintLayout Layout { get; private set; } = layout;
    public override bool IsPageCountValid => true;
    public override int PageCount => _faces?.Length ?? pages.Count;
    public override IDocumentPaginatorSource Source => null!;
    public override Size PageSize
    {
        get => Layout.Paper;
        set
        {
            if (!double.IsFinite(value.Width) || !double.IsFinite(value.Height) || value.Width <= 0 || value.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (value == Layout.Paper) return;
            var bounds = Layout.Printable; bounds.Intersect(new Rect(value));
            if (bounds.IsEmpty) throw new ArgumentException("纸张没有可打印区域。");
            Layout = Layout with { Paper = value, Printable = bounds };
        }
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= PageCount) return DocumentPage.Missing;
        if (_faces is { } faces)
        {
            var face = faces[pageNumber]; var result = new DrawingVisual();
            using (var dc = result.RenderOpen()) dc.DrawDrawing(PdfPrintDrawing.Render(document, face, _options, _options.Dpi));
            return new DocumentPage(result, face.Layout.Paper, new Rect(face.Layout.Paper), face.Layout.Printable);
        }
        var index = pages[pageNumber];
        var target = Layout.Place(document.Pages[index]);
        // 300 dpi at the final physical output size, bounded to 32 MP per page.
        var image = document.RenderForPrint(index, target.Width * 300 / 96 / document.Pages[index].Width);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(Layout.Paper));
            drawing.PushClip(new RectangleGeometry(Layout.Printable));
            drawing.DrawImage(image, target);
            drawing.Pop();
        }
        return new DocumentPage(visual, Layout.Paper, new Rect(Layout.Paper), Layout.Printable);
    }
}
