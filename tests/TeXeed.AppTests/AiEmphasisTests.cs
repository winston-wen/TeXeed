using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckAiEmphasisAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        IEnumerable<Inline> InlineTree(InlineCollection items) => items.SelectMany(i => i is Span span ? new[] { i }.Concat(InlineTree(span.Inlines)) : [i]);
        IEnumerable<(GeometryDrawing Drawing, Rect Bounds)> WaveDrawings(DrawingGroup group, Matrix parent)
        {
            var transform = group.Transform?.Value ?? Matrix.Identity; transform.Append(parent);
            foreach (var child in group.Children)
                if (child is DrawingGroup nested)
                {
                    foreach (var item in WaveDrawings(nested, transform)) yield return item;
                }
                else if (child is GeometryDrawing drawing)
                    foreach (var figure in PathGeometry.CreateFromGeometry(drawing.Geometry).Figures)
                        yield return (drawing, new MatrixTransform(transform).TransformBounds(new PathGeometry([figure]).Bounds));
        }
        var view = new MarkdownView { ChatMode = true, Padding = new Thickness(0), Markdown = "普通文字 **加粗内容和多个字符，长段落换行时仍须连续显示波浪下划线并保持可选择。** 再有 ***粗斜体 gjpqy*** 和 **[链接](https://example.com)**。\n\n行内公式 $x_i + y_j$ 前后 **gjpqy 的底部** 应当留白，**公式 $a+b$ 也强调**。" };
        var host = new Window { Owner = owner, Width = 350, Height = 350, Content = view, Title = "波浪强调验证" };
        host.Show();
        try
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            await AwaitMathAsync(view); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Span[] Strong() => view.Document.Blocks.OfType<Paragraph>().SelectMany(p => InlineTree(p.Inlines)).OfType<Span>()
                .Where(s => ReferenceEquals(s.Tag, MarkdownView.StrongEmphasisMarker)).ToArray();
            var strong = Strong();
            Check(strong.Length == 5, "Strong Markdown semantic markers were lost");
            Check(strong.All(s => s.FontWeight == FontWeights.Normal), "Strong text should have normal weight");
            Check(strong.Any(s => InlineTree(s.Inlines).Any(i => i.FontStyle == FontStyles.Italic)), "Strong italic lost its italic style");
            Check(strong.Any(s => InlineTree(s.Inlines).Any(i => i is Hyperlink)), "Strong link lost link semantics");
            var drawing = (DrawingGroup)Field(view, "_waves")!;
            void ReportGaps(DrawingGroup waves)
            {
                var ink = GlyphInk(view, view);
                foreach (var wave in WaveDrawings(waves, Matrix.Identity))
                {
                    var bounds = wave.Bounds;
                    var above = ink.Where(i => i.Bounds.Right > bounds.Left && i.Bounds.Left < bounds.Right && i.Bounds.Top < bounds.Top && i.Bounds.Bottom <= bounds.Top + 1)
                        .OrderByDescending(i => i.Bounds.Bottom).FirstOrDefault();
                    if (above is not null) Console.WriteLine($"Wave ink gap at {view.FontSize}: {bounds.Top - above.Bounds.Bottom:F2} DIP, text={above.Text}");
                }
            }
            ReportGaps(drawing);
            Check(WaveDrawings(drawing, Matrix.Identity).Count() >= 4, "Wave is missing from one or more wrapped lines");
            var linkedRun = InlineTree(strong.Single(s => InlineTree(s.Inlines).Any(i => i is Hyperlink)).Inlines).OfType<Run>().Single(r => r.Text == "链接");
            Check(((Hyperlink)linkedRun.Parent).TextDecorations is null or { Count: 0 }, "Strong link retained a straight underline over the wave");
            Check(WaveDrawings(drawing, Matrix.Identity).Any(g => ReferenceEquals(g.Drawing.Pen.Brush, linkedRun.Foreground)), "Linked emphasis wave does not follow the link color");
            var copied = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            Check(copied.Contains("加粗内容") && copied.Contains("链接"), "Wavy formatting changed copyable text");
            Capture(host, Path.Combine(output, "ai-wavy-16.png"));
            view.FontSize = 24; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            await AwaitMathAsync(view); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var scaled = (DrawingGroup)Field(view, "_waves")!;
            ReportGaps(scaled);
            Check(WaveDrawings(scaled, Matrix.Identity).Any(g => Math.Abs(g.Bounds.Height - 3) < .1), "Wave does not scale when font size changes");
            Check(view.Document.LineHeight >= 37.5, "Enlarged text does not reserve space for the next line");
            Capture(host, Path.Combine(output, "ai-wavy-24.png"));
            linkedRun = InlineTree(Strong().Single(s => InlineTree(s.Inlines).Any(i => i is Hyperlink)).Inlines).OfType<Run>().Single(r => r.Text == "链接");
            ThemeManager.Apply("github-light-default"); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var light = (DrawingGroup)Field(view, "_waves")!;
            Check(WaveDrawings(light, Matrix.Identity).Any(g => ReferenceEquals(g.Drawing.Pen.Brush, linkedRun.Foreground)), "Wave did not follow the link color after changing theme");
            Capture(host, Path.Combine(output, "ai-wavy-light.png"));
            ThemeManager.Apply("ristretto");
            var question = new MarkdownView { UseWavyEmphasis = true, Markdown = "用户的 **强调内容**" };
            var questionSpan = ((Paragraph)question.Document.Blocks.FirstBlock!).Inlines.OfType<Span>().Single();
            Check(ReferenceEquals(questionSpan.Tag, MarkdownView.StrongEmphasisMarker), "Question emphasis does not use a wave");
            var generic = new MarkdownView { Markdown = "其他页面的 **强调内容**" };
            var genericSpan = ((Paragraph)generic.Document.Blocks.FirstBlock!).Inlines.OfType<Span>().Single();
            Check(genericSpan.FontWeight == FontWeights.Bold, "AI-specific emphasis leaked into other views");
            var foldView = new MarkdownView { ChatMode = true, Markdown = "# 第一节\n\n**被折叠的重点**\n\n# 第二节\n\n**保持显示的重点**" };
            foldView.ConfigureFolding(new Dictionary<string, bool>(), "wave-test", () => { });
            host.Content = foldView; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            int WaveCount() => WaveDrawings((DrawingGroup)Field(foldView, "_waves")!, Matrix.Identity).Count();
            var expandedWaves = WaveCount();
            var section = (Section)foldView.Document.Blocks.FirstBlock!;
            var heading = (Paragraph)section.Blocks.FirstBlock!;
            var toggle = heading.Inlines.OfType<Hyperlink>().Single();
            Check(expandedWaves >= 2, "Expanded sections did not render both emphasis waves");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(WaveCount() == expandedWaves - 1, "Folding a section left its emphasis wave visible");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(WaveCount() == expandedWaves, "Expanding a section did not restore its emphasis wave");
            Console.WriteLine($"AI wavy emphasis: {checks} checks passed (semantics, italic, link, wrap, copy and scale).");
        }
        finally { host.Close(); }
    }
}
