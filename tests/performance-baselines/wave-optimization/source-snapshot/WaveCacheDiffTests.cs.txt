using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Markup;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckWaveCacheDiffAsync(MainWindow owner)
    {
        var view = new MarkdownView { ChatMode = true, TurnNumber = 1, Padding = new Thickness(0) };
        var host = new Window { Owner = owner, Content = new ScrollViewer { Content = view }, Width = 620, Height = 900 };
        host.Show();
        try
        {
            var samples = new[] {
                "中文甲乙**中文粗体段**普通中文**多处强调**和**不同长度的另一处粗体**。\n\n" +
                "一串文字**中文强调**紧邻**后面的汉字**。\n\n" +
                "边界 **这是一个相当长的中文强调段落用于覆盖跨行换行和逐字符移动位置** 尾部。\n\n" +
                "英文 **coffee**、**fi**、**hello world**、**中文 English**、**末尾空格 **、** 标题**。\n\n" +
                "链接 **[中文链接](https://example.com)** 和 **[other link](https://example.com)**。\n\n" +
                "## **标题中有中文**\n\n- **列表强调**之后中文\n- **换行很长的列表强调内容来回验证窗口缩小再扩展的情况**。",
                "**天地玄黄宇宙洪荒**字前后**天地玄黄宇宙洪荒**。\n\n" +
                string.Join("", Enumerable.Range(0, 24).Select(i => $"字{i}**天地玄黄**连接"))
            };
            var checks = 0;
            void Compare(string description)
            {
                Invoke(view, "LayoutWaves");
                var warm = (DrawingGroup)Field(view, "_waves")!;
                var cache = Field(view, "_waveRunWidths")!; cache.GetType().GetMethod("Clear")!.Invoke(cache, null);
                Invoke(view, "LayoutWaves");
                var a = Points(warm); var b = Points((DrawingGroup)Field(view, "_waves")!);
                if (a.Count != b.Count) throw new Exception($"Wave count differs {description}: {a.Count} vs {b.Count}");
                for (var i = 0; i < a.Count; i++)
                    if (Math.Abs(a[i].X - b[i].X) > .001 || Math.Abs(a[i].Y - b[i].Y) > .001)
                        throw new Exception($"Wave differs {description} point={i}: {a[i]} vs {b[i]}");
                checks++;
            }
            foreach (var font in new[] { "Microsoft YaHei", "SimSun", "Arial" })
            foreach (var size in new[] { 16d, 24d })
            foreach (var mode in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
            foreach (var source in samples)
            {
                view.FontFamily = new FontFamily(font); view.FontSize = size;
                TextOptions.SetTextFormattingMode(view, mode); view.Markdown = source;
                foreach (var width in new[] { 900d, 350, 800, 450, 700, 280, 1020, 380, 620, 900 })
                {
                    host.Width = width; host.UpdateLayout();
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                    Compare($"font={font} size={size} mode={mode} width={width}");
                }
            }
            view.Markdown = "# **第一标题**\n\n**被折叠的重点**和**随行文字**\n\n# 第二标题\n\n**保持显示的重点**。";
            view.ConfigureFolding(new Dictionary<string, bool>(), "wave-cache", () => { });
            foreach (var font in new[] { "Microsoft YaHei", "SimSun", "Arial" })
            foreach (var language in new[] { "zh-CN", "ja-JP" })
            foreach (var mode in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
            {
                view.FontFamily = new FontFamily(font); view.Language = XmlLanguage.GetLanguage(language); TextOptions.SetTextFormattingMode(view, mode);
                host.Width = 460; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var heading = (Paragraph)((Section)view.Document.Blocks.FirstBlock!).Blocks.FirstBlock!;
                var toggle = heading.Inlines.OfType<Hyperlink>().Single();
                toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Compare($"font-change={font} lang={language} mode={mode} collapsed");
                toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Compare($"font-change={font} lang={language} mode={mode} expanded");
            }
            var waveCache = Field(view, "_waveRunWidths")!;
            var clear = waveCache.GetType().GetMethod("Clear")!;
            var lookup = waveCache.GetType().GetMethod("TryGetValue")!;
            int CacheCount() => ((IEnumerable<Inline>)Field(view, "_strongRuns")!).Count(run => (bool)lookup.Invoke(waveCache, new object?[] { run, null })!);
            foreach (var custom in new[] { false, true })
            {
                Typography.SetEastAsianWidths(view, custom ? FontEastAsianWidths.Full : FontEastAsianWidths.Normal);
                Typography.SetStylisticSet1(view, custom);
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                clear.Invoke(waveCache, null); Invoke(view, "LayoutWaves");
                var count = CacheCount();
                if ((!custom && count == 0) || (custom && count != 0)) throw new Exception($"Typography caching mismatch: custom={custom}, cached={count}");
                Compare($"custom typography={custom}");
            }
            view.ClearValue(Typography.EastAsianWidthsProperty); view.ClearValue(Typography.StylisticSet1Property);
            TextOptions.SetTextFormattingMode(view, TextFormattingMode.Ideal);
            view.Markdown = "**字体渲染测试** 和 **更多强调**";
            host.Width = 650; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var localRun = ((IEnumerable<Inline>)Field(view, "_strongRuns")!).OfType<Run>().First();
            var localSpan = (Span)localRun.Parent;
            foreach (var applyToRun in new[] { true, false })
            {
                localRun.ClearValue(TextOptions.TextFormattingModeProperty); localSpan.ClearValue(TextOptions.TextFormattingModeProperty);
                TextOptions.SetTextFormattingMode(applyToRun ? localRun : localSpan, TextFormattingMode.Display);
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                if (TextOptions.GetTextFormattingMode(localRun) != TextFormattingMode.Display) throw new Exception("Run did not inherit local text formatting mode");
                Invoke(view, "LayoutWaves");
                var lookupArguments = new object?[] { localRun, null };
                if (!(bool)lookup.Invoke(waveCache, lookupArguments)! || lookupArguments[1]!.GetType().GetField("FormattingMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lookupArguments[1]) is not TextFormattingMode.Display)
                    throw new Exception($"Run cache did not record effective formatting mode; applied to {(applyToRun ? "run" : "span")}");
                Compare($"local formatting applied to {(applyToRun ? "run" : "span")}");
            }
            Console.WriteLine($"PASS: {checks} cached/fresh wave comparisons across fonts, sizes, formatting modes, links, whitespace and wrapping.");
        }
        finally { host.Close(); }
    }

    private static List<Point> Points(DrawingGroup drawing)
    {
        var result = new List<Point>();
        foreach (var child in drawing.Children.OfType<GeometryDrawing>())
            foreach (var figure in child.Geometry.GetFlattenedPathGeometry().Figures)
            {
                result.Add(figure.StartPoint);
                foreach (var segment in figure.Segments)
                    if (segment is PolyLineSegment poly) result.AddRange(poly.Points);
                    else if (segment is LineSegment line) result.Add(line.Point);
            }
        return result;
    }
}
