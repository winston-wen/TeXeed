using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiTurnFoldingAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string error) { checks++; if (!ok) throw new Exception(error); }
        new AiCredentials { ApiKey = "fixture-never-send", UseApiKey = true }.Save();
        using var stalled = new StalledAiHandler();
        using var connection = new AiConnection(stalled);
        using var panel = new AiPanel(connection);
        var host = new Window { Owner = owner, Width = 620, Height = 850, Content = panel, Title = "AI 轮次折叠" };
        try
        {
            host.Show();
            var project = Path.Combine(output, "turn-fold.tex");
            await panel.SetProjectAsync(project);
            var models = (ComboBox)panel.Settings.FindName("Models");
            models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            await panel.Settings.SaveSelectionAsync(CancellationToken.None);
            var book = (AiNotebook)Field(panel, "_book")!;
            await CheckTurnFoldRangesAsync(host, panel, book, output, Check);
            book.Turns.Clear();
            var partial = new AiTurn("中断的回答", "留下的正文") { Collapsed = true, Incomplete = true };
            book.Turns.Add(new("第一轮", "正文")); book.Turns.Add(partial);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var second = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == partial.Id);
            CheckSingleFoldGestures(host, second, partial, Check);
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Turns.Single(t => t.Id == partial.Id).Collapsed, "Saved fold state was lost");
            book.Turns.Clear(); Invoke(panel, "RefreshTranscript");
            ((TextBox)panel.FindName("Composer")).Text = "验证实时折叠";
            var send = panel.SubmitAsync();
            await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); host.UpdateLayout();
            await CheckLiveFoldAsync(host, panel, (AiReplyStatus)panel.FindName("LiveReplyStatus"), Check);
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"PASS: {checks} WPF turn-fold checks (single, merged ranges, navigation, reading position, keyboard, persistence and live replies).");
        }
        finally { host.Close(); }
    }

    private static async Task CheckTurnFoldRangesAsync(Window host, AiPanel panel, AiNotebook book, string output, Action<bool, string> Check)
    {
        book.Turns.Clear();
        for (var n = 1; n <= 120; n++) book.Turns.Add(new("合成轮次 " + n, "合成回答") { SentAt = DateTimeOffset.UnixEpoch, AnsweredAt = DateTimeOffset.UnixEpoch.AddSeconds(1), Collapsed = true });
        Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll"); scroll.ScrollToEnd(); host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var rangeView = Descendants<AiTurnView>(panel).Single(v => v.IsVisible);
        var marker = Descendants<TextBlock>(rangeView).Single(t => t.IsVisible && t.Text == "[1 ~ 120]");
        var markerBounds = VisibleBounds(marker, panel);
        Check(markerBounds.Width >= marker.ActualWidth - .1 && markerBounds.Height >= marker.ActualHeight - .1, "The three-digit hanging turn range is clipped");
        Console.WriteLine($"Folded 120 turns: one visible row, marker width {marker.ActualWidth:F2}, visible {markerBounds.Width:F2}");
        Capture(host, Path.Combine(output, "ai-compact-turn-120.png"));
        Check(await panel.NavigateSameConversationTurnAsync(book.Turns[2].Id), "Unable to reach the third turn within a collapsed range");
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        string[] Ranges() => Descendants<AiTurnView>(panel).Where(v => v.IsVisible && v.Turn.Collapsed)
            .Select(v => Descendants<TextBlock>(v).Single(t => t.IsVisible).Text).ToArray();
        Check(Ranges().SequenceEqual(new[] { "[1, 2]", "[4 ~ 120]" }) && !book.Turns[2].Collapsed, "Expanding just the third turn did not split the interval: " + string.Join(" · ", Ranges()));
        var selectedRange = Descendants<AiTurnView>(panel).Single(v => v.Turn == book.Turns[0]);
        selectedRange.FoldTarget.RaiseEvent(CreateKey(host, System.Windows.Input.Key.Space, System.Windows.Input.Keyboard.KeyDownEvent)); host.UpdateLayout();
        Check(book.Turns.Take(3).All(t => !t.Collapsed) && book.Turns.Skip(3).All(t => t.Collapsed), "Opening a range changed turns outside it");
        Descendants<AiTurnView>(panel).Single(v => v.Turn == book.Turns[2]).Toggle(); host.UpdateLayout();
        Check(Ranges().SequenceEqual(new[] { "[3 ~ 120]" }), "Closing the third turn did not coalesce it with its neighbor");
        book.Turns.Clear();
        for (var n = 1; n <= 12; n++) book.Turns.Add(new($"阅读第 {n} 轮", "第一段。\n\n第二段。") { Collapsed = n == 4 });
        Invoke(panel, "RefreshTranscript");
        Check(await panel.NavigateSameConversationTurnAsync(book.Turns[4].Id), "Could not navigate to the fold viewport fixture");
        var fifth = Descendants<AiTurnView>(panel).Single(v => v.Turn == book.Turns[4]);
        var time = Descendants<AiReplyStatus>(fifth).Single();
        double Y(FrameworkElement element) => element.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Y(time) - 130);
        host.UpdateLayout();
        var clickY = Y(time);
        var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(click, 2);
        time.RaiseEvent(click);
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout();
        var merged = Descendants<AiTurnView>(panel).Single(v => v.Turn == book.Turns[3]);
        Check(book.Turns[4].Collapsed && Math.Abs(Y(merged.FoldTarget) - clickY) < 2,
            $"Folding at {clickY:F1} moved the merged line to {Y(merged.FoldTarget):F1} DIP");
    }

    private static void CheckSingleFoldShape(AiTurnView second, Action<bool, string> Check)
    {
        var texts = Descendants<TextBlock>(second).Where(t => t.IsVisible).ToArray();
        var rules = Descendants<Border>(second).Where(b => b.IsVisible && b.Height == 1).ToArray();
        Check(texts.Length == 1 && texts[0].Text == "[2]" && rules.Length == 1 && rules[0].ActualWidth > 100,
            "A folded turn must render only its ordinal and one rule");
        Check(!Descendants<MarkdownView>(second).Any(v => v.IsVisible) && !Descendants<AiReplyStatus>(second).Any(v => v.IsVisible),
            "A folded turn still shows content or timestamps");
    }

    private static void CheckSingleFoldGestures(Window host, AiTurnView second, AiTurn partial, Action<bool, string> Check)
    {
        CheckSingleFoldShape(second, Check);
        var folded = second.FoldTarget;
        var clickPoint = folded.TranslatePoint(new Point(folded.ActualWidth * .6, folded.ActualHeight / 2), host);
        var hit = host.InputHitTest(clickPoint) as UIElement;
        Check(hit is not null, "Folded rule is not hit-testable");
        var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2); hit!.RaiseEvent(doubleClick); host.UpdateLayout();
        Check(!partial.Collapsed, "Double-click on the rule did not expand the turn");
        var footer = Descendants<AiReplyStatus>(second).Single();
        Check(footer.Text.EndsWith("程序中止") && footer.IsVisible, "Expanding lost the end status");
        footer.RaiseEvent(CreateKey(host, Key.Space, Keyboard.KeyDownEvent)); host.UpdateLayout();
        CheckSingleFoldShape(second, Check);
        second.FoldTarget.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.KeyDownEvent)); host.UpdateLayout();
        Check(!partial.Collapsed && footer.IsVisible, "Keyboard expansion did not restore the footer");
        doubleClick.Handled = false; footer.RaiseEvent(doubleClick); host.UpdateLayout();
        Check(partial.Collapsed, "Double-click did not fold the answer");
    }

    private static async Task CheckLiveFoldAsync(Window host, AiPanel panel, AiReplyStatus live, Action<bool, string> Check)
    {
        var liveStamp = (TextBlock)panel.FindName("LiveSentTime");
        var liveClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(liveClick, 2); liveStamp.RaiseEvent(liveClick);
        host.UpdateLayout();
        var foldedLive = Descendants<AiTurnView>(panel).Single(v => v.IsVisible);
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(!liveStamp.IsVisible && !live.IsVisible && Descendants<TextBlock>(foldedLive).Single(t => t.IsVisible).Text == "[1]", "Folded live turn must show only its compact row");
        foldedLive.FoldTarget.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.KeyDownEvent)); host.UpdateLayout();
        Check(liveStamp.IsVisible && live.IsVisible, "The live folded rule cannot restore the running turn");
    }
}
