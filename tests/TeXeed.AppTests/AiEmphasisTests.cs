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
            var linkGaps = new List<(double Size, double Gap)>();
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
            void ReportLinkGap(DrawingGroup waves, Run link)
            {
                var glyph = GlyphInk(view, view).Single(i => i.Text == "链接");
                var wave = WaveDrawings(waves, Matrix.Identity).Where(w => ReferenceEquals(w.Drawing.Pen.Brush, link.Foreground) && w.Bounds.Left < glyph.Bounds.Right && w.Bounds.Right > glyph.Bounds.Left)
                    .OrderBy(w => Math.Abs(w.Bounds.Top - glyph.Bounds.Bottom)).First();
                var first = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var gap = wave.Bounds.Top - glyph.Bounds.Bottom - wave.Drawing.Pen.Thickness / 2;
                Console.WriteLine($"Strong link at {view.FontSize}: ink={glyph.Bounds}; baseline={glyph.Baseline:F2}; face={glyph.Face}; weight={glyph.Weight}; line={first}; wave={wave.Bounds}; pen={wave.Drawing.Pen.Thickness:F2}; visible gap={gap:F2}; family={link.FontFamily}; style={link.FontStyle}");
                linkGaps.Add((view.FontSize, gap));
            }
            ReportGaps(drawing);
            Check(WaveDrawings(drawing, Matrix.Identity).Count() >= 4, "Wave is missing from one or more wrapped lines");
            var linkedRun = InlineTree(strong.Single(s => InlineTree(s.Inlines).Any(i => i is Hyperlink)).Inlines).OfType<Run>().Single(r => r.Text == "链接");
            ReportLinkGap(drawing, linkedRun);
            Check(((Hyperlink)linkedRun.Parent).TextDecorations is null or { Count: 0 }, "Strong link retained a straight underline over the wave");
            Check(WaveDrawings(drawing, Matrix.Identity).Any(g => ReferenceEquals(g.Drawing.Pen.Brush, linkedRun.Foreground)), "Linked emphasis wave does not follow the link color");
            var copied = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            Check(copied.Contains("加粗内容") && copied.Contains("链接"), "Wavy formatting changed copyable text");
            Capture(host, Path.Combine(output, "ai-wavy-16.png"));
            view.FontSize = 24; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            await AwaitMathAsync(view); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var scaled = (DrawingGroup)Field(view, "_waves")!;
            ReportGaps(scaled);
            linkedRun = InlineTree(Strong().Single(s => InlineTree(s.Inlines).Any(i => i is Hyperlink)).Inlines).OfType<Run>().Single(r => r.Text == "链接");
            ReportLinkGap(scaled, linkedRun);
            Check(WaveDrawings(scaled, Matrix.Identity).Any(g => Math.Abs(g.Bounds.Height - 3) < .1), "Wave does not scale when font size changes");
            Check(view.Document.LineHeight >= 37.5, "Enlarged text does not reserve space for the next line");
            Capture(host, Path.Combine(output, "ai-wavy-24.png"));
            Check(linkGaps.All(g => g.Gap >= g.Size / 8 - .5), "Linked emphasis wave is too close to its text");
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
            var spacingView = new MarkdownView { ChatMode = true, Padding = new Thickness(0), Markdown =
                "普通第一行  \n普通第二行\n\n" +
                "**波线第一行 gjpqy**  \n正文第二行 gjpqy  \n**波线第三行 gjpqy**  \n正文第四行 gjpqy  \n**[强调链接](https://example.com)**  \n链接下一行\n\n" +
                "1. **列表第一项 gjpqy**\n2. 列表第二项 gjpqy\n3. **列表第三项 gjpqy**\n4. 列表第四项 gjpqy\n\n" +
                "**公式之前 gjpqy**  \n公式下一行 $a+b$\n\n" +
                "**段尾强调 gjpqy**\n\n段后正文 gjpqy\n\n**段尾公式 gjpqy**\n\n$$a^2+b^2=c^2$$\n\n" +
                "## **强调标题 gjpqy**\n\n标题下面正文\n\n普通最后一段\n\n" +
                "1. 普通数字一\n2. 普通数字二\n\n- 普通圆点一\n- 普通圆点二\n\n" +
                "98. 多位数字第一项\n99. 多位数字第二项\n100. 多位数字第三项" };
            host.Content = new ScrollViewer { Content = spacingView }; host.Width = 600; host.Height = 1150;
            foreach (var size in new[] { 16d, 24d })
            {
                spacingView.FontSize = size; host.UpdateLayout();
                await AwaitMathAsync(spacingView); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var ink = GlyphInk(spacingView, spacingView);
                var waves = WaveDrawings((DrawingGroup)Field(spacingView, "_waves")!, Matrix.Identity).ToArray();
                InkSample Ink(string text) => ink.First(i => i.Text.Contains(text));
                double Gap(string upper, string lower)
                {
                    var up = Ink(upper); var down = Ink(lower);
                    var wave = waves.Where(w => w.Bounds.Left < up.Bounds.Right && w.Bounds.Right > up.Bounds.Left && w.Bounds.Top >= up.Bounds.Bottom - 1 && w.Bounds.Bottom < down.Bounds.Top)
                        .OrderBy(w => w.Bounds.Top - up.Bounds.Bottom).First();
                    return down.Bounds.Top - wave.Bounds.Bottom - wave.Drawing.Pen.Thickness / 2;
                }
                var gaps = new[] { Gap("波线第一行", "正文第二行"), Gap("波线第三行", "正文第四行"),
                    Gap("强调链接", "链接下一行"), Gap("列表第一项", "列表第二项"), Gap("列表第三项", "列表第四项"), Gap("公式之前", "公式下一行"),
                    Gap("段尾强调", "段后正文"), Gap("强调标题", "标题下面正文") };
                var beforeDisplay = Ink("段尾公式");
                var displayWave = waves.Where(w => w.Bounds.Left < beforeDisplay.Bounds.Right && w.Bounds.Right > beforeDisplay.Bounds.Left && w.Bounds.Top >= beforeDisplay.Bounds.Bottom - 1)
                    .OrderBy(w => w.Bounds.Top - beforeDisplay.Bounds.Bottom).First();
                var display = Descendants<ChatFormula>(spacingView).Single(f => f.Source == "a^2+b^2=c^2");
                var formulaTop = display.TranslatePoint(new Point(), spacingView).Y;
                var formulaGap = formulaTop - displayWave.Bounds.Bottom - displayWave.Drawing.Pen.Thickness / 2;
                var plainLine = Ink("普通第二行").Baseline - Ink("普通第一行").Baseline;
                var headingParagraph = Ink("标题下面正文").Baseline - Ink("强调标题").Baseline;
                var plainParagraph = Ink("普通最后一段").Baseline - Ink("标题下面正文").Baseline;
                var orderedGap = Ink("普通数字二").Baseline - Ink("普通数字一").Baseline;
                var unorderedGap = Ink("普通圆点二").Baseline - Ink("普通圆点一").Baseline;
                Console.WriteLine($"Wave to next ink at {size}: " + string.Join(", ", gaps.Select(g => g.ToString("F2"))) + $"; display formula {formulaGap:F2}; plain line {plainLine:F2}; heading/body {headingParagraph:F2}; body/body {plainParagraph:F2}");
                Console.WriteLine("List marker glyphs: " + string.Join(", ", ink.Where(i => i.Text.Contains("1.") || i.Text.Contains("2.") || i.Text.Contains("•")).Select(i => $"{i.Text}:{i.Bounds.Left:F2}")));
                Check(gaps.All(g => g >= size / 8), "Wave is too close to the next line of ink, including separate list items");
                Check(formulaGap >= size / 8, "Wave is too close to the following display formula");
                Check(Math.Abs(plainLine - Math.Max(25, size * 25 / 16)) < .5, "Unemphasized line spacing changed");
                Check(Math.Abs(orderedGap - plainLine) < .5 && Math.Abs(unorderedGap - plainLine) < .5, "Unemphasized list item spacing changed");
                var numberMarkers = ink.Where(i => i.Text is "1." or "2.").ToArray();
                Check(numberMarkers.Length >= 4 && numberMarkers.All(i => i.Bounds.Left >= 0), "Ordered list number is clipped at the left edge");
                var longMarkers = ink.Where(i => i.Text is "98." or "99." or "100.").ToArray();
                Check(longMarkers.Length == 3 && longMarkers.All(i => i.Bounds.Left >= 0), "Multi-digit ordered list number is clipped at the left edge");
                Check(Math.Abs(headingParagraph - plainParagraph) < 1, "Emphasized heading changed the paragraph gap");
                Capture(host, Path.Combine(output, $"ai-wavy-nextline-{size:0}.png"));
            }
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
