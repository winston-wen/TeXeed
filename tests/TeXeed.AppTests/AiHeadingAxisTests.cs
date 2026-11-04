using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static (double Marker, double Title) HeadingInkCentroids(MarkdownView view, Rect marker, Rect title, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        double Center(Rect box)
        {
            var left = Math.Max(0, (int)Math.Floor((box.Left - .5) * scale));
            var right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling((box.Right + .5) * scale));
            var top = Math.Max(0, (int)Math.Floor((box.Top - .5) * scale));
            var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling((box.Bottom + .5) * scale));
            double weight = 0, yWeight = 0;
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                {
                    var alpha = pixels[y * stride + x * 4 + 3];
                    weight += alpha; yWeight += alpha * (y + .5) / scale;
                }
            return weight > 0 ? yWeight / weight : double.NaN;
        }
        return (Center(marker), Center(title));
    }

    private static async Task CheckAiHeadingAxisAsync(MainWindow owner, string output, bool enforce)
    {
        var view = new MarkdownView { ChatMode = true, TurnNumber = 7, Padding = new Thickness(0) };
        view.ConfigureFolding(new(), "axis", () => { });
        var host = new Window { Owner = owner, Content = new ScrollViewer { Content = view }, Width = 500, Height = 260, Title = "标题标记中轴" };
        var results = new List<object>();
        var deltas = new List<double>();
        host.Show();
        try
        {
            view.TurnNumber = 1;
            view.Markdown = "## 8. 最后一层：从近似共享值变成“二选一”\n\n正文测试。";
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Capture(host, Path.Combine(output, "heading-marker-user-title.png"));
            var initialGlyphs = GlyphInk(view, view);
            File.WriteAllText(Path.Combine(output, "heading-marker-user-ink.json"), JsonSerializer.Serialize(initialGlyphs));
            var initialMarker = initialGlyphs.Single(g => g.Text.Contains("[1:h2]"));
            var initialTitle = initialGlyphs.Where(g => g.Bounds.Top < initialMarker.Bounds.Bottom && !g.Text.Contains("[1:h2]")).Select(g => g.Bounds).Aggregate(Rect.Empty, (a, b) => { a.Union(b); return a; });
            foreach (var scale in new[] { 1d, 2d, 4d })
            {
                var centers = HeadingInkCentroids(view, initialMarker.Bounds, initialTitle, scale);
                Console.WriteLine($"Visible ink centroid {scale}x: marker={centers.Marker:F3}, title={centers.Title:F3}, difference={centers.Marker - centers.Title:F3} DIP");
                if (enforce && (double.IsNaN(centers.Marker) || double.IsNaN(centers.Title) || Math.Abs(centers.Marker - centers.Title) > .5))
                    throw new Exception($"Heading marker optical center differs at {scale}x: {centers.Marker - centers.Title:F3} DIP");
            }
            view.FontSize = 24;
            view.Markdown = "";
            view.Markdown = "## 8. 最后一层：从近似共享值变成“二选一”\n\n正文测试。";
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Capture(host, Path.Combine(output, "heading-marker-user-title-large.png"));
            var largeGlyphs = GlyphInk(view, view);
            var largeMarker = largeGlyphs.Single(g => g.Text.Contains("[1:h2]"));
            var largeTitle = largeGlyphs.Where(g => g.Bounds.Top < largeMarker.Bounds.Bottom && !g.Text.Contains("[1:h2]")).Select(g => g.Bounds).Aggregate(Rect.Empty, (a, b) => { a.Union(b); return a; });
            foreach (var scale in new[] { 1d, 2d, 4d })
            {
                var centers = HeadingInkCentroids(view, largeMarker.Bounds, largeTitle, scale);
                Console.WriteLine($"Visible ink centroid 24 DIP {scale}x: difference={centers.Marker - centers.Title:F3} DIP");
                if (enforce && (double.IsNaN(centers.Marker) || double.IsNaN(centers.Title) || Math.Abs(centers.Marker - centers.Title) > .75))
                    throw new Exception($"Large heading marker optical center differs at {scale}x: {centers.Marker - centers.Title:F3} DIP");
            }
            if (Environment.GetEnvironmentVariable("TEXEED_HEADING_QUICK") == "1") return;
            var cases = new[] {
                ("8. 最后一层：从近似共享值变成“二选一”", "最后一层"),
                ("8. 最后一层：密码与工程", "最后一层"),
                ("中文标题 123 / Alpha", "中文标题"),
                ("123. Heading alpha gjpqy", "Heading"),
                ("Heading Latin words " + string.Concat(Enumerable.Repeat("long title ", 8)) + "最后才有中文", "Heading"),
                ("8. 最后一层：附带 $x_i^2$ 公式", "最后一层"),
                ("8. 最后一层：" + string.Concat(Enumerable.Repeat("较长标题自动换行", 5)), "最后一层")
            };
            foreach (var size in new[] { 16d, 24d })
            foreach (var turn in new[] { 1, 7, 11, 108 })
            foreach (var level in Enumerable.Range(1, 6))
            foreach (var (text, reference) in cases)
            foreach (var emphasized in new[] { false, true })
            foreach (var folded in new[] { false, true })
            {
                view.TurnNumber = 0;
                view.FontSize = size;
                view.Markdown = "";
                view.ConfigureFolding(new(), "axis", () => { });
                view.Markdown = new string('#', level) + " " + (emphasized ? "**" + text + "**" : text) + "\n\n正文测试。";
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                view.TurnNumber = turn;
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var section = (Section)view.Document.Blocks.FirstBlock!;
                var heading = (Paragraph)section.Blocks.FirstBlock!;
                if (folded)
                {
                    ((Hyperlink)heading.Inlines.FirstInline!).RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                    host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                }
                var glyphs = GlyphInk(view, view);
                var marker = glyphs.Where(g => g.Text.Contains($"[{turn}:h{level}]")).ToArray();
                var title = glyphs.Where(g => g.Text.Contains(reference)).ToArray();
                if (marker.Length != 1 || title.Length != 1)
                    throw new Exception($"Cannot identify marker/title glyphs: {size}, h{level}, {text}: {string.Join(" | ", glyphs.Select(g => g.Text))}");
                var markerCenter = marker[0].Bounds.Top + marker[0].Bounds.Height / 2;
                var titleCenter = title[0].Bounds.Top + title[0].Bounds.Height / 2;
                var delta = markerCenter - titleCenter;
                results.Add(new { size, turn, level, text, emphasized, folded, markerCenter, titleCenter, delta });
                deltas.Add(delta);
                if (enforce && Math.Abs(delta) > size * .06)
                    throw new Exception($"Heading marker ink axis mismatch: {size}, h{level}, emphasized={emphasized}, folded={folded}, {text}: {delta:F3} DIP");
            }
            File.WriteAllText(Path.Combine(output, "heading-marker-axis.json"), JsonSerializer.Serialize(results));
            Console.WriteLine($"Heading marker center errors: count={deltas.Count}, min={deltas.Min():F3}, max={deltas.Max():F3} DIP");
            Capture(host, Path.Combine(output, "heading-marker-axis.png"));
        }
        finally { host.Close(); }
    }
}
