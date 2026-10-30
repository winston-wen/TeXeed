using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        host.Show();
        var measurements = new List<object>();
        static double[] Baselines(FrameworkElement element, Visual relative) => GlyphInk(element, relative).Select(i => i.Baseline).ToArray();
        try
        {
            ((TextBox)panel.FindName("Composer")).Text = "检查合成说明与 Rm 正文，保持字体、间距和中轴一致。";
            var request = panel.SubmitAsync(); await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var segments = (ObservableCollection<AiSegment>)Field(panel, "_liveSegments")!;
            segments.Add(new() { Kind = "activity", Text = "已查看项目目录" });
            segments.Add(new() { Kind = "text", Text = "我会检查合成模板与调用位置。\n然后修改显示方式，保留条件。" });
            segments.Add(new() { Kind = "activity", Text = "已读取源码 · sample.tex" });
            segments.Add(new() { Kind = "text", Text = "检查完成，接着调整示例中的排版。" });
            var status = (AiReplyStatus)panel.FindName("LiveReplyStatus"); status.PendingText = "正在生成回复";
            foreach (var width in new[] { 860d, 420d })
            {
                host.Width = width; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var live = (FrameworkElement)panel.FindName("LiveContainer");
                var question = (MarkdownView)panel.FindName("LiveQuestion"); var number = (TextBlock)panel.FindName("LiveTurnNumber");
                var questionInk = GlyphInk(question, live); var numberInk = GlyphInk(number, live);
                var rows = Descendants<AiActivityView>(live).Where(r => r.IsVisible).ToArray();
                var bodies = Descendants<MarkdownView>((FrameworkElement)panel.FindName("LiveSegments")).Where(v => v.IsVisible).ToArray();
                var gaps = new[] {
                    Baselines(bodies[0], live).Min() - Baselines((TextBlock)rows[0].Header, live).Max(),
                    Baselines((TextBlock)rows[1].Header, live).Min() - Baselines(bodies[0], live).Max(),
                    Baselines(bodies[1], live).Min() - Baselines((TextBlock)rows[1].Header, live).Max(),
                    Baselines(status, live).Min() - Baselines(bodies[1], live).Max() };
                var label = Descendants<TextBlock>(status).Single(); var ink = GlyphInk(label, status).Select(i => i.Bounds).Aggregate(Rect.Empty, (a, r) => { a.Union(r); return a; });
                var dot = Descendants<Ellipse>(status).Single(); var box = dot.TransformToAncestor(status).TransformBounds(new Rect(dot.RenderSize));
                var dotDelta = box.Top + box.Height / 2 - ink.Top - ink.Height / 2;
                var data = new { width, numberFont = number.FontFamily.Source, numberFace = numberInk[0].Face, questionFace = questionInk[0].Face, numberWeight = numberInk[0].Weight, gaps, dotDelta, dotDiameter = dot.ActualWidth };
                measurements.Add(data);
                Capture(host, Path.Combine(output, "live-" + width + ".png"));
                if (enforce)
                {
                    if (number.FontFamily.Source != question.FontFamily.Source || numberInk[0].Face != questionInk.First(i => i.Text.Contains("Rm")).Face) throw new Exception("Live ordinal does not use the question's serif family");
                    if (gaps.Any(g => Math.Abs(g - 26.5) > .6)) throw new Exception("Live process/body baseline gaps differ: " + string.Join(",", gaps));
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
            if (enforce && (bounds.Count != 3 || bounds.Any(b => Math.Abs(b.Height - 25) > .1))) throw new Exception("Linked inline code background exceeds its 25-DIP line");
            if (enforce && Math.Abs(bounds[1].Y - bounds[2].Y) > .1) throw new Exception("Linked and ordinary code backgrounds have different vertical alignment");
            Console.WriteLine(enforce ? "PASS: live serif ordinal, uniform process/body spacing, 13-DIP ink-centered indicator and 25-DIP inline code surfaces." : "Recorded live typography, status cost and inline code surfaces.");
        }
        finally { host.Close(); }
    }
}
