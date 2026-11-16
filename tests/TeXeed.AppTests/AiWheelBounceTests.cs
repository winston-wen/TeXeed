using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Reflection;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiWheelBounceAsync(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("TEXEED_ONLY_ANON_WHEEL") == "1")
        {
            var selected = Environment.GetEnvironmentVariable("TEXEED_WHEEL_FIXTURE")
                ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "tests", "TeXeed.AppTests", "Fixtures", "ai-wheel-anonymous.json");
            await CheckLongTurnWheelAsync(window, selected);
            return;
        }
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        for (var i = 0; i < 12; i++)
            book.Turns.Add(new AiTurn("问题 " + i, string.Join("\n\n", Enumerable.Range(0, (i % 3 + 1) * 25).Select(x => $"回答 {i} 的第 {x} 段，包含一段较长的解释文字以模拟用户的实际长会话。"))));
        Invoke(window, "OpenAiTab"); Invoke(panel, "RefreshTranscript");
        window.UpdateLayout(); await Task.Delay(400); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        if (scroll.ScrollableHeight < 500) throw new Exception("Conversation wheel fixture has no overflow");
        var wheelChanges = new List<string>();
        ScrollChangedEventHandler wheelChanged = (_, e) => { if (ReferenceEquals(e.OriginalSource, scroll)) wheelChanges.Add($"{scroll.VerticalOffset:F2}({e.VerticalChange:+0.00;-0.00;0.00},{e.ExtentHeightChange:+0.00;-0.00;0.00})"); };
        scroll.ScrollChanged += wheelChanged;
        for (var i = 0; i < 20; i++)
        {
            var before = scroll.VerticalOffset;
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            double LastTop() => views.TryGetValue(book.Turns.Last().Id, out var last) && last.IsLoaded ? last.TranslatePoint(new Point(), scroll).Y : double.NaN;
            var beforeTop = LastTop(); wheelChanges.Clear();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            scroll.RaiseEvent(wheel);
            window.UpdateLayout(); await Task.Delay(200); window.UpdateLayout();
            var after = scroll.VerticalOffset;
            Console.WriteLine($"AI wheel up {i}: {before:F1} -> {after:F1} / {scroll.ScrollableHeight:F1}; top {beforeTop:F1} -> {LastTop():F1}; handled={wheel.Handled}; restoring={Field(panel, "_restoringReading")}; changes={string.Join(',', wheelChanges)}");
            if (before > 1 && after >= before - .1) throw new Exception($"Conversation wheel did not move upward: {before:F1} -> {after:F1}");
        }
        scroll.ScrollChanged -= wheelChanged;
        Console.WriteLine("PASS: upward wheel in multi-turn conversation does not reverse");
        var tableSource = "| 核对项 | 当前项目 | 上游项目 | 第四列 |\n|---|---|---|---|\n" +
            string.Join("\n", Enumerable.Range(0, 8).Select(i => $"| 第 {i} 行 | `file_{i}.tex` 和公式 $x_i$ | 很长的标识符abcdefghijklmno 以及更多内容 | 说明abcdefghijklmno |"));
        var answer = "前言段落。\n\n" + tableSource + "\n\n" + string.Join("\n\n", Enumerable.Repeat("后文需要足够的滚动空间。", 25));
        book.Turns.Clear();
        book.Turns.Add(new AiTurn("第一轮", string.Join("\n\n", Enumerable.Repeat("第一轮内容。", 10))));
        book.Turns.Add(new AiTurn("比较不同的方案", answer) { Segments = [new() { Id="activity", Kind="activity", Text="展开的过程" }, new() { Id="answer", Kind="final", Text=answer }] });
        Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(1000); window.UpdateLayout();
        scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var table = AnnotationVisuals<ScrollViewer>(panel).FirstOrDefault(s => s.ScrollableWidth > 10 && s.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled);
        if (table is null) throw new Exception("Wide table fixture has no horizontal overflow");
        var tableView = (MarkdownView)table.Content;
        var tableHost = (ScrollViewer)tableView.Template.FindName("PART_ContentHost", tableView);
        table.BringIntoView(); window.UpdateLayout(); await Task.Delay(200); window.UpdateLayout();
        Console.WriteLine($"Table host before: offset={tableHost.VerticalOffset:F2} extent={tableHost.ScrollableHeight:F2}; table extent={table.ScrollableHeight:F2}; outer={scroll.VerticalOffset:F2}");
        for (var i = 0; i < 15; i++)
        {
            var before = scroll.VerticalOffset;
            tableView.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            window.UpdateLayout(); await Task.Delay(200); window.UpdateLayout();
            Console.WriteLine($"Table wheel {i}: outer {before:F2} -> {scroll.VerticalOffset:F2}, inner {tableHost.VerticalOffset:F2}/{tableHost.ScrollableHeight:F2}, table {table.VerticalOffset:F2}/{table.ScrollableHeight:F2}, loaded={tableView.IsLoaded}, visible={tableView.IsVisible}, source={PresentationSource.FromVisual(tableView) is not null}");
            if (before > 1 && scroll.VerticalOffset >= before - 1) throw new Exception("Wheel over table did not move upward");
        }
        // Return to the original conversation fixture for the deferred restore check.
        book.Turns.Clear();
        for (var i = 0; i < 12; i++) book.Turns.Add(new AiTurn("问题 " + i, string.Join("\n\n", Enumerable.Range(0, (i % 3 + 1) * 25).Select(x => $"回答 {i} 的第 {x} 段，包含一段较长的解释文字以模拟用户的实际长会话。"))));
        Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(400); scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(500); window.UpdateLayout();
        var turns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        var active = turns[book.Turns.Last().Id];
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MarkdownView).GetProperty("RenderTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(active.Answers[0], deferred.Task);
        book.Active.ReadingTurnId = active.Turn.Id;
        book.Active.ReadingTurnOffset = 0;
        typeof(AiPanel).GetField("_restoringReading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, true);
        Invoke(panel, "RestoreReadingAnchor");
        await Task.Delay(150); window.UpdateLayout();
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        var afterWheel = scroll.VerticalOffset;
        deferred.SetResult(); await Task.Delay(250); window.UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - afterWheel) > 1)
            throw new Exception($"Delayed reading restoration overrode user wheel: {afterWheel:F1} -> {scroll.VerticalOffset:F1}");
        Console.WriteLine("PASS: user wheel cancels pending reading restoration");
        async Task CheckRestoreInputAsync(string name, Action input, bool shouldCancel)
        {
            var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(MarkdownView).GetProperty("RenderTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(active.Answers[0], delayed.Task);
            book.Active.ReadingTurnId = active.Turn.Id; book.Active.ReadingTurnOffset = 0;
            typeof(AiPanel).GetField("_restoringReading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, true);
            Invoke(panel, "RestoreReadingAnchor"); await Task.Delay(100); window.UpdateLayout();
            var version = (int)Field(panel, "_readingRestoreVersion")!;
            input();
            var cancelled = !(bool)Field(panel, "_restoringReading")! && (int)Field(panel, "_readingRestoreVersion")! > version;
            if (cancelled != shouldCancel) throw new Exception($"Initial restore did not handle {name} as expected");
            delayed.SetResult(); await Task.Delay(150); window.UpdateLayout();
        }
        await CheckRestoreInputAsync("PageUp", () => scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.PageUp) { RoutedEvent = UIElement.PreviewKeyDownEvent }), true);
        var scrollbar = AnnotationVisuals<System.Windows.Controls.Primitives.ScrollBar>(scroll).First(bar => bar.Orientation == Orientation.Vertical);
        await CheckRestoreInputAsync("scrollbar", () => scrollbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent }), true);
        await CheckRestoreInputAsync("touch movement", () => Invoke(panel, "NavigationTouchMove", panel, null!), true);
        var composer = (TextBox)panel.FindName("Composer");
        await CheckRestoreInputAsync("composer input", () => composer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = UIElement.PreviewKeyDownEvent }), false);
        Console.WriteLine("PASS: keyboard, scrollbar and touch cancel initial restore; composer input does not");
        var fixture = Environment.GetEnvironmentVariable("TEXEED_WHEEL_FIXTURE")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "tests", "TeXeed.AppTests", "Fixtures", "ai-wheel-anonymous.json");
        await CheckLongTurnWheelAsync(window, fixture);
        await CheckAsyncLayoutAnchorAsync(window);
    }
    private static async Task CheckLongTurnWheelAsync(MainWindow window, string fixture)
    {
        if (Environment.GetEnvironmentVariable("TEXEED_NARROW_WHEEL") == "1") { window.Width = 900; window.Height = 700; }
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(panel, "_book")!;
        var source = System.Text.Json.JsonSerializer.Deserialize<AiNotebook>(System.IO.File.ReadAllText(fixture))!;
        var conversation = source.Conversations.Single();
        if (double.TryParse(Environment.GetEnvironmentVariable("TEXEED_WHEEL_CACHE"), out var cache))
        {
            var history = (ItemsControl)panel.FindName("History");
            VirtualizingPanel.SetCacheLength(history, new VirtualizationCacheLength(cache));
            VirtualizingPanel.SetCacheLengthUnit(history, VirtualizationCacheLengthUnit.Page);
        }
        book.Turns.Clear(); book.Turns.AddRange(conversation.Turns);
        Invoke(window, "OpenAiTab"); Invoke(panel, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(2500); window.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        scroll.ScrollToEnd(); window.UpdateLayout(); await Task.Delay(1000); window.UpdateLayout();
        var realizedTurns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
        double LastTurnTop() => realizedTurns.TryGetValue(book.Turns.Last().Id, out var last) && last.IsLoaded
            ? last.TranslatePoint(new Point(), scroll).Y : double.NaN;
        var wheelLines = SystemParameters.WheelScrollLines;
        if (wheelLines == 0) throw new Exception("The cross-turn fixture requires an enabled mouse wheel");
        var wheelStep = wheelLines < 0 ? scroll.ViewportHeight : wheelLines * 16d;
        var desiredTop = -Math.Min(wheelStep * 8, scroll.ViewportHeight * .7);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var top = LastTurnTop();
            if (!double.IsFinite(top)) throw new Exception("Cross-turn fixture did not realize the final turn");
            if (Math.Abs(top - desiredTop) < 2) break;
            scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + top - desiredTop, 0, scroll.ScrollableHeight));
            window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
        }
        var startingTop = LastTurnTop();
        if (!double.IsFinite(startingTop) || Math.Abs(startingTop - desiredTop) > 3)
            throw new Exception($"Cross-turn fixture did not reach its measured starting position: expected {desiredTop:F1}, actual {startingTop:F1}");
        window.Activate(); await Task.Delay(200);
        var changes = new List<string>();
        ScrollChangedEventHandler changed = (_, e) => { if (ReferenceEquals(e.OriginalSource, scroll)) changes.Add($"{scroll.VerticalOffset:F2}(vertical {e.VerticalChange:F2},extent {e.ExtentHeightChange:F2},pending {Field(panel, "_layoutAnchorPending")},version {Field(panel, "_layoutAnchorVersion")})"); };
        RoutedEventHandler notified = (_, e) => changes.Add($"notify({e.OriginalSource.GetType().Name},pending {Field(panel, "_layoutAnchorPending")},version {Field(panel, "_layoutAnchorVersion")})");
        scroll.ScrollChanged += changed;
        scroll.AddHandler(MarkdownView.AsyncLayoutChangingEvent, notified, true);
        try
        {
            var crossedUp = 0; var crossedDown = 0;
            foreach (var (delta, count, wait) in new[] { (120, 25, 200), (-120, 25, 25), (120, 25, 25), (-120, 25, 200) })
            {
                for (var i = 0; i < count; i++)
                {
                    var before = scroll.VerticalOffset; changes.Clear();
                    var realized = realizedTurns;
                    double Top() => LastTurnTop();
                    var beforeTop = Top();
                    string Views() => string.Join("; ", realized.Values.Where(v => v.IsLoaded).Select(v => $"turn={book.Turns.IndexOf(v.Turn)},top={v.TranslatePoint(new Point(), scroll).Y:F1},height={v.ActualHeight:F1},answers={string.Join('/', v.Answers.Select(a => a.RenderTask.IsCompleted ? 'Y' : 'N'))},formulas={AnnotationVisuals<ChatFormula>(v).Count(f => !f.RenderTask.IsCompleted)}/{AnnotationVisuals<ChatFormula>(v).Count()}"));
                    string Anchor()
                    {
                        var anchor = Field(panel, "_layoutAnchor"); if (anchor is null) return "none";
                        var owner = (FrameworkElement)Field(anchor, "Owner")!;
                        var ownerTurn = realized.Values.FirstOrDefault(v => v.IsAncestorOf(owner));
                        return $"turn={book.Turns.IndexOf(ownerTurn?.Turn!)},type={owner.GetType().Name},ownerTop={owner.TranslatePoint(new Point(), scroll).Y:F1},savedY={Field(anchor, "Y")},savedOffset={Field(anchor, "Offset")},pointer={Field(anchor, "Pointer") is not null}";
                    }
                    var beforeAnchor = Anchor();
                    var beforeViews = Views();
                    var hit = scroll.InputHitTest(new Point(scroll.ActualWidth * .88, scroll.ActualHeight * .57));
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    if (hit is UIElement element) element.RaiseEvent(wheel);
                    else if (hit is ContentElement content) content.RaiseEvent(wheel);
                    else throw new Exception("Cannot hit-test the conversation viewport");
                    await Task.Delay(wait); window.UpdateLayout();
                    var afterTop = Top();
                    if (delta > 0 && beforeTop < 0 && afterTop >= 0) crossedUp++;
                    if (delta < 0 && beforeTop >= 0 && afterTop < 0) crossedDown++;
                    if (double.IsFinite(beforeTop) && double.IsFinite(afterTop) && beforeTop > -scroll.ViewportHeight && beforeTop < scroll.ViewportHeight &&
                        (delta > 0 ? afterTop < beforeTop - 2 : afterTop > beforeTop + 2))
                        throw new Exception($"Visible conversation reversed during wheel {delta}: turn top {beforeTop:F2} -> {afterTop:F2}; offsets {before:F2} -> {scroll.VerticalOffset:F2}; path {string.Join(',', changes)}; before {beforeViews}; after {Views()}; anchor {beforeAnchor}; hit={hit.GetType().Name}");
                }
            }
            if (crossedUp < 2 || crossedDown < 2) throw new Exception($"Wheel did not cross both turn boundaries: up {crossedUp}, down {crossedDown}");
            Console.WriteLine("PASS: anonymous legacy conversation preserves visible position on slow and rapid up/down wheel across turns");
        }
        finally { scroll.ScrollChanged -= changed; scroll.RemoveHandler(MarkdownView.AsyncLayoutChangingEvent, notified); }
    }

}
