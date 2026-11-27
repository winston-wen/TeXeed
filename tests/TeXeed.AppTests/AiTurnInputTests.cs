using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckTurnInputAsync(MainWindow owner, string output)
    {
        static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
        var project = Path.Combine(output, "blocking-input.tex");
        File.WriteAllText(project, @"\documentclass{article}\begin{document}Question.\end{document}");
        var book = new AiNotebook();
        book.Active.Permission = AiPermission.ReadOnly;
        AiStorage.Save(project, book);
        await CallAsync(owner, "OpenAsync", project);
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var server = new RevisionFixtureServer { Permission = AiPermission.ReadOnly };
        using var panel = new AiPanel(new AiConnection(server));
        panel.CreateToolRunner = () => owner.CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        var host = new Window { Owner = owner, Content = panel, Width = 900, Height = 740, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
        host.Show();
        try
        {
            AiNotebook Active() => (AiNotebook)Field(panel, "_book")!;
            var composer = (TextBox)panel.FindName("Composer");
            async Task Reload()
            {
                panel.Bind(await AiWorkspaceStore.LoadAsync(project), book.Active.Id);
                var models = (ComboBox)panel.Settings.FindName("Models");
                models.ItemsSource = new[] { new AiModel("fixture", "等待输入回归") }; models.SelectedIndex = 0;
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            async Task<AiTurnView> Show(string id)
            {
                Check(await panel.NavigateSameConversationTurnAsync(id), "Waiting turn is unreachable.");
                host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var view = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == id);
                await Task.WhenAll(view.Answers.Select(v => v.RenderTask));
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return view;
            }
            static Button ButtonOn(AiTurnView turn, string label) => Descendants<Button>(turn).Single(b => Equals(b.Content, label) && b.IsVisible);
            static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Script(params Func<string[], JsonObject>[] steps) { server.Failure = null; server.Steps = new(steps); }
            void Done()
            {
                if (server.Failure is { } error) throw new InvalidOperationException("Fixture response failed", error);
                Check(server.Steps.Count == 0, "Expected model request missing.");
            }
            async Task Idle()
            {
                await WaitUntilAsync(() => Field(panel, "_operation") is null && server.Steps.Count == 0 || server.Failure is not null, "Waiting action did not settle.");
                Done(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            JsonObject Ask(string[] _) => server.Call("await_user_input", new { });
            await Reload();
            server.ObserveRequest = payload => Check(payload["tools"]!.ToJsonString().Contains("await_user_input"), "Missing general wait tool in read-only mode.");
            Script(Ask, _ => server.Say("这会决定下一步的讲解。要先看什么？\n\n{{radio:path:derive:看推导}}\n\n{{radio:path:example:看例子}}"));
            composer.Text = "请按我的选择继续讲。";
            await panel.SubmitAsync(); Done();
            var first = AiStorage.Load(project).Turns.Single();
            Check(first.IsWaitingForInput && !first.IsIncomplete && first.AnsweredAt is null, "A blocking question was marked finished or interrupted.");
            var id = first.Id;
            var shown = await Show(id);
            var confirm = ButtonOn(shown, "确认并继续");
            Check(!confirm.IsEnabled && Descendants<AiReplyStatus>(shown).Single().Text == "等待你确认答案",
                "An unanswered choice could resume reasoning or was displayed as answered.");
            Descendants<RadioButton>(shown).Single(b => Equals(b.Content, "看例子")).IsChecked = true;
            Check(confirm.IsEnabled && server.Steps.Count == 0 && Active().Turns.Single().IsWaitingForInput, "Selection submitted too soon or failed to enable confirmation.");
            composer.Text = "UNRELATED-COMPOSER-DRAFT";
            await panel.SaveAsync(); await Reload();
            shown = await Show(id);
            Check(ButtonOn(shown, "确认并继续").IsEnabled && Descendants<RadioButton>(shown).Single(b => Equals(b.Content, "看例子")).IsChecked == true, "Reload lost the unconfirmed selection.");
            Capture(host, Path.Combine(output, "waiting-input.png"));
            static string RequestText(JsonNode? node) => node switch
            {
                JsonArray items => string.Join("\n", items.Select(RequestText)),
                JsonObject fields => string.Join("\n", fields.Select(pair => RequestText(pair.Value))),
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                _ => ""
            };
            const string firstChoice = "\"radio:path\":\"example\"";
            server.ObserveRequest = payload => Check(!RequestText(payload["input"]).Contains(firstChoice), "Sending an unrelated question consumed the pending choice.");
            Script(_ => server.Say("普通练习。\n\n{{text:practice}}"));
            composer.Text = "顺便另问一个问题"; await panel.SubmitAsync(); Done();
            var extra = Active().Turns.Last();
            Check(extra.Id != id && !extra.IsWaitingForInput && Active().Turns[0].IsWaitingForInput, "An ordinary exercise started or consumed a wait.");
            var ordinary = await Show(extra.Id);
            Check(!Descendants<Button>(ordinary).Any(b => Equals(b.Content, "确认并继续") && b.IsVisible), "Ordinary exercise got a confirm button.");
            composer.Text = "UNRELATED-COMPOSER-DRAFT";
            shown = await Show(id);
            confirm = ButtonOn(shown, "确认并继续");
            var received = new List<string>();
            server.ObserveRequest = payload => received.Add(RequestText(payload["input"]));
            Script(Ask, _ => server.Say("这次要展开哪一种？\n\n{{radio:path:brief:简短}}\n\n{{radio:path:full:完整}}"));
            Click(confirm); Click(confirm);
            await Idle();
            var middle = AiStorage.Load(project);
            Check(middle.Turns.Count == 2 && middle.Turns[0].Id == id && middle.Turns[0].IsWaitingForInput &&
                middle.Turns[0].InputReplies.Length == 1 && composer.Text == "UNRELATED-COMPOSER-DRAFT", "Confirm lost the origin, draft or batch identity.");
            Check(received.Any(r => r.Contains(firstChoice)) && received.All(r => !r.Contains("UNRELATED-COMPOSER-DRAFT")), "Continuation received the wrong input.");
            await Reload();
            shown = await Show(id);
            var old = Descendants<RadioButton>(shown).Single(b => Equals(b.Content, "看例子"));
            var current = Descendants<RadioButton>(shown).Single(b => Equals(b.Content, "完整"));
            Check(old.IsChecked == true && !old.IsEnabled && current.IsEnabled && current.IsChecked != true && !ButtonOn(shown, "确认并继续").IsEnabled,
                "The second question reused the previous answer or unlocked it.");
            current.IsChecked = true; received.Clear();
            Script(_ => server.Say("现在按你的选择展开完整例子。"));
            Click(ButtonOn(shown, "确认并继续")); await Idle();
            await Reload();
            var completed = Active().Turns[0];
            Check(completed.Id == id && Active().Turns.Count == 2 && !completed.IsWaitingForInput && completed.AnsweredAt.HasValue &&
                completed.Answer.Contains("完整例子") && completed.InputReplies.Length == 2 && composer.Text == "UNRELATED-COMPOSER-DRAFT" &&
                received.Any(r => r.Contains("\"radio:path\":\"full\"")) && Active().Turns[1].Id == extra.Id, "A second confirmation did not complete the original turn.");
            shown = await Show(id);
            Check(!Descendants<Button>(shown).Any(b => Equals(b.Content, "确认并继续") && b.IsVisible), "Completed turn still offers confirmation.");
            Script(Ask, _ => server.Say("请给出你想先理解的符号。"));
            composer.Text = "请等我回答再讲"; await panel.SubmitAsync(); Done();
            var tail = Active().Turns.Last();
            shown = await Show(tail.Id);
            Check(Descendants<TextBox>(shown).Any(t => !t.IsReadOnly) && !ButtonOn(shown, "确认并继续").IsEnabled, "Question without form markup has no answer field.");
            var count = Active().Turns.Count;
            Descendants<TextBox>(shown).Single(t => !t.IsReadOnly).Text = "USER-ENDED-RESPONSE";
            Click(ButtonOn(shown, "结束本轮"));
            await WaitUntilAsync(() => !Active().Turns.Last().IsWaitingForInput, "Could not end the wait.");
            await panel.SaveAsync(); await Reload();
            Check(Active().Turns.Count == count && !Active().Turns.Last().IsWaitingForInput && !Active().Turns.Last().CanRetryInputReply,
                "Ending a wait created another turn or offered automatic continuation.");
            shown = await Show(tail.Id);
            Check(Descendants<TextBox>(shown).Single(t => t.Text == "USER-ENDED-RESPONSE").IsReadOnly && AiForms.Snapshot(Active().Turns.Last()) is null,
                "The ended question was still editable or could be silently submitted.");
            Script(Ask, _ => server.Say("先选择比较方法。\n\n{{text:compare}}"));
            composer.Text = "先等我选方法"; await panel.SubmitAsync(); Done();
            var retryId = Active().Turns.Last().Id;
            shown = await Show(retryId);
            Descendants<TextBox>(shown).Single(t => !t.IsReadOnly).Text = "method-A";
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeReply = token => { started.TrySetResult(); return Task.Delay(Timeout.Infinite, token); };
            Click(ButtonOn(shown, "确认并继续"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Click((Button)panel.FindName("StopButton"));
            await WaitUntilAsync(() => Field(panel, "_operation") is null, "Stopping confirmed reasoning did not settle.");
            server.BeforeReply = null; await Reload();
            var interrupted = Active().Turns.Single(t => t.Id == retryId);
            Check(interrupted.CanRetryInputReply && interrupted.InputReplies.Length == 1, "Interrupted reply lost its confirmed input.");
            received.Clear(); Script(_ => server.Say("已按所选方法继续。"));
            shown = await Show(retryId);
            Click(Descendants<Button>(shown).Single(b => b.IsVisible && b.Content?.ToString()?.Contains("重试") == true));
            await Idle();
            var retried = AiStorage.Load(project).Turns.Single(t => t.Id == retryId);
            Check(retried.InputReplies.Length == 1 && retried.AnsweredAt.HasValue && retried.Answer.Contains("已按所选方法继续") &&
                received.Any(r => r.Contains("method-A")), "Retry duplicated confirmation or forgot the answer.");
            // An older release let the user edit the clarification turn before it was resumed.
            var legacy = new AiTurn("旧轮次", "原说明。");
            var notebook = Active();
            notebook.Turns.Add(legacy);
            var baseline = panel.BeginRevision(legacy);
            await panel.CommitRevisionAsync(legacy.Id, baseline.Version, [baseline.Rows[0] with { Text = "已保存的说明。" }], "user");
            var currentLegacy = notebook.Turns[^1];
            var draft = panel.BeginRevision(currentLegacy);
            notebook.Turns[^1] = currentLegacy with
            {
                RevisionRequest = new AiRevisionRequest(id, Guid.NewGuid().ToString("N"), AwaitingReply: true),
                UserRevisionDraft = draft with { Rows = [draft.Rows[0] with { Text = "用户另存的整理草稿。" }] }
            };
            await panel.SaveAsync(); await Reload();
            var migrated = Active().Turns[^1];
            Check(migrated.IsWaitingForInput && !migrated.CanReviseAnswer && migrated.UserRevisionDraft?.Rows[0].Text == "用户另存的整理草稿。" &&
                AiAnswerRevision.Resolve($"请改写第{Active().Turns.Count}轮", Active().Turns, []) == null &&
                AiAnswerRevision.Resolve("请改写第1轮", Active().Turns, [])?.TargetTurnId == id,
                "Waiting state exposed the wrong revision target or lost the old draft");
            shown = await Show(legacy.Id);
            var menu = shown.Answers.First().ContextMenu!;
            menu.PlacementTarget = shown.Answers.First(); menu.IsOpen = true;
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!menu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() is "整理这一段" or "请 AI 改这一轮"), "Waiting turn exposes an editor command");
            menu.IsOpen = false;
            Active().Active.Permission = AiPermission.FullAccess;
            AiRevisionRunner Reader() => (AiRevisionRunner)Invoke(panel, "CreateRevisionRunner",
                new AiRevisionRequest(legacy.Id, Guid.NewGuid().ToString("N")), (Action<bool>)(_ => { }))!;
            try { await Reader()("read_answer_revision", """{"start_row":0,"max_rows":10}""", default); throw new Exception("Waiting question was available to a revision tool"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("找不到授权改稿的轮次")) { }
            Active().Active.Permission = AiPermission.ReadOnly;
            Descendants<TextBox>(shown).Single(t => !t.IsReadOnly).Text = "确认选择。";
            Script(_ => server.Say("新追加的续答。"));
            Click(ButtonOn(shown, "确认并继续")); await Idle(); await Reload();
            var finishedLegacy = Active().Turns[^1];
            var savedUser = finishedLegacy.RevisionHistory!.Single(r => r.Author == "user");
            Check(finishedLegacy.CanReviseAnswer && savedUser.Sources.Values.Single() == "已保存的说明。" &&
                finishedLegacy.RevisionHistory![^1].Rows[^1].Text == "新追加的续答。" &&
                finishedLegacy.UserRevisionDraft!.Rows[^1].Text == "新追加的续答。", "Continuation rewrote history or omitted appended rows from the draft");
            Active().Active.Permission = AiPermission.FullAccess;
            var resumedRead = await Reader()("read_answer_revision", """{"start_row":0,"max_rows":10}""", default);
            var reopened = AiStorage.Load(project).Turns[^1];
            Check(resumedRead.Contains("用户另存的整理草稿。") && resumedRead.Contains("新追加的续答。") && reopened.UserRevisionDraft == null &&
                reopened.Answer.Contains("clarification") && reopened.RevisionHistory![^1].Author == "user", "Finishing a legacy draft removed its question or continuation");
            await CheckInputJournalAsync(output);
            Console.WriteLine("WPF/HTTP: waiting, persisted selection, confirmation, non-final turn continuation, repeated field ID, draft isolation, ordinary exercises, fallback and cancellation passed.");
        }
        finally { host.Content = null; host.Close(); }
    }
}
