using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiLiveLayoutAsync(MainWindow owner, string output, bool enforce)
    {
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var stalled = new StalledAiHandler();
        using var panel = new AiPanel(new AiConnection(stalled));
        await panel.SetProjectAsync(Path.Combine(output, "live-layout.tex"));
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Content = panel, Width = 860, Height = 820, Title = "实时阅读排版 · 合成资料" };
        if (!owner.ShowActivated)
        {
            host.ShowActivated = false; host.ShowInTaskbar = false; host.WindowStartupLocation = WindowStartupLocation.Manual;
            host.Left = owner.Left; host.Top = owner.Top;
        }
        host.Show();
        var measurements = new List<object>();
        static double[] Baselines(FrameworkElement element, Visual relative) => GlyphInk(element, relative).Select(i => i.Baseline).ToArray();
        try
        {
            ((TextBox)panel.FindName("Composer")).Text = "检查合成说明与 Rm 正文，保持字体、间距和中轴一致。";
            var request = panel.SubmitAsync(); await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var buffer = (AiStreamBuffer)Field(panel, "_followUpBuffer")!;
            buffer.Add(new("layout-workspace", "activity", "已查看项目目录"));
            buffer.Add(new("layout-first", "text", "我会检查合成模板与调用位置。\n然后修改显示方式，保留条件。"));
            buffer.Add(new("layout-read", "activity", "已读取源码 · sample.tex"));
            buffer.Add(new("layout-second", "text", "检查完成，接着调整示例中的排版。"));
            buffer.Add(new("pending", "status", "正在生成回复"));
            var status = (AiReplyStatus)panel.FindName("LiveReplyStatus");
            var segments = (ItemsControl)panel.FindName("LiveSegments");
            await WaitUntilAsync(() => segments.Items.Count == 4 && status.PendingText == "正在生成回复",
                "The live layout fixture did not render its stream");
            foreach (var width in new[] { 860d, 420d })
            {
                host.Width = width; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var live = (FrameworkElement)panel.FindName("LiveContainer");
                var question = (MarkdownView)panel.FindName("LiveQuestion"); var number = (TextBlock)panel.FindName("LiveTurnNumber");
                var questionInk = GlyphInk(question, live); var numberInk = GlyphInk(number, live);
                var rows = Descendants<AiActivityView>(live).Where(r => r.IsVisible).ToArray();
                var bodies = Descendants<MarkdownView>((FrameworkElement)panel.FindName("LiveSegments")).Where(v => v.IsVisible).ToArray();
                var processLines = rows.Select(row => Baselines((TextBlock)row.Header, live)).ToArray();
                var bodyLines = bodies.Select(body => Baselines(body, live)).ToArray();
                var statusLines = Baselines(status, live);
                Capture(host, Path.Combine(output, "live-" + width + ".png"));
                if (processLines.Length != 2 || bodyLines.Length != 2 || processLines.Any(values => values.Length == 0) ||
                    bodyLines.Any(values => values.Length == 0) || statusLines.Length == 0)
                    throw new Exception($"Missing live lines at {width} DIP: process {string.Join('/', processLines.Select(lines => lines.Length))}, " +
                        $"body {string.Join('/', bodies.Select((body, i) => $"{body.Markdown} ({bodyLines[i].Length})"))}, status {statusLines.Length}");
                var gaps = new[] {
                    bodyLines[0].Min() - processLines[0].Max(),
                    processLines[1].Min() - bodyLines[0].Max(),
                    bodyLines[1].Min() - processLines[1].Max(),
                    statusLines.Min() - bodyLines[1].Max() };
                var label = Descendants<TextBlock>(status).Single(); var ink = GlyphInk(label, status).Select(i => i.Bounds).Aggregate(Rect.Empty, (a, r) => { a.Union(r); return a; });
                var dot = Descendants<Ellipse>(status).Single(); var box = dot.TransformToAncestor(status).TransformBounds(new Rect(dot.RenderSize));
                var dotDelta = box.Top + box.Height / 2 - ink.Top - ink.Height / 2;
                var ordinalInk = numberInk.Single(sample => sample.Text.Contains(number.Text));
                var latin = questionInk.First(sample => sample.Text.Contains("Rm"));
                var firstBaseline = questionInk.Min(sample => sample.Baseline);
                var questionFirstLine = questionInk.Where(sample => Math.Abs(sample.Baseline - firstBaseline) < .5)
                    .Select(sample => sample.Bounds).Aggregate(Rect.Empty, (bounds, rect) => { bounds.Union(rect); return bounds; });
                var numberDelta = ordinalInk.Bounds.Top + ordinalInk.Bounds.Height / 2 - questionFirstLine.Top - questionFirstLine.Height / 2;
                var readingMetrics = bodies.Select(body => new { Size = body.FontSize, Height = ((Paragraph)body.Document.Blocks.FirstBlock!).LineHeight }).ToArray();
                var processMetrics = rows.Select(row => (TextBlock)row.Header)
                    .Select(header => new { Size = header.FontSize, Height = header.LineHeight }).ToArray();
                var data = new { width, numberFont = number.FontFamily.Source, numberFace = ordinalInk.Face, questionFace = latin.Face,
                    numberWeight = ordinalInk.Weight, ordinalInk.BoldSimulation, numberDelta, gaps, dotDelta, dotDiameter = dot.ActualWidth, readingMetrics, processMetrics };
                measurements.Add(data);
                Console.WriteLine(JsonSerializer.Serialize(data));
                if (enforce)
                {
                    if (number.FontFamily.Source != question.FontFamily.Source || number.FontSize != 18 || number.FontWeight != FontWeights.SemiBold ||
                        ordinalInk.Weight <= latin.Weight && (!ordinalInk.BoldSimulation || latin.BoldSimulation))
                        throw new Exception("Live ordinal did not render in the question's family at 18 DIP semibold");
                    if (Math.Abs(numberDelta) > .5) throw new Exception("Live ordinal and first question line have different ink axes: " + numberDelta);
                    if (readingMetrics.Any(body => body.Size != 18 || Math.Abs(body.Height - 28.125) > .01) ||
                        processMetrics.Any(process => process.Size != 13 || Math.Abs(process.Height - 26.5) > .01))
                        throw new Exception("Live output and process sizes/heights: " +
                            string.Join(", ", readingMetrics.Concat(processMetrics).Select(metric => $"{metric.Size:F3}/{metric.Height:F3}")));
                    if (Math.Abs(gaps[0] - gaps[2]) > .6 || Math.Abs(gaps[1] - gaps[3]) > .6 ||
                        gaps.Any(gap => gap < 23 || gap > 32))
                        throw new Exception("Live process/body baseline gaps differ: " + string.Join(",", gaps));
                    if (Math.Abs(dotDelta) > .5 || Math.Abs(dot.ActualWidth - label.FontSize) > .1) throw new Exception("Pending dot size/ink center differs: " + dotDelta);
                }
            }
            var statusTimes = new List<double>(); var statusAllocations = new List<long>();
            for (var sample = 0; sample < 9; sample++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                status.PendingText = sample % 2 == 0 ? "等待 AI 响应" : "正在生成回复";
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                watch.Stop();
                if (sample > 1) { statusTimes.Add(watch.Elapsed.TotalMilliseconds); statusAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated); }
            }
            var statusPerf = new { milliseconds = statusTimes.Order().ElementAt(3), allocatedBytes = statusAllocations.Order().ElementAt(3), samples = statusTimes, allocations = statusAllocations };
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await request.WaitAsync(TimeSpan.FromSeconds(5));
            var code = new MarkdownView { ChatMode = true, SourceLink = _ => { }, Markdown = "已修改 `preamble.tex` 中的模板。\n\n用 `main.tex` 编译。普通 `check` 代码。" };
            host.Width = 860; host.Content = code; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var bounds = (List<Rect>)Field(code, "_inlineCodeBounds")!;
            Capture(host, Path.Combine(output, "inline-code.png"));
            var glyphs = GlyphInk(code, code).Select(g => new { g.Text, g.Baseline, top = g.Bounds.Top, height = g.Bounds.Height });
            var codeRuns = ((System.Collections.IEnumerable)Field(code, "_inlineCodeSurfaces")!).Cast<object>().Select(s => (System.Windows.Documents.Run)s.GetType().GetProperty("Content")!.GetValue(s)!).ToArray();
            var caretMetrics = codeRuns.Select(r => new { r.Text, first = r.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward).ToString(), inner = r.ContentStart.GetPositionAtOffset(1)!.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward).ToString(), last = r.ContentEnd.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward).ToString() });
            File.WriteAllText(Path.Combine(output, "live-layout.json"), JsonSerializer.Serialize(new { live = measurements, statusPerf, code = bounds.Select(b => new { b.X, b.Y, b.Width, b.Height }), glyphs, caretMetrics }));
            if (enforce && (bounds.Count != 3 || bounds.Any(b => Math.Abs(b.Height - 28.125) > .1)))
                throw new Exception("Linked inline code background does not match the 28.125-DIP reading line");
            if (enforce && Math.Abs(bounds[1].Y - bounds[2].Y) > .1) throw new Exception("Linked and ordinary code backgrounds have different vertical alignment");
            Console.WriteLine(enforce ? "PASS: live semibold ordinal and ink alignment, distinct body and metadata sizes, process/body rhythm, and inline code surfaces." : "Recorded live typography, status cost and inline code surfaces.");
        }
        finally { host.Close(); }
    }
}
