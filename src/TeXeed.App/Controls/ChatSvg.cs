using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace TeXeed.App.Controls;

/// <summary>Read path-only dvisvgm SVG, with no scripts, fonts, images or external resources.</summary>
internal sealed record ChatSvg(Geometry Geometry, Rect Bounds, double Baseline, string Path)
{
    internal sealed record Slice(Geometry Geometry, double Left, double Width);
    internal IReadOnlyList<Slice> InlineSlices { get; private init; } = [];
    internal bool HasBreakMetrics { get; private init; }
    internal string? CjkFont { get; private init; }
    internal static ChatSvg Read(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("SVG 为空。");
        return Read(root, path);
    }
    internal static ChatSvg Read(XElement root, string path)
    {
        // dvisvgm can define another point size of a glyph as a scaled local <use>.
        var definitions = root.Descendants().Where(e => (e.Name.LocalName is "path" or "use") && e.Attribute("id") is not null)
            .ToDictionary(e => (string)e.Attribute("id")!, e => e);
        var glyphs = new Dictionary<string, Geometry>();
        var resolving = new HashSet<string>();
        var count = 0;
        Geometry ReadElement(XElement element, int depth)
        {
            if (depth > 32 || ++count > 20000) throw new InvalidDataException("SVG 过于复杂。");
            Geometry geometry;
            switch (element.Name.LocalName)
            {
                case "svg": case "g":
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var child in element.Elements().Where(e => e.Name.LocalName is not "defs" and not "title" and not "desc")) group.Children.Add(ReadElement(child, depth + 1));
                    geometry = group; break;
                case "path": geometry = Geometry.Parse("F1 " + (string?)element.Attribute("d")).Clone(); break;
                case "rect": geometry = new RectangleGeometry(new Rect(Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height"))); break;
                case "use":
                    var link = (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")) ?? (string?)element.Attribute("href");
                    if (link is null || !link.StartsWith('#') || !definitions.TryGetValue(link[1..], out var definition)) throw new InvalidDataException("SVG 字形引用无效。");
                    if (!glyphs.TryGetValue(link, out var glyph))
                    {
                        if (!resolving.Add(link)) throw new InvalidDataException("公式 SVG 字形引用循环。");
                        glyph = ReadElement(definition, depth + 1);
                        resolving.Remove(link);
                        glyph.Freeze(); glyphs.Add(link, glyph);
                    }
                    var instance = new GeometryGroup { FillRule = FillRule.Nonzero };
                    instance.Children.Add(glyph);
                    instance.Transform = new TranslateTransform(Number(element, "x"), Number(element, "y"));
                    geometry = instance; break;
                default: throw new InvalidDataException("不支持的公式 SVG 元素：" + element.Name.LocalName);
            }
            if (element.Attribute("transform") is { } transform)
            {
                var transforms = new TransformGroup(); transforms.Children.Add(geometry.Transform);
                transforms.Children.Add(ParseTransform(transform.Value)); geometry.Transform = transforms;
            }
            return geometry;
        }
        var result = ReadElement(root, 0);
        var view = Numbers((string?)root.Attribute("viewBox") ?? "");
        if (view.Length != 4 || view[2] <= 0 || view[3] <= 0) throw new InvalidDataException("SVG 尺寸无效。");
        // The shipout hbox starts at (0,0); its baseline is the TeX height in bp.
        const double ptToBp = 72d / 72.27;
        var left = Math.Min(0, view[0]); var top = Math.Min(0, view[1]);
        var width = Math.Max(Number(root, "data-texeed-width") * ptToBp, view[0] + view[2]) - left;
        var height = Math.Max((Number(root, "data-texeed-height") + Number(root, "data-texeed-depth")) * ptToBp, view[1] + view[3]) - top;
        if (!double.IsFinite(width + height) || width > 50000 || height > 10000) throw new InvalidDataException("公式尺寸超出范围。");
        result.Freeze();
        var bounds = new Rect(left, top, width, height);
        var breaks = (string?)root.Attribute("data-texeed-breaks");
        return new(result, bounds, Number(root, "data-texeed-height") * ptToBp - top, path)
        {
            HasBreakMetrics = breaks is not null,
            CjkFont = (string?)root.Attribute("data-texeed-cjk-font"),
            InlineSlices = width >= ChatInlineBreaks.MinimumWidthBp && !string.IsNullOrWhiteSpace(breaks)
                ? SplitAtTeXPositions(result, bounds, Numbers(breaks)) : []
        };
    }

    private static IReadOnlyList<Slice> SplitAtTeXPositions(Geometry source, Rect bounds, double[] positions)
    {
        // dvisvgm groups translated glyph outlines under the page. Reuse these frozen
        // geometries, so rendering one line does not repaint the rest of a long equation.
        var glyphs = new List<Geometry>();
        void Collect(Geometry node)
        {
            if (node is GeometryGroup group && group.Transform.Value.IsIdentity)
                foreach (var child in group.Children) Collect(child);
            else if (!node.Bounds.IsEmpty) glyphs.Add(node);
        }
        Collect(source);
        var byX = glyphs.Select(g => g.Bounds).OrderBy(b => b.Left).ToArray();
        var edges = new List<double> { bounds.Left };
        var right = bounds.Left;
        var index = 0;
        foreach (var x in positions)
        {
            if (!double.IsFinite(x) || x <= edges[^1] || x >= bounds.Right) continue;
            while (index < byX.Length && byX[index].Left < x)
                right = Math.Max(right, byX[index++].Right);
            // Italic glyphs can protrude past the TeX advance. Move the boundary into
            // the adjacent gap, or keep this group whole when there is no safe gap.
            var boundary = Math.Max(x, right);
            if (boundary < bounds.Right && (index == byX.Length || boundary <= byX[index].Left))
                edges.Add(boundary);
        }
        if (edges.Count == 1) return [];
        edges.Add(bounds.Right);
        var pieces = Enumerable.Range(1, edges.Count - 1).Select(_ => new GeometryGroup { FillRule = FillRule.Nonzero }).ToArray();
        foreach (var glyph in glyphs)
        {
            var piece = edges.BinarySearch(glyph.Bounds.Left);
            if (piece < 0) piece = ~piece - 1;
            pieces[Math.Clamp(piece, 0, pieces.Length - 1)].Children.Add(glyph);
        }
        var slices = new Slice[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            pieces[i].Freeze();
            slices[i] = new(pieces[i], edges[i], edges[i + 1] - edges[i]);
        }
        return slices;
    }
    private static double Number(XElement e, string name) => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
    private static double[] Numbers(string value) => Regex.Split(value.Trim(), @"[\s,]+").Where(s => s.Length > 0).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    private static Transform ParseTransform(string source)
    {
        var result = new TransformGroup(); var end = 0;
        foreach (Match match in Regex.Matches(source, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)"))
        {
            if (!string.IsNullOrWhiteSpace(source[end..match.Index])) throw new InvalidDataException("SVG 变换无效。");
            var n = Numbers(match.Groups[2].Value);
            Transform transform = match.Groups[1].Value switch
            {
                "matrix" when n.Length == 6 => new MatrixTransform(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" when n.Length is 1 or 2 => new TranslateTransform(n[0], n.Length == 2 ? n[1] : 0),
                "scale" when n.Length is 1 or 2 => new ScaleTransform(n[0], n.Length == 2 ? n[1] : n[0]),
                "rotate" when n.Length == 1 => new RotateTransform(n[0]),
                "rotate" when n.Length == 3 => new RotateTransform(n[0], n[1], n[2]),
                _ => throw new InvalidDataException("SVG 变换无效。")
            };
            // SVG lists transformations from outermost to innermost.
            result.Children.Insert(0, transform); end = match.Index + match.Length;
        }
        if (!string.IsNullOrWhiteSpace(source[end..])) throw new InvalidDataException("SVG 变换无效。");
        return result;
    }
}
