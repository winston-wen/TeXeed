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
    private static async Task CheckAiUiPerformanceAsync(MainWindow owner, string output, string? baseline, bool normalizeTypography)
    {
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 900, Height = 760, Content = panel, Title = "AI UI performance · synthetic fixtures" };
        host.Show(); await panel.SetProjectAsync(Path.Combine(output, "performance.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        var details = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"line {i}: synthetic tool result"));
        for (var turn = 0; turn < 120; turn++)
            book.Turns.Add(new("Synthetic question " + turn, "Synthetic answer.") { SentAt = DateTimeOffset.UnixEpoch, Segments = Enumerable.Range(0, 15)
                .Select(i => new AiSegment { Kind = "activity", Text = "Synthetic activity " + i, Details = details }).Append(new() { Kind = "final", Text = "Synthetic answer." }).ToList() });
        Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var samples = new Dictionary<string, List<double>> { ["scroll_layout_ms"] = [], ["detail_toggle_ms"] = [], ["resize_ms"] = [] };
        var allocations = new List<long>();
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
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            watch.Stop(); if (run > 0) { samples["scroll_layout_ms"].Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before); }
            var row = Descendants<AiActivityView>(panel).FirstOrDefault(v => v.IsVisible);
            if (row is null) throw new Exception("Performance fixture did not realize activity rows.");
            NormalizeTypography();
            watch.Restart(); row.IsExpanded = true; host.UpdateLayout(); row.IsExpanded = false; host.UpdateLayout(); watch.Stop();
            if (run > 0) samples["detail_toggle_ms"].Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart(); host.Width = run % 2 == 0 ? 620 : 900; host.UpdateLayout(); watch.Stop();
            if (run > 0) samples["resize_ms"].Add(watch.Elapsed.TotalMilliseconds);
        }
        double Median(IEnumerable<double> values) { var rows = values.Order().ToArray(); return rows[rows.Length / 2]; }
        var result = samples.ToDictionary(k => k.Key, k => Median(k.Value));
        result["scroll_allocated_bytes"] = Median(allocations.Select(v => (double)v));
        result["realized_turns"] = Descendants<AiTurnView>(panel).Count();
        result["realized_activities"] = Descendants<AiActivityView>(panel).Count();
        result["realized_controls"] = Descendants<Control>(panel).Count();
        result["visible_controls"] = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 });
        File.WriteAllText(Path.Combine(output, "ui-performance.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "ui-performance-samples.json"), JsonSerializer.Serialize(samples));
        if (result["realized_turns"] > 12) throw new Exception("AI history no longer bounds realized turns.");
        if (baseline is not null)
        {
            var previous = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(baseline))!;
            foreach (var name in samples.Keys)
                if (result[name] > Math.Max(previous[name] * 1.3, previous[name] + 8)) throw new Exception($"UI performance regression: {name} {previous[name]:F2} -> {result[name]:F2} ms");
            if (result["scroll_allocated_bytes"] > Math.Max(previous["scroll_allocated_bytes"] * 1.3, previous["scroll_allocated_bytes"] + 262144))
                throw new Exception("UI performance regression: scroll allocation increased.");
        }
        Console.WriteLine("AI UI performance (120 turns, 1800 activities, synthetic 2000-line details): " + JsonSerializer.Serialize(result));
        host.Close();
    }
}
