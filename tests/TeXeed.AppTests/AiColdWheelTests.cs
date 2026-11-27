using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckColdWheelAsync(Window owner, string output)
    {
        var project = Path.Combine(output, "cold-wheel.tex");
        var book = new AiNotebook();
        const string table = "| 项目 | 测量方法 |\n| --- | --- |\n| 长度 | 读刻度 |\n| 用时 | 读秒表 |";
        const string paragraph = "先读取本组样本中的数据，分别比较测量前后的数值，并保留每次观测的有效位数。";
        const string formula = @"z^x=\operatorname{Rec}(w',c_x)";
        var mathCache = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg"));
        using (var fixture = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.eqref.single.svg")!)
        using (var cache = File.Create(Path.Combine(mathCache.FullName, ChatTexRenderer.CacheFileName(formula, true))))
            fixture.CopyTo(cache);
        var first = string.Join("\n\n", Enumerable.Range(1, 15).Select(n =>
            $"## 测量 {n}\n\n{paragraph}{paragraph}\n\n$$\n{formula}\n$$\n\n" + (n % 5 == 0 ? table : paragraph)));
        var rows = AiRevisionText.Parse("answer", first);
        book.Turns.Add(new("读一遍实验记录。", first)
        {
            Segments = [new() { Id = "answer", Kind = "final", Text = first }],
            RevisionHistory = [new("revised", "user", DateTimeOffset.Now, rows) { Sources = new() { ["answer"] = first } }]
        });
        for (var n = 2; n <= 11; n++)
        {
            var text = $"{paragraph}\n\n{table}\n\n{paragraph}\n\n{{{{radio:method:distance:记录距离}}}}\n\n{{{{radio:method:time:记录时间}}}}";
            var segments = Enumerable.Range(1, n % 4 + 3).Select(i => new AiSegment { Id = $"step{i}", Kind = "activity", Text = $"已记录第 {i} 次读数" }).ToList();
            segments.Add(new() { Id = "answer", Kind = "final", Text = text });
            book.Turns.Add(new($"第 {n} 组怎样读取？", text) { Segments = segments, Collapsed = n is >= 2 and <= 5 or 7 });
        }
        book.Active.ReadingTurnId = book.Turns[^1].Id;
        var store = new AiWorkspaceStore(project, book);
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Content = panel, Width = 980, Height = 1060, Left = -4000, Top = -1000, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            panel.Bind(store, book.Active.Id);
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            await WaitUntilAsync(() => !(bool)Field(panel, "_restoringReading")!, "The restored conversation did not settle.");
            scroll.ScrollToEnd();
            await SettleFoldLayoutAsync(host);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            void WheelUp()
            {
                var hit = scroll.InputHitTest(new Point(scroll.ViewportWidth * .71, scroll.ViewportHeight * .47));
                var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                if (hit is UIElement element) element.RaiseEvent(e);
                else if (hit is ContentElement text) text.RaiseEvent(e);
                else throw new Exception("No reader target under the wheel.");
            }
            if (Environment.GetCommandLineArgs().Contains("--measure-cold-wheel"))
            {
                var numberSix = book.Turns[5].Id;
                var reverseDip = 0d;
                double? previousTop = null;
                void RecordSix(object? sender, EventArgs args)
                {
                    if (!views.TryGetValue(numberSix, out var view) || !view.IsVisible) return;
                    var top = view.TranslatePoint(new Point(), scroll).Y;
                    if (previousTop is { } previous && previous < scroll.ViewportHeight && previous + view.ActualHeight > 0)
                        reverseDip = Math.Max(reverseDip, previous - top);
                    previousTop = top;
                }
                const int pulses = 72;
                var syncTicks = 0L;
                var syncBytes = 0L;
                CompositionTarget.Rendering += RecordSix;
                var start = Stopwatch.GetTimestamp();
                var before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    for (var i = 0; i < pulses; i++)
                    {
                        var tick = Stopwatch.GetTimestamp();
                        var bytes = GC.GetAllocatedBytesForCurrentThread();
                        WheelUp();
                        syncTicks += Stopwatch.GetTimestamp() - tick;
                        syncBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
                        await Task.Delay(1);
                    }
                    await SettleFoldLayoutAsync(host);
                }
                finally { CompositionTarget.Rendering -= RecordSix; }
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var uiBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var visibleControls = Descendants<Control>(scroll).Count(c => c.IsVisible &&
                    c.TransformToAncestor(scroll).TransformBounds(new Rect(c.RenderSize)).IntersectsWith(new Rect(scroll.RenderSize)));
                var result = new { pulses, syncMs = syncTicks * 1000d / Stopwatch.Frequency, syncBytes, elapsedMs, uiBytes, visibleControls, reverseDip };
                File.WriteAllText(Path.Combine(output, "cold-wheel-perf.json"), JsonSerializer.Serialize(result));
                return;
            }
            var snapshot = new List<string>();
            var visited = new HashSet<int>();
            (int Turn, double Top, double Offset)? last = null;
            int wheel = 0;
            string? failure = null;
            void Observe(string cause)
            {
                if (failure is not null) return;
                var probe = scroll.ViewportHeight / 2;
                var visible = views.Values.Where(v => v.IsLoaded && v.IsVisible)
                    .Select(v => (View: v, Top: v.TranslatePoint(new Point(), scroll).Y))
                    .OrderBy(row => row.Top).ToArray();
                var hit = visible.LastOrDefault(r => r.Top <= probe && r.Top + r.View.ActualHeight > probe);
                if (hit.View is null)
                {
                    // Between two turns a real paragraph margin occupies at most 30 DIP.
                    if (!visible.Any(r => r.Top <= probe + 30 && r.Top + r.View.ActualHeight >= probe - 30))
                        failure = $"Reader centre became blank during upward wheel input {wheel}.";
                    return;
                }
                var number = hit.View.TurnNumber;
                var current = (Turn: number, Top: hit.Top, Offset: scroll.VerticalOffset);
                if (last is not { } old || old != current)
                {
                    snapshot.Add($"{wheel} {cause} turn={number} top={hit.Top:F2} offset={scroll.VerticalOffset:F2} extent={scroll.ExtentHeight:F2} visible={string.Join("|", visible.Select(v => $"{v.View.TurnNumber}:{v.Top:F0}:{v.View.ActualHeight:F0}"))}");
                    if (last is { } prior && (number > prior.Turn || number == prior.Turn && hit.Top < prior.Top - 2))
                        failure = $"Visible content reversed while scrolling upward: turn {prior.Turn} at {prior.Top:F1} -> {number} at {hit.Top:F1}.";
                    last = current;
                    visited.Add(number);
                }
            }
            EventHandler frame = (_, _) => Observe("frame");
            Observe("start");
            if (last is not { Turn: >= 8 } || views.TryGetValue(book.Turns[0].Id, out var firstView) && firstView.IsLoaded &&
                firstView.TranslatePoint(new Point(0, firstView.ActualHeight), scroll).Y > -scroll.ViewportHeight)
                throw new Exception("Did not start on late turns outside the first turn's realized viewport. " + string.Join(" / ", snapshot));
            CompositionTarget.Rendering += frame;
            try
            {
                for (; wheel < 230 && failure is null; wheel++)
                {
                    if (last is { Turn: 1 })
                    {
                        var answer = views[book.Turns[0].Id].Answers.Single();
                        if (answer.TranslatePoint(new Point(0, answer.ActualHeight), scroll).Y > scroll.ViewportHeight * .8) break;
                    }
                    WheelUp();
                    Observe("wheel");
                    await Task.Delay(1);
                }
                if (failure is null)
                {
                    await SettleFoldLayoutAsync(host);
                    var body = views[book.Turns[0].Id].Answers.Single();
                    var near = scroll.TranslatePoint(new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2), body);
                    var position = body.GetPositionFromPoint(near, true) ?? throw new Exception("No readable text in the first answer.");
                    double TextY() => body.TranslatePoint(position.GetCharacterRect(LogicalDirection.Forward).TopLeft, scroll).Y;
                    var stopY = TextY();
                    await AwaitMathAsync(body);
                    await Task.Delay(350);
                    await SettleFoldLayoutAsync(host);
                    Observe("stopped");
                    var drift = TextY() - stopY;
                    if (Math.Abs(drift) > 2) failure = $"Visible text moved {drift:F1} DIP after scrolling stopped.";
                    Capture(host, Path.Combine(output, "cold-wheel-stopped.png"));
                }
            }
            finally
            {
                CompositionTarget.Rendering -= frame;
                File.WriteAllLines(Path.Combine(output, "cold-wheel-frames.txt"), snapshot);
            }
            if (failure is not null) throw new Exception(failure);
            if (!visited.Contains(6) || last is not { Turn: 1 }) throw new Exception("Did not cross the folded rows into the long first answer.");
            if (!await panel.NavigateSameConversationTurnAsync(book.Turns[9].Id)) throw new Exception("Cannot explicitly navigate from the long answer.");
            await SettleFoldLayoutAsync(host);
            if (!views.TryGetValue(book.Turns[9].Id, out var target) || target.TranslatePoint(new Point(), scroll).Y >= scroll.ViewportHeight ||
                target.TranslatePoint(new Point(0, target.ActualHeight), scroll).Y <= 0) throw new Exception("The navigation target was not visible.");
            Console.WriteLine("PASS: upward wheel crosses short, folded and long turns with no reversal.");
        }
        finally { host.Content = null; host.Close(); }
    }
}
