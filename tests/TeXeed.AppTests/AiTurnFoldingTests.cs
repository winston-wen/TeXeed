using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

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
            await panel.SaveAsync();
            await panel.SetProjectAsync(project);
            book = (AiNotebook)Field(panel, "_book")!;
            ((ScrollViewer)panel.FindName("MessagesScroll")).ScrollToTop();
            await SettleFoldLayoutAsync(host);
            Check(book.Turns.Skip(3).Take(4).Select(t => t.FoldGroup).Distinct().SingleOrDefault() is { Length: > 0 } &&
                book.Turns.Skip(3).Take(4).All(t => t.Collapsed) &&
                Descendants<AiTurnView>(panel).Any(view => view.Turn.Id == book.Turns[3].Id && FoldOrdinal(view) == "[4 ~ 7]"),
                "Reopening the conversation did not restore its explicitly compacted run");
            Check(book.Turns[8] is { Collapsed: true, FoldGroup: null } && !book.Turns[7].Collapsed,
                "Reopening lost the individual collapsed turn or its expanded neighbour");
            var theme = ThemeManager.Current.Id;
            ThemeManager.Apply("github-light-default");
            host.Width = 420;
            await SettleFoldLayoutAsync(host);
            var reopened = Descendants<AiTurnView>(panel).Single(view => view.Turn.Id == book.Turns[3].Id);
            var narrowHint = FoldHint(reopened, "展开");
            var visibleRules = Descendants<Border>(reopened.FoldTarget).Where(b => b.Height == 1 && b.ActualWidth > 1)
                .Select(rule => VisibleBounds(rule, host)).Where(rect => rect.Width > 0).ToArray();
            var apparentCenter = (visibleRules.Min(rect => rect.Left) + visibleRules.Max(rect => rect.Right)) / 2;
            var narrowHintX = narrowHint.TranslatePoint(new Point(narrowHint.ActualWidth / 2, 0), host).X;
            Check(Math.Abs(narrowHintX - apparentCenter) < 2,
                $"The compact hint is centered at {narrowHintX:F1} DIP against the visible divider centre at {apparentCenter:F1} DIP");
            Capture(host, Path.Combine(output, "ai-fold-light-narrow.png"));
            ThemeManager.Apply(theme);
            host.Width = 620;
            book.Turns.Clear();
            var partial = new AiTurn("中断的回答", "留下的正文") { Collapsed = true, Incomplete = true };
            book.Turns.Add(new("第一轮", "正文")); book.Turns.Add(partial);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var second = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == partial.Id);
            CheckSingleFoldGestures(host, second, partial, Check);
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Turns.Single(t => t.Id == partial.Id).Collapsed, "Saved fold state was lost");
            book.Turns.Clear();
            book.Turns.Add(new("已完成的回答", "正文") { Collapsed = true });
            Invoke(panel, "RefreshTranscript");
            ((TextBox)panel.FindName("Composer")).Text = "验证实时折叠";
            var send = panel.SubmitAsync();
            await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleFoldLayoutAsync(host);
            var liveStamp = (TextBlock)panel.FindName("LiveSentTime");
            RaiseFoldClick(liveStamp, 2);
            await SettleFoldLayoutAsync(host);
            var liveView = Descendants<AiTurnView>(panel).Single(view => view.IsVisible && view.Turn.Id != book.Turns[0].Id);
            Check(FoldOrdinal(liveView) == "[2]" && !liveStamp.IsVisible && book.Turns[0].FoldGroup is null,
                "The running round should fold individually beside a finished collapsed turn");
            var liveHint = FoldHint(liveView, "收纳");
            ClickFoldHint(host, liveHint, 2);
            await SettleFoldLayoutAsync(host);
            Check(!liveStamp.IsVisible && Descendants<AiTurnView>(panel).Any(view => view.IsVisible && FoldOrdinal(view) == "[1, 2]"),
                "A live turn could not be explicitly compacted with its preceding collapsed neighbour");
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleFoldLayoutAsync(host);
            Check(book.Turns.Count == 2 && book.Turns[1].Collapsed && book.Turns[1].FoldGroup == book.Turns[0].FoldGroup &&
                Descendants<AiTurnView>(panel).Any(view => view.IsVisible && FoldOrdinal(view) == "[1, 2]"),
                "Completing or stopping a grouped live round lost its fold group");
            Console.WriteLine($"PASS: {checks} WPF turn-fold checks (individual, compact, hints, navigation, reading position, keyboard, persistence and live replies).");
        }
        finally { host.Close(); }
    }

    private static async Task CheckTurnFoldRangesAsync(Window host, AiPanel panel, AiNotebook book, string output, Action<bool, string> Check)
    {
        book.Turns.Clear();
        for (var n = 1; n <= 120; n++) book.Turns.Add(new("合成轮次 " + n, "合成回答") { SentAt = DateTimeOffset.UnixEpoch, AnsweredAt = DateTimeOffset.UnixEpoch.AddSeconds(1), Collapsed = true });
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        Invoke(panel, "RefreshTranscript"); scroll.ScrollToTop();
        await SettleFoldLayoutAsync(host);
        AiTurnView View(int n) => Descendants<AiTurnView>(panel).Single(v => v.IsVisible && v.Turn.Id == book.Turns[n - 1].Id);
        Check(FoldOrdinal(View(1)) == "[1]" && FoldOrdinal(View(2)) == "[2]" && View(2).FoldGroupHint?.IsVisible != true,
            "Consecutive old saved folds must be individual rows, with hints only on the endpoints");
        var firstHint = FoldHint(View(1), "收纳");
        var beforeSingle = scroll.VerticalOffset;
        ClickFoldHint(host, firstHint, 1);
        await SettleFoldLayoutAsync(host);
        Check(FoldOrdinal(View(1)) == "[1]" && book.Turns.All(turn => turn.Collapsed && turn.FoldGroup is null) &&
            Math.Abs(scroll.VerticalOffset - beforeSingle) < .1,
            "Single-clicking the group hint must not expand or compact anything");
        ClickFoldHint(host, firstHint, 2);
        await SettleFoldLayoutAsync(host);
        var rangeView = Descendants<AiTurnView>(panel).Single(v => v.IsVisible);
        var marker = Descendants<TextBlock>(rangeView.FoldTarget).Single(t => t.IsVisible && t.Text == "[1 ~ 120]");
        var markerBounds = VisibleBounds(marker, panel);
        Check(markerBounds.Width >= marker.ActualWidth - .1 && markerBounds.Height >= marker.ActualHeight - .1, "The three-digit hanging turn range is clipped");
        Check(book.Turns.All(turn => turn.Collapsed && turn.FoldGroup == book.Turns[0].FoldGroup) && book.Turns[0].FoldGroup is { Length: > 0 },
            "Compacting must group the whole run without opening any turn");
        Console.WriteLine($"Compacted 120 turns: one visible row, marker width {marker.ActualWidth:F2}, visible {markerBounds.Width:F2}");
        Capture(host, Path.Combine(output, "ai-compact-turn-120.png"));
        ClickFoldHint(host, FoldHint(rangeView, "展开"), 2);
        await SettleFoldLayoutAsync(host);
        Check(FoldOrdinal(View(1)) == "[1]" && FoldOrdinal(View(3)) == "[3]" && book.Turns.All(turn => turn.Collapsed && turn.FoldGroup is null),
            "Expanding the compact line must restore the individual collapsed rows");
        ClickFoldHint(host, FoldHint(View(1), "收纳"), 2);
        await SettleFoldLayoutAsync(host);
        Check(await panel.NavigateSameConversationTurnAsync(book.Turns[2].Id), "Unable to reach the third turn within a collapsed range");
        await SettleFoldLayoutAsync(host);
        Check(!book.Turns[2].Collapsed && book.Turns.Where((_, i) => i != 2).All(t => t.Collapsed) &&
            book.Turns.All(t => t.FoldGroup is null) && FoldOrdinal(View(4)) == "[4]",
            "Navigating into a compact group should only open the target and restore neighbouring individual rows");

        book.Turns.Clear();
        for (var n = 1; n <= 12; n++)
            book.Turns.Add(new($"阅读第 {n} 轮", "第一段。\n\n第二段。") { Collapsed = n is 4 or 6 or 7 or 9 });
        Invoke(panel, "RefreshTranscript");
        Check(await panel.NavigateSameConversationTurnAsync(book.Turns[4].Id), "Could not navigate to the fold viewport fixture");
        var time = Descendants<AiReplyStatus>(View(5)).Single();
        double Y(FrameworkElement element) => element.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + Y(time) - 130);
        await SettleFoldLayoutAsync(host);
        var clickY = Y(time);
        RaiseFoldClick(time, 2);
        await SettleFoldLayoutAsync(host);
        Check(book.Turns[4].Collapsed && Math.Abs(Y(View(5).FoldTarget) - clickY) < 2,
            $"Folding at {clickY:F1} moved the individual line to {Y(View(5).FoldTarget):F1} DIP");
        Check(Enumerable.Range(4, 4).All(n => FoldOrdinal(View(n)) == $"[{n}]") && View(5).FoldGroupHint?.IsVisible != true && View(6).FoldGroupHint?.IsVisible != true,
            "Folding an intervening turn caused automatic grouping or put a group hint on an interior divider");
        double FoldPitch(int n) => Y(View(n + 1).FoldTarget) - Y(View(n).FoldTarget);
        Check(Enumerable.Range(4, 3).All(n => Math.Abs(FoldPitch(n) - 28.125) < 2),
            "Consecutive individual collapsed rows do not follow the reading line height");
        var afterRun = Y(View(8)) - Y(View(7).FoldTarget) - View(7).FoldTarget.ActualHeight;
        Check(Math.Abs(afterRun - 30) < 2, $"The divider run and the next expanded turn have a {afterRun:F1} DIP gap");
        FoldHint(View(4), "收纳");
        FoldHint(View(7), "收纳");
        Capture(host, Path.Combine(output, "ai-individual-folds.png"));
        View(5).FoldTarget.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.KeyDownEvent));
        await SettleFoldLayoutAsync(host);
        Check(!View(5).Turn.Collapsed && book.Turns[3].Collapsed && book.Turns[5].Collapsed && book.Turns[6].Collapsed,
            "Peeking an individual folded row also opened its neighbours");
        Check(View(4).FoldGroupHint?.IsVisible != true && FoldHint(View(6), "收纳").IsVisible && FoldHint(View(7), "收纳").IsVisible,
            "Peeking did not recompute the endpoints on either side of the expanded turn");
        RaiseFoldClick(Descendants<AiReplyStatus>(View(5)).Single(), 2);
        await SettleFoldLayoutAsync(host);
        var tailHint = FoldHint(View(7), "收纳");
        var tailY = Y(tailHint) + tailHint.ActualHeight / 2;
        ClickFoldHint(host, tailHint, 2);
        await SettleFoldLayoutAsync(host);
        var packedHint = FoldHint(View(4), "展开");
        Check(FoldOrdinal(View(4)) == "[4 ~ 7]" && Math.Abs(Y(packedHint) + packedHint.ActualHeight / 2 - tailY) < 2,
            $"Compacting from the last divider at {tailY:F1} moved the compact hint to {Y(packedHint) + packedHint.ActualHeight / 2:F1} DIP");
        var packedY = Y(packedHint) + packedHint.ActualHeight / 2;
        ClickFoldHint(host, packedHint, 2);
        await SettleFoldLayoutAsync(host);
        var restoredHint = FoldHint(View(4), "收纳");
        Check(Enumerable.Range(4, 4).All(n => FoldOrdinal(View(n)) == $"[{n}]") && Math.Abs(Y(restoredHint) + restoredHint.ActualHeight / 2 - packedY) < 2,
            "Restoring the individual dividers moved the first hint away from the compact divider");
        ClickFoldHint(host, restoredHint, 2);
        await SettleFoldLayoutAsync(host);
    }

    private static async Task SettleFoldLayoutAsync(Window host)
    {
        host.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
    }

    private static string FoldOrdinal(AiTurnView view) =>
        Descendants<TextBlock>(view.FoldTarget).SingleOrDefault(t => t.IsVisible && t.Text.StartsWith('['))?.Text ?? "";

    private static FrameworkElement FoldHint(AiTurnView view, string label)
    {
        var hint = view.FoldGroupHint;
        var name = (hint as ContentControl)?.Content?.ToString();
        if (hint is not { IsVisible: true } ||
            name?.Contains(label, StringComparison.Ordinal) != true &&
            !Descendants<TextBlock>(hint).Any(t => t.IsVisible && t.Text.Contains(label, StringComparison.Ordinal)))
            throw new Exception($"Fold divider {FoldOrdinal(view)} does not show the {label} hint");
        var center = hint.TranslatePoint(new Point(hint.ActualWidth / 2, hint.ActualHeight / 2), view.FoldTarget);
        var rule = Descendants<Border>(view.FoldTarget).First(b => b.Height == 1 && b.ActualWidth > 1);
        var left = rule.TranslatePoint(new Point(), view.FoldTarget).X;
        var right = Descendants<Border>(view.FoldTarget).Where(b => b.Height == 1 && b.ActualWidth > 1)
            .Max(b => b.TranslatePoint(new Point(b.ActualWidth, 0), view.FoldTarget).X);
        var lineY = rule.TranslatePoint(new Point(0, rule.ActualHeight / 2), view.FoldTarget).Y;
        if (Math.Abs(center.Y - lineY) > 2 || Math.Abs(center.X - (left + right) / 2) > 2)
            throw new Exception($"Fold hint {label} is not centered on its divider: {center.X:F1} vs {(left + right) / 2:F1} DIP");
        return hint;
    }

    private static void ClickFoldHint(Window host, FrameworkElement hint, int clicks)
    {
        var point = hint.TranslatePoint(new Point(hint.ActualWidth / 2, hint.ActualHeight / 2), host);
        if (host.InputHitTest(point) is not UIElement hit || !ReferenceEquals(hit, hint) && !hint.IsAncestorOf(hit))
            throw new Exception("The divider hint is not the hit target at its visible centre");
        RaiseFoldClick(hit, clicks);
    }

    private static void RaiseFoldClick(UIElement target, int clicks)
    {
        var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(mouse, clicks);
        target.RaiseEvent(mouse);
        if (mouse.Handled) return;
        mouse.RoutedEvent = Mouse.MouseDownEvent;
        target.RaiseEvent(mouse);
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
