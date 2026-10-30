using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiUiPerformanceAsync(MainWindow owner, string output, string? baseline, bool normalizeTypography, bool withHeadings = false, bool complexQuestions = false, bool inlineCode = false)
    {
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 900, Height = 760, Content = panel, Title = "AI UI performance · synthetic fixtures" };
        host.Show(); await panel.SetProjectAsync(Path.Combine(output, "performance.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        var details = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"line {i}: synthetic tool result"));
        var answer = withHeadings ? string.Join("\n\n", Enumerable.Range(1, 6).Select(level => new string('#', level) + " 合成标题 Heading\n\nSynthetic answer.")) : "Synthetic answer.";
        if (inlineCode) answer = "查看 " + string.Join("、", Enumerable.Range(1, 6).Select(i => "`sample_" + i + ".tex:12`")) + "。\n\n普通 `check` 代码与正文。";
        var questions = complexQuestions ? new[] { "普通中文首行包含 Rm。", "中文与 `code` 混排首行。", "- 列表首行含有中文。\n- 下一条。", string.Concat(Enumerable.Repeat("窄窗口自动换行，包含 Rm 和 code。", 8)) } : new[] { "Synthetic question" };
        for (var turn = 0; turn < 120; turn++)
            book.Turns.Add(new(questions[turn % questions.Length] + " " + turn, answer) { SentAt = DateTimeOffset.UnixEpoch, Segments = Enumerable.Range(0, 15)
                .Select(i => new AiSegment { Kind = "activity", Text = "Synthetic activity " + i, Details = details }).Append(new() { Kind = "final", Text = answer }).ToList() });
        Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var samples = new Dictionary<string, List<double>> { ["scroll_layout_ms"] = [], ["detail_toggle_ms"] = [], ["resize_ms"] = [] };
        var allocations = new Dictionary<string, List<double>> { ["scroll_allocated_bytes"] = [], ["detail_allocated_bytes"] = [], ["resize_allocated_bytes"] = [] };
        void NormalizeTypography()
        {
            if (!normalizeTypography) return;
            foreach (var row in Descendants<AiActivityView>(panel))
            {
                row.FontSize = 16; row.Margin = new Thickness(0, 0, 0, 8.5);
                if (row.Header is TextBlock header)
                {
                    header.FontSize = 16; header.LineHeight = 25;
                    foreach (var text in header.Inlines.OfType<Run>()) text.FontSize = 16;
                }
            }
            foreach (var stamp in Descendants<TextBlock>(panel).Where(t => t.Text.StartsWith("发送时间：") || t.Text.StartsWith("答复时间："))) stamp.FontSize = 16;
            host.UpdateLayout();
        }
        for (var run = 0; run < 8; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            scroll.ScrollToVerticalOffset(run % 2 == 0 ? 0 : Math.Max(0, scroll.ScrollableHeight * .6)); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            watch.Stop(); if (run > 0) { samples["scroll_layout_ms"].Add(watch.Elapsed.TotalMilliseconds); allocations["scroll_allocated_bytes"].Add(GC.GetAllocatedBytesForCurrentThread() - before); }
            var row = Descendants<AiActivityView>(panel).FirstOrDefault(v => v.IsVisible);
            if (row is null) throw new Exception("Performance fixture did not realize activity rows.");
            NormalizeTypography();
            before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart(); row.IsExpanded = true; host.UpdateLayout(); row.IsExpanded = false; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); watch.Stop();
            if (run > 0) { samples["detail_toggle_ms"].Add(watch.Elapsed.TotalMilliseconds); allocations["detail_allocated_bytes"].Add(GC.GetAllocatedBytesForCurrentThread() - before); }
            before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart(); host.Width = run % 2 == 0 ? 620 : 900; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); watch.Stop();
            if (run > 0) { samples["resize_ms"].Add(watch.Elapsed.TotalMilliseconds); allocations["resize_allocated_bytes"].Add(GC.GetAllocatedBytesForCurrentThread() - before); }
        }
        double Median(IEnumerable<double> values) { var rows = values.Order().ToArray(); return rows[rows.Length / 2]; }
        var result = samples.ToDictionary(k => k.Key, k => Median(k.Value));
        foreach (var (name, values) in allocations) result[name] = Median(values);
        result["realized_turns"] = Descendants<AiTurnView>(panel).Count();
        result["realized_activities"] = Descendants<AiActivityView>(panel).Count();
        result["realized_controls"] = Descendants<Control>(panel).Count();
        result["visible_controls"] = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 });
        File.WriteAllText(Path.Combine(output, "ui-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "ui-performance-samples.json"), JsonSerializer.Serialize(new { elapsed = samples, allocated = allocations }));
        if (result["realized_turns"] > 12) throw new Exception("AI history no longer bounds realized turns.");
        if (baseline is not null)
        {
            var previous = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(baseline))!;
            foreach (var name in samples.Keys)
                if (result[name] > Math.Max(previous[name] * 1.3, previous[name] + 8)) throw new Exception($"UI performance regression: {name} {previous[name]:F2} -> {result[name]:F2} ms");
            foreach (var name in allocations.Keys)
                if (previous.TryGetValue(name, out var oldAllocation) && result[name] > Math.Max(oldAllocation * 1.3, oldAllocation + 262144))
                    throw new Exception("UI performance regression: " + name + " increased.");
        }
        Console.WriteLine("AI UI performance (120 turns, 1800 activities, synthetic 2000-line details): " + JsonSerializer.Serialize(result));
        host.Close();
    }
}
