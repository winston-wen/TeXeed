using System.IO;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckAiPrototypeAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var sent = DateTimeOffset.Parse("2026-10-19T10:00:00+08:00");
        var completed = new AiTurn("完成的回答", "## 正文\n\n这是已完成的答复。") { SentAt = sent, AnsweredAt = sent.AddSeconds(54), Incomplete = false };
        var partial = new AiTurn("中断的回答", "保留收到的部分文字") { SentAt = sent, EndedAt = sent.AddSeconds(30), Incomplete = true, Collapsed = true };
        Check(completed.AnswerTime == "答复时间：2026-10-19 (+8) 10:00:54", "Completed turn must show an absolute timestamp");
        Check(partial.AnswerTime == "答复时间：2026-10-19 (+8) 10:00:30（未完成）", "Interrupted partial answer lost its timestamp or marker");
        Check(new AiTurn("旧记录", "[本轮未完成]\n部分文字").AnswerTime == "答复时间：未记录（未完成）", "Crash recovery invented an end time");
        Check(new AiTurn("旧记录", "已完成的旧正文").AnswerTime == "答复时间：未记录", "Legacy success was marked incomplete or given a fabricated answer time");
        var legacyFailure = new AiTurn("旧中断", "已有部分正文") { Segments = [new() { Id = "failure", Kind = "activity", Text = "已停止。", CreatedAt = sent.AddSeconds(4) }] };
        Check(legacyFailure.AnswerTime == "答复时间：2026-10-19 (+8) 10:00:04（未完成）", "Legacy partial answer failure was missed");

        new AiCredentials { ApiKey = "fixture-never-send", UseApiKey = true }.Save();
        using var stalled = new StalledAiHandler(); using var connection = new AiConnection(stalled); using var panel = new AiPanel(connection);
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        await panel.Settings.SaveSelectionAsync(CancellationToken.None);
        var host = new Window { Owner = owner, Width = 820, Height = 900, Content = panel, Title = "AI 原型 v0.4.13 · 原生验证" };
        var theme = ThemeManager.Current.Id;
        try
        {
            host.Show();
            var project = Path.Combine(output, "prototype.tex"); await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            book.Turns.Clear(); book.Turns.Add(completed); book.Turns.Add(partial); book.Active.HeaderPinned = true;
            Invoke(panel, "RefreshTranscript"); await Task.Delay(150); host.UpdateLayout();
            await AwaitMathAsync(panel);
            var views = Descendants<AiTurnView>(panel).ToArray();
            Check(views.Length == 2, "Fixture turns did not render");
            var first = views.Single(v => v.Turn.Id == completed.Id); var second = views.Single(v => v.Turn.Id == partial.Id);
            foreach (var view in views)
            {
                var timestamp = Descendants<TextBlock>(view).Single(t => t.IsVisible && t.Text.StartsWith("发送时间："));
                var reply = Descendants<AiReplyStatus>(view).Single();
                Check(Math.Abs(timestamp.TranslatePoint(new Point(), view).X - reply.TranslatePoint(new Point(), view).X) < .1, "Sent and answer timestamps must align");
                Check(Descendants<Button>(view).All(b => !Equals(b.Content, "（已折叠，点击展开）") && b.ToolTip is not ("将问题重新放入编辑栏" or "删除此轮问题和回答")), "Removed hint or footer icons remain");
                var stamp = Descendants<TextBlock>(reply).Single(); var line = Descendants<Line>(reply).Single();
                Check(line.TranslatePoint(new Point(), reply).Y >= stamp.ActualHeight, "Timestamp still intersects the dotted separator");
            }
            Check(Descendants<TextBlock>(second).Any(t => t.IsVisible && t.Text.Contains("（已折叠）\n答复时间：")), "Collapsed turn must share one text block for both timestamps");
            var footer = Descendants<AiReplyStatus>(second).Single();
            void CheckCollapsedSpacing()
            {
                var block = Descendants<TextBlock>(footer).Single();
                Check(block.Text == partial.SentTime + "\n" + partial.AnswerTime, "Collapsed timestamps are not in the same text block");
                Check(double.IsNaN(block.LineHeight) && block.Margin == new Thickness(0) && block.Padding == new Thickness(0), "Collapsed timestamps override natural text line spacing");
                var reference = new TextBlock { Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, TextWrapping = block.TextWrapping };
                reference.Measure(new Size(block.ActualWidth, double.PositiveInfinity));
                Check(Math.Abs(block.ActualHeight - reference.DesiredSize.Height) < .1, "Collapsed timestamp height differs from default block text");
            }
            CheckCollapsedSpacing();
            foreach (var offset in new[] { 0, partial.SentTime.Length + 1 })
            {
                var block = Descendants<TextBlock>(footer).Single();
                var position = block.Inlines.OfType<Run>().Single().ContentStart.GetPositionAtOffset(offset)!;
                var rect = position.GetCharacterRect(LogicalDirection.Forward);
                var point = block.TranslatePoint(new Point(rect.Left + 2, rect.Top + rect.Height / 2), host);
                var target = host.InputHitTest(point);
                Check(target is UIElement or ContentElement, "Timestamp line cannot be hit");
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
                typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(click, 2);
                if (target is UIElement element) element.RaiseEvent(click); else ((ContentElement)target).RaiseEvent(click);
                host.UpdateLayout();
                Check(!partial.Collapsed, "Double-clicking either timestamp line must expand the turn");
                footer.RaiseEvent(CreateKey(host, Key.Space, Keyboard.KeyDownEvent)); host.UpdateLayout();
                Check(partial.Collapsed, "Keyboard fold did not restore the compact block");
            }
            footer.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.KeyDownEvent)); host.UpdateLayout();
            Check(!partial.Collapsed && !partial.SentTime.Contains("已折叠") && !partial.SentTime.Contains("未折叠"), "Expanding must remove the marker without an unfolded label");
            Check(footer.Text.EndsWith("（未完成）"), "Expanding lost the incomplete marker");
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2);
            footer.RaiseEvent(doubleClick); host.UpdateLayout();
            Check(partial.Collapsed && partial.SentTime.EndsWith("（已折叠）"), "Double-click did not fold the answer");
            Check(footer.IsVisible && footer.Text.EndsWith("（未完成）"), "Folding hid the answer time or incomplete marker");
            await panel.SaveAsync(); var saved = AiStorage.Load(project).Turns.Single(t => t.Id == partial.Id);
            Check(saved.Collapsed && saved.Incomplete == true && saved.EndedAt == partial.EndedAt, "New status fields or folds did not persist");

            foreach (var colors in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(colors); host.Width = 820; host.UpdateLayout();
                Check(panel.FindName("ChatHeader") is null && panel.FindName("HeadingPeek") is null,"Removed header remains in conversation body");
                Capture(host, Path.Combine(output, "ai-prototype-" + colors + ".png"));
                host.Width = 420; host.UpdateLayout();
                Check(panel.FindName("ChatHeader") is null,"Narrow layout resurrected the removed header");
                CheckCollapsedSpacing();
                Capture(host, Path.Combine(output, "ai-prototype-narrow-" + colors + ".png"));
            }
            ThemeManager.Apply(theme); host.Width = 820;
            book.Turns.Clear(); Invoke(panel, "RefreshTranscript");
            ((TextBox)panel.FindName("Composer")).Text = "验证思考中与停止状态";
            var send = panel.SubmitAsync(); await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); host.UpdateLayout();
            var live = (AiReplyStatus)panel.FindName("LiveReplyStatus");
            Check(live.IsVisible && live.IsThinking && !live.Focusable, "Live footer must display thinking without enabling folding");
            Check(((TextBlock)panel.FindName("LiveSentTime")).Text.StartsWith("发送时间："), "Live send time still uses a combined footer");
            var indicator = Descendants<Ellipse>(live).Single();
            Check(indicator.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "Thinking animation ignores the motion preference");
            var extent = ((ScrollViewer)panel.FindName("MessagesScroll")).ExtentHeight;
            await Task.Delay(150); host.UpdateLayout();
            Check(Math.Abs(((ScrollViewer)panel.FindName("MessagesScroll")).ExtentHeight - extent) < .1, "Thinking animation changes transcript layout");
            Capture(host, Path.Combine(output, "ai-prototype-thinking.png"));
            panel.Visibility = Visibility.Collapsed; await Task.Delay(40);
            Check(!indicator.HasAnimatedProperties, "Hidden thinking indicator retained its animation clock");
            panel.Visibility = Visibility.Visible; await Task.Delay(40);
            Check(indicator.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "Thinking animation did not resume");
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await send.WaitAsync(TimeSpan.FromSeconds(10)); host.UpdateLayout();
            var stopped = book.Turns.Single();
            Check(stopped.IsIncomplete && stopped.Incomplete == true && stopped.AnsweredAt is null && stopped.EndedAt is not null, "Stopping did not store distinct incomplete/end state");
            Check(stopped.AnswerTime.EndsWith("（未完成）") && !stopped.AnswerTime.Contains("未记录"), "Stopped reply lost its end timestamp");
            Check(!indicator.HasAnimatedProperties && !live.IsVisible, "Settled reply retained the thinking pulse");
            Capture(host, Path.Combine(output, "ai-prototype-stopped.png"));
            var persisted = AiStorage.Load(project).Turns.Single();
            Check(persisted.Incomplete == true && persisted.EndedAt == stopped.EndedAt, "Stopped status was not durable");
        }
        finally { ThemeManager.Apply(theme); host.Close(); }
        using (var failedConnection = new AiConnection(new AiFakeHandler { Broken = true }))
        using (var failedPanel = new AiPanel(failedConnection))
        {
            var failedModels = (ComboBox)failedPanel.Settings.FindName("Models"); failedModels.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; failedModels.SelectedIndex = 0;
            await failedPanel.Settings.SaveSelectionAsync(CancellationToken.None);
            var failedHost = new Window { Owner = owner, Content = failedPanel, Width = 820, Height = 900 };
            try
            {
                failedHost.Show();
                var failedProject = Path.Combine(output, "partial-failure.tex"); await failedPanel.SetProjectAsync(failedProject);
                ((TextBox)failedPanel.FindName("Composer")).Text = "保留断流前的部分答复";
                await failedPanel.SubmitAsync(); failedHost.UpdateLayout();
                var failedTurn = ((AiNotebook)Field(failedPanel, "_book")!).Turns.Single();
                Check(failedTurn.Answer.Contains("partial") && failedTurn.IsIncomplete && failedTurn.EndedAt is not null, "Stream failure discarded partial text or status");
                Check(Descendants<AiReplyStatus>(failedPanel).Any(s => s.IsVisible && s.Text.EndsWith("（未完成）")), "Partial failed reply has no incomplete footer");
                Check(AiStorage.Load(failedProject).Turns.Single().AnswerTime == failedTurn.AnswerTime, "Partial failure timestamp/marker changed after loading");
                Capture(failedHost, Path.Combine(output, "ai-prototype-partial-failure.png"));
            }
            finally { failedHost.Close(); }
        }
        Console.WriteLine($"AI prototype parity: {checks} checks passed (absolute timestamps, empty dotted rule, folded/incomplete states, legacy data, themes, live animation and stop persistence).");
    }
}
