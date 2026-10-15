using System.Windows;

namespace TeXeed.App.Services;

internal sealed record PdfPrintPlacement(int Page, Rect Source, Rect Target, Rect Clip, bool Border = false);
internal sealed record PdfPrintFace(PdfPrintLayout Layout, IReadOnlyList<PdfPrintPlacement> Items, bool CutMarks = false, bool Rotate180 = false);

internal static class PdfPrintPlan
{
    internal static PdfPrintFace[] Copies(PdfPrintFace[] faces, int copies, bool collate, bool duplex)
    {
        if (faces.Length == 0 || copies < 1 || (long)faces.Length * copies > 100000) throw new ArgumentException("输出面数或份数无效。");
        var padded = duplex && faces.Length % 2 == 1 ? [.. faces, new PdfPrintFace(faces[^1].Layout, [])] : faces;
        return collate ? Enumerable.Range(0, copies).SelectMany(_ => padded).ToArray() : duplex
            ? padded.Chunk(2).SelectMany(pair => Enumerable.Range(0, copies).SelectMany(_ => pair)).ToArray()
            : padded.SelectMany(f => Enumerable.Repeat(f, copies)).ToArray();
    }

    internal static PdfPrintFace[] Build(IReadOnlyList<Size> sizes, int[] pages, PdfPrintOptions options,
        Func<Size, PdfPrintLayout> layoutFor)
    {
        if (pages.Length == 0) return [];
        if (options.Scale is < 1 or > 1000 || !double.IsFinite(options.Scale) || options.PosterScale is < 10 or > 1000 ||
            !double.IsFinite(options.PosterScale) || options.OverlapMm is < 0 or > 30 || !double.IsFinite(options.OverlapMm))
            throw new ArgumentException("倍率应为 1–1000%，重叠边应为 0–30 mm。");
        if (options.NUp is not (2 or 4 or 6 or 9 or 16)) throw new ArgumentException("不支持的每张页数。");
        var result = new List<PdfPrintFace>();
        Rect Source(int p) => options.ViewRegion is { } view ? Rect.Intersect(view, new Rect(sizes[p])) : new Rect(sizes[p]);
        PdfPrintLayout Layout(int p)
        {
            var layout = layoutFor(sizes[p]);
            if (options.Margins is { } m)
            {
                var requested = new Rect(m.Left, m.Top, Math.Max(0, layout.Paper.Width - m.Left - m.Right), Math.Max(0, layout.Paper.Height - m.Top - m.Bottom));
                requested.Intersect(layout.Printable);
                if (requested.IsEmpty || requested.Width < 1 || requested.Height < 1) throw new ArgumentException("页边距超出可打印区域。");
                layout = layout with { Printable = requested };
            }
            return layout;
        }
        PdfPrintPlacement Place(int p, Rect cell, bool fit = false)
        {
            var src = Source(p);
            if (src.IsEmpty || src.Width < .01 || src.Height < .01) throw new ArgumentException("当前视图区域为空。");
            var factor = Math.Min(cell.Width / (src.Width * 96 / 72), cell.Height / (src.Height * 96 / 72));
            if (!fit) factor = options.ScaleMode switch { PdfPrintScale.Actual => 1, PdfPrintScale.Shrink => Math.Min(1, factor), PdfPrintScale.Custom => options.Scale / 100, _ => factor };
            var w = src.Width * 96 / 72 * factor; var h = src.Height * 96 / 72 * factor;
            return new(p, src, new Rect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h), cell, fit && options.PageBorder);
        }
        if (options.Mode == PdfPrintMode.Booklet)
        {
            var length = (pages.Length + 3) / 4 * 4; var layout = Layout(pages[0]); var area = layout.Printable;
            for (var sheet = 0; sheet < length / 4; ++sheet)
            {
                for (var side = 0; side < 2; ++side)
                {
                    if (options.BookletSide == 1 && side == 1 || options.BookletSide == 2 && side == 0) continue;
                    int[] pair = side == 0 ? [length - 1 - sheet * 2, sheet * 2] : [sheet * 2 + 1, length - 2 - sheet * 2];
                    if (options.RightBinding) Array.Reverse(pair);
                    var items = new List<PdfPrintPlacement>();
                    for (var c = 0; c < 2; ++c)
                        if (pair[c] < pages.Length) items.Add(Place(pages[pair[c]], new Rect(area.X + c * area.Width / 2, area.Y, area.Width / 2, area.Height), true) with { Border = false });
                    result.Add(new(layout, items));
                }
            }
        }
        else if (options.Mode == PdfPrintMode.Multiple)
        {
            var cols = options.NUp switch { 2 => 2, 4 => 2, 6 => 3, 9 => 3, _ => 4 }; var rows = options.NUp / cols;
            for (var offset = 0; offset < pages.Length; offset += options.NUp)
            {
                var layout = Layout(pages[offset]); var area = layout.Printable; var items = new List<PdfPrintPlacement>();
                for (var i = 0; i < options.NUp && offset + i < pages.Length; ++i)
                {
                    var col = options.ColumnOrder ? i / rows : i % cols; var row = options.ColumnOrder ? i % rows : i / cols;
                    items.Add(Place(pages[offset + i], new Rect(area.X + col * area.Width / cols, area.Y + row * area.Height / rows, area.Width / cols, area.Height / rows), true));
                }
                result.Add(new(layout, items));
            }
        }
        else foreach (var p in pages)
        {
            var layout = Layout(p); var area = layout.Printable;
            if (options.Mode != PdfPrintMode.Poster) { result.Add(new(layout, [Place(p, area)])); continue; }
            var src = Source(p); var scale = 96d / 72 * options.PosterScale / 100;
            var overlap = options.OverlapMm * 96 / 25.4;
            if (overlap >= Math.Min(area.Width, area.Height)) throw new ArgumentException("重叠边必须小于可打印区域。");
            var cols = Math.Max(1, (int)Math.Ceiling((src.Width * scale - overlap) / (area.Width - overlap)));
            var rows = Math.Max(1, (int)Math.Ceiling((src.Height * scale - overlap) / (area.Height - overlap)));
            if ((long)cols * rows + result.Count > 10000) throw new ArgumentException("海报拼接超过 10,000 面，请降低倍率。");
            for (var y = 0; y < rows; ++y) for (var x = 0; x < cols; ++x)
                result.Add(new(layout, [new(p, src, new Rect(area.X - x * (area.Width - overlap), area.Y - y * (area.Height - overlap), src.Width * scale, src.Height * scale), area)], options.CutMarks));
        }
        return result.ToArray();
    }
}
