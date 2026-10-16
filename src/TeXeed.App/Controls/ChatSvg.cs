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
    internal static ChatSvg Read(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("SVG 为空。");
        var definitions = root.Descendants().Where(e => e.Name.LocalName == "path" && e.Attribute("id") is not null)
            .ToDictionary(e => (string)e.Attribute("id")!, e => e);
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
                    geometry = ReadElement(definition, depth + 1);
                    var useTransform = new TransformGroup();
                    useTransform.Children.Add(geometry.Transform);
                    useTransform.Children.Add(new TranslateTransform(Number(element, "x"), Number(element, "y")));
                    geometry.Transform = useTransform; break;
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
        return new(result, new Rect(left, top, width, height), Number(root, "data-texeed-height") * ptToBp - top, path);
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
