using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace TeXeed.App.Controls;

/// <summary>Natural-height vector mathematics; unlike the outline it never caps fractions at one text line.</summary>
internal sealed class ChatFormula : FrameworkElement
{
    private static readonly Dictionary<(string, bool), Geometry?> Cache = [];
    private readonly Geometry _geometry;
    internal string Source { get; }
    static ChatFormula() => TextElement.ForegroundProperty.OverrideMetadata(typeof(ChatFormula),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    internal ChatFormula(string source, bool display)
    {
        Source = source;
        if (!Cache.TryGetValue((source, display), out var geometry))
        {
            try
            {
                if (source.Length > 8192 || source.Count(c => c == '{') > 256) throw new ArgumentException("公式过长");
                var normalized = System.Text.RegularExpressions.Regex.Replace(source, @"\\(operatorname|longrightarrow|longleftarrow|longleftrightarrow|mapsto|to|dfrac|tfrac|qquad|quad)\b", m => m.Groups[1].Value switch
                {
                    "qquad" => @"\text{  }", "quad" => @"\text{ }", "operatorname" => @"\mathrm", "longrightarrow" => @"\text{⟶}", "longleftarrow" => @"\text{⟵}",
                    "longleftrightarrow" => @"\text{⟷}", "mapsto" => @"\text{↦}", "to" => @"\rightarrow", _ => @"\frac"
                });
                geometry = RenderFormula(normalized, display);
                geometry.Freeze();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex.Message); geometry = null; }
            if (Cache.Count >= 512) Cache.Clear();
            Cache[(source, display)] = geometry;
        }
        _geometry = geometry ?? throw new ArgumentException("此公式暂不支持，保留 LaTeX 源文。");
        ToolTip = source;
        System.Windows.Automation.AutomationProperties.SetName(this, source);
    }
    private static Geometry RenderFormula(string source, bool display)
    {
        const string begin = @"\begin{aligned}", end = @"\end{aligned}";
        if (source.StartsWith(begin) && source.EndsWith(end))
        {
            var rows = SplitMath(source[begin.Length..^end.Length], @"\\").Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(row => SplitMath(row, "&").Select(cell => RenderFormula(cell.Trim(), display)).ToArray()).ToArray();
            if (rows.Length == 0 || rows.Length > 64 || rows.Any(r => r.Length > 8)) throw new ArgumentException("对齐公式过长");
            var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(i => rows.Max(r => i < r.Length ? r[i].Bounds.Width : 0)).ToArray();
            var result = new GeometryGroup(); double y = 0;
            foreach (var row in rows)
            {
                double x = 0; var height = row.Max(g => g.Bounds.Height);
                for (var i = 0; i < row.Length; i++)
                {
                    var cell = row[i].Clone(); var bounds = cell.Bounds;
                    cell.Transform = new TranslateTransform(x - bounds.X + (i % 2 == 0 ? widths[i] - bounds.Width : 0), y - bounds.Y + (height - bounds.Height) / 2);
                    result.Children.Add(cell); x += widths[i] + (i % 2 == 0 ? 4 : 20);
                }
                y += height + 10;
            }
            return result;
        }
        var formula = WpfTeXFormulaParser.Instance.Parse(source.Length == 0 ? @"\text{ }" : source);
        return formula.RenderToGeometry(WpfTeXEnvironment.Create(display ? TexStyle.Display : TexStyle.Text, display ? 20 : 17, "Cambria Math"), scale: display ? 20 : 17);
    }
    // Split only at the current TeX level, preserving nested fractions and matrix environments.
    private static IEnumerable<string> SplitMath(string source, string separator)
    {
        int start = 0, braces = 0, environments = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source.AsSpan(i).StartsWith(@"\begin{") || source.AsSpan(i).StartsWith(@"\end{"))
            {
                environments += source.AsSpan(i).StartsWith(@"\begin{") ? 1 : -1;
                var close = source.IndexOf('}', i); if (close < 0) break; i = close; continue;
            }
            if (braces == 0 && environments == 0 && source.AsSpan(i).StartsWith(separator))
            { yield return source[start..i]; i += separator.Length - 1; start = i + 1; continue; }
            if (source[i] == '\\') { i++; continue; }
            if (source[i] == '{') braces++; else if (source[i] == '}') braces--;
        }
        yield return source[start..];
    }
    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, _geometry.Bounds.Width) + 4, Math.Max(1, _geometry.Bounds.Height) + 6);
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushTransform(new TranslateTransform(2 - _geometry.Bounds.X, 3 - _geometry.Bounds.Y));
        drawing.DrawGeometry((Brush)GetValue(TextElement.ForegroundProperty), null, _geometry);
        drawing.Pop();
    }
}
