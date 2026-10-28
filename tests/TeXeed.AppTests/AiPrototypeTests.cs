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
        Check(completed.AnswerTime == "2026-10-19 (+8) 10:00:54 答复", "Completed turn must show an absolute timestamp first");
        Check(partial.AnswerTime == "2026-10-19 (+8) 10:00:30 程序中止", "Interrupted partial answer lost its timestamp or status");
        Check(new AiTurn("旧记录", "[本轮未完成]\n部分文字").AnswerTime == "未记录 程序中止", "Crash recovery invented an end time");
        Check(new AiTurn("旧记录", "已完成的旧正文").AnswerTime == "未记录 答复", "Legacy success was marked incomplete or given a fabricated answer time");
        var legacyFailure = new AiTurn("旧中断", "已有部分正文") { Segments = [new() { Id = "failure", Kind = "activity", Text = "已停止。", CreatedAt = sent.AddSeconds(4) }] };
        Check(legacyFailure.AnswerTime == "2026-10-19 (+8) 10:00:04 用户中止", "Legacy user stop was missed");
        Check((legacyFailure with { EndReason = "program" }).AnswerTime.EndsWith("程序中止"), "Explicit end reason did not override legacy text");

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
                var timestamp = Descendants<TextBlock>(view).Single(t => t.IsVisible && t.Text.StartsWith(view.Turn.DisplayTime + " 发送"));
                var reply = Descendants<AiReplyStatus>(view).Single();
                Check(Math.Abs(timestamp.TranslatePoint(new Point(), view).X - reply.TranslatePoint(new Point(), view).X) < .1, "Sent and answer timestamps must align");
                Check(Descendants<Button>(view).All(b => !Equals(b.Content, "（已折叠，点击展开）") && b.ToolTip is not ("将问题重新放入编辑栏" or "删除此轮问题和回答")), "Removed hint or footer icons remain");
                var stamp = Descendants<TextBlock>(reply).Single(t => t.FontSize == 13 && t.Text.Contains(view.Turn.AnswerTime)); var line = Descendants<Border>(reply).Single(b => !b.IsHitTestVisible);
                Check(line.TranslatePoint(new Point(), reply).Y >= stamp.ActualHeight, "Timestamp intersects the separator");
            }
            Check(Descendants<TextBlock>(second).Any(t => t.IsVisible && t.Text == partial.SentTime + "\n" + partial.AnswerTime), "Collapsed turn must share one text block for both timestamps");
            var footer = Descendants<AiReplyStatus>(second).Single();
            void CheckCollapsedSpacing()
            {
                var block = Descendants<TextBlock>(footer).Single(t => t.Text.Contains('\n'));
                Check(block.Text == partial.SentTime + "\n" + partial.AnswerTime, "Collapsed timestamps are not in the same text block");
                Check(double.IsNaN(block.LineHeight) && block.Margin == new Thickness(0) && block.Padding == new Thickness(0), "Collapsed timestamps override natural text line spacing");
                var reference = new TextBlock { Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, TextWrapping = block.TextWrapping };
                reference.Measure(new Size(block.ActualWidth, double.PositiveInfinity));
                Check(Math.Abs(block.ActualHeight - reference.DesiredSize.Height) < .1, "Collapsed timestamp height differs from default block text");
            }
            CheckCollapsedSpacing();
            foreach (var offset in new[] { 0, partial.SentTime.Length + 1 })
            {
                var block = Descendants<TextBlock>(footer).Single(t => t.Text.Contains('\n'));
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
            Check(footer.Text.EndsWith("程序中止"), "Expanding lost the end status");
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2);
            footer.RaiseEvent(doubleClick); host.UpdateLayout();
            Check(partial.Collapsed && partial.SentTime.EndsWith("（已折叠）"), "Double-click did not fold the answer");
            Check(footer.IsVisible && footer.Text.EndsWith("程序中止"), "Folding hid the answer time or end status");
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
            Check(((TextBlock)panel.FindName("LiveSentTime")).Text.EndsWith("发送"), "Live send time does not place the timestamp first");
            Check(((TextBlock)panel.FindName("LiveTurnNumber")).Text == "[1]", "Live turn number does not follow the conversation");
            var liveStamp = (TextBlock)panel.FindName("LiveSentTime");
            var liveClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(liveClick, 2); liveStamp.RaiseEvent(liveClick);
            Check(((ItemsControl)panel.FindName("LiveSegments")).Visibility == Visibility.Collapsed && liveStamp.Text.EndsWith("（已折叠）"), "Live send time cannot fold the running turn");
            liveStamp.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(liveStamp), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(((ItemsControl)panel.FindName("LiveSegments")).Visibility == Visibility.Visible, "Live send time cannot restore the running turn");
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
            Check(stopped.EndReason == "user" && stopped.AnswerTime.EndsWith("用户中止") && !stopped.AnswerTime.Contains("未记录"), "Stopped reply lost its end timestamp or reason");
            Check(!indicator.HasAnimatedProperties && !live.IsVisible, "Settled reply retained the thinking pulse");
            Capture(host, Path.Combine(output, "ai-prototype-stopped.png"));
            var persisted = AiStorage.Load(project).Turns.Single();
            Check(persisted.Incomplete == true && persisted.EndedAt == stopped.EndedAt && persisted.EndReason == "user", "Stopped status was not durable");
        }
        finally { ThemeManager.Apply(theme); host.Close(); }
        using (var failedConnection = new AiConnection(new AiFakeHandler { ProgramFailure = true }))
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
                Check(failedTurn.EndReason == "program" && Descendants<AiReplyStatus>(failedPanel).Any(s => s.IsVisible && s.Text.EndsWith("程序中止")), "Partial failed reply has no program-stop footer");
                Check(AiStorage.Load(failedProject).Turns.Single().AnswerTime == failedTurn.AnswerTime, "Partial failure timestamp/marker changed after loading");
                Capture(failedHost, Path.Combine(output, "ai-prototype-partial-failure.png"));
            }
            finally { failedHost.Close(); }
        }
        Console.WriteLine($"AI prototype parity: {checks} checks passed (timestamp-first metadata, straight separator, folded/end states, legacy data, themes, live animation and stop persistence).");
    }
}
