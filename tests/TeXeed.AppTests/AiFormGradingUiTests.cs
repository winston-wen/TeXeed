using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class GradingHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource[] Entered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal readonly TaskCompletionSource[] Release = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal readonly string[] Responses = new string[4];
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var index = Interlocked.Increment(ref Requests) - 1;
            if (index >= Entered.Length) throw new InvalidOperationException("Unexpected grading request");
            Entered[index].TrySetResult(); await Release[index].Task.WaitAsync(token);
            var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = Responses[index] });
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + delta + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static string GradeReply(params (string Source, string Submission, Dictionary<string, (string Result, string Comment)> Fields)[] forms)
    {
        var body = new { forms = forms.Select(form => new { turn = form.Source, submission = form.Submission,
            fields = form.Fields.ToDictionary(pair => pair.Key, pair => new { result = pair.Value.Result, comment = pair.Value.Comment }) }).ToArray() };
        return "批改结果如下。\n\n```texeed-grade\n" + JsonSerializer.Serialize(body) + "\n```";
    }
    private static async Task CheckAiFormGradingUiAsync(MainWindow owner, string output)
    {
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new GradingHandler(); using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, "grading-ui.tex"); await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var first = AiForms.WithSubmission(new AiTurn("第一张题", "# 第一张\n\n{{text:same}} {{text:same}}\n\n{{checkbox:same:勾选}} {{file:same:史料}}"), null);
        var second = AiForms.WithSubmission(new AiTurn("第二张题", "# 第二张\n\n{{text:b}}"), null);
        book.Turns.Add(first); book.Turns.Add(second);
        var path = Path.Combine(output, "grading-original.txt"); var replacement = Path.Combine(output, "grading-replacement.txt");
        File.WriteAllText(path, "old attachment"); File.WriteAllText(replacement, "new attachment");
        var origin = new AiFormAttachmentOrigin(1, book.Active.Id, book.Active.Title, first.Id, 1, "same", "史料");
        if (!panel.AddFileReferences([path], origin, () => true)) throw new Exception("Could not attach grading fixture");
        if (!panel.AddFileReferences([replacement], origin, () => true)) throw new Exception("Could not attach deselected fixture");
        var deselected = book.Entries.Last(); deselected.AttachmentSelected = false;
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 980, Height = 760, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var aView = views[first.Id].Answers.Single(); var bView = views[second.Id].Answers.Single(); await Task.WhenAll(aView.RenderTask, bView.RenderTask); host.UpdateLayout();
            var aTexts = Descendants<TextBox>(aView).Where(t => AutomationProperties.GetName(t) == "same").ToArray();
            var check = Descendants<CheckBox>(aView).Single(); var attachment = Descendants<FormAttachmentButton>(aView).Single();
            var bText = Descendants<TextBox>(bView).Single(t => AutomationProperties.GetName(t) == "b");
            aTexts[0].Text = "A1"; check.IsChecked = true; bText.Text = "B1";
            if (aTexts[1].Text != "A1") throw new Exception("Duplicate form fields did not synchronize");
            await panel.SaveAsync();
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "请批改";
            var firstSend = panel.SubmitAsync(); await handler.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pendingPath = AiTurnCheckpoint.PathsFor(project).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            var journal = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(pendingPath))!;
            if (AiForms.AllFromTurn(journal.Turn).Count != 2 || !aTexts[0].IsReadOnly || !bText.IsReadOnly) throw new Exception("Multiple open forms were not checkpointed and locked together");
            composer.Text = "排队继续"; await CallAsync(panel, "AddFollowUpAsync", "queue");
            handler.Responses[0] = GradeReply(
                (first.Id, journal.Turn.Id, new() { ["text:same"] = ("correct", "计算正确"), ["checkbox:same"] = ("uncertain", "需要进一步核对"), ["file:same"] = ("incorrect", "材料不匹配") }),
                (second.Id, journal.Turn.Id, new() { ["text:b"] = ("incorrect", "请重新计算") }));
            handler.Release[0].TrySetResult(); await firstSend.WaitAsync(TimeSpan.FromSeconds(20));
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (aTexts.Any(t => t.IsReadOnly) || bText.IsReadOnly || !check.IsEnabled || !attachment.IsEnabled) throw new Exception("Validated grading did not reopen its forms");
            if (!AutomationProperties.GetHelpText(aTexts[0]).Contains("正确") || !AutomationProperties.GetHelpText(aTexts[1]).Contains("正确") ||
                !AutomationProperties.GetHelpText(check).Contains("待核实") || !AutomationProperties.GetHelpText(attachment).Contains("错误") || !AutomationProperties.GetHelpText(bText).Contains("错误"))
                throw new Exception("Inline grade marks or accessible explanations are missing");
            Capture(host, Path.Combine(output, "ai-grading-initial.png"));
            if (!attachment.Content.ToString()!.Contains("上次提交 1 项")) throw new Exception("Reopened file control lost its previous submission");
            var queue = AiFollowUpQueue.Load(book.Active);
            if (!queue.IsPaused || queue.Items.Count != 1 || handler.Requests != 1) throw new Exception("Reopened forms were silently resubmitted by the queued message");
            var gradeTurn = book.Turns.Last();
            var gradeView = ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!).GetValueOrDefault(gradeTurn.Id);
            if (gradeView is not null)
                foreach (var rendered in gradeView.Answers)
                    if (rendered.Markdown.Contains("texeed-grade")) throw new Exception("A validated machine grade block remained in the reading view");
            aTexts[0].Text = "A2";
            if (aTexts[1].Text != "A2" || AutomationProperties.GetHelpText(aTexts[0]).Contains("正确") || !AutomationProperties.GetHelpText(bText).Contains("错误"))
                throw new Exception("Editing did not clear only the changed field's grade");
            deselected.AttachmentSelected = true;
            Invoke(panel, "Entry_Changed", new CheckBox { DataContext = deselected }, new RoutedEventArgs());
            if (AutomationProperties.GetHelpText(attachment).Contains("错误")) throw new Exception("Changing an attachment selection did not invalidate its previous grade");
            if (!panel.AddFileReferences([replacement], origin, () => true)) throw new Exception("Could not add a new file to the reopened form");
            Capture(host, Path.Combine(output, "ai-grading-reopened.png"));
            await panel.SaveAsync();
            var reopened = AiStorage.Load(project).Active;
            if (!AiFormGrading.IsReopened(reopened, first.Id) || AiForms.Value(AiForms.Draft(reopened.Turns.Single(t => t.Id == first.Id)), "text", "same") != "A2")
                throw new Exception("Reopening after grading lost editable draft or state");
            composer.Text = "检查修改"; var secondSend = panel.SubmitAsync(); await handler.Entered[1].Task.WaitAsync(TimeSpan.FromSeconds(15));
            var secondJournal = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(pendingPath))!;
            var secondForms = AiForms.AllFromTurn(secondJournal.Turn);
            if (secondForms.Count != 2 || secondForms.Single(s => s.TurnId == first.Id).Values.GetValueOrDefault("text:same") != "A2" ||
                secondForms.Single(s => s.TurnId == second.Id).Values.GetValueOrDefault("text:b") != "B1") throw new Exception("Reopened forms were not independently resubmitted");
            handler.Responses[1] = "收到修改。"; handler.Release[1].TrySetResult(); await secondSend.WaitAsync(TimeSpan.FromSeconds(20));
            if (AiFormGrading.IsReopened(book.Active, first.Id) || AiFormGrading.IsReopened(book.Active, second.Id)) throw new Exception("An ordinary answer incorrectly reopened forms");
            composer.Text = "现在批改上一次"; var thirdSend = panel.SubmitAsync(); await handler.Entered[2].Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.Responses[2] = GradeReply((first.Id, secondJournal.Turn.Id, new() { ["text:same"] = ("incorrect", "再检查") }));
            handler.Release[2].TrySetResult(); await thirdSend.WaitAsync(TimeSpan.FromSeconds(20));
            if (!AiFormGrading.IsReopened(book.Active, first.Id) || AiFormGrading.IsReopened(book.Active, second.Id)) throw new Exception("Later grading did not reopen only its target form");
            composer.Text = "再次提交"; var fourthSend = panel.SubmitAsync(); await handler.Entered[3].Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.Responses[3] = GradeReply((first.Id, "nonexistent-submission", new() { ["text:same"] = ("correct", "错误关联") }));
            handler.Release[3].TrySetResult(); await fourthSend.WaitAsync(TimeSpan.FromSeconds(20));
            if (AiFormGrading.IsReopened(book.Active, first.Id) || !((TextBlock)panel.FindName("StatusText")).Text.Contains("无法关联"))
                throw new Exception("An invalid grade silently reopened a form");
            await panel.StopAndSaveAsync();
            Console.WriteLine("PASS: multiple forms, inline feedback, edit invalidation, file history, queue pause, later grading and invalid grade rejection.");
        }
        finally { foreach (var gate in handler.Release) gate.TrySetResult(); host.Close(); }
    }

    private static async Task CheckAiFormGradeRollbackAsync(MainWindow owner, string output, bool sharedStore)
    {
        using var handler = new GradingHandler(); using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, sharedStore ? "grading-rollback-shared.tex" : "grading-rollback-local.tex");
        if (sharedStore)
        {
            var notebook = new AiNotebook(); var conversation = notebook.Active;
            panel.Bind(new AiWorkspaceStore(project, notebook), conversation.Id);
        }
        else await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var source = AiForms.WithSubmission(new AiTurn("题目", "{{text:answer}}"), null);
        book.Turns.Add(source); AiForms.SetDraft(source, "text", "answer", "答案");
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 800, Height = 500, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await panel.SaveAsync();
            ((TextBox)panel.FindName("Composer")).Text = "请批改";
            var operation = panel.SubmitAsync(); await handler.Entered[0].Task.WaitAsync(TimeSpan.FromSeconds(15));
            var journalPath = AiTurnCheckpoint.PathsFor(project).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            var journal = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(journalPath))!;
            handler.Responses[0] = GradeReply((source.Id, journal.Turn.Id, new() { ["text:answer"] = ("correct", "正确") }));
            var originalHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
            var blocked = Path.Combine(output, sharedStore ? "grading-blocked-shared" : "grading-blocked-local"); File.WriteAllText(blocked, "not a directory");
            try { Environment.SetEnvironmentVariable("TEXEED_HOME", blocked); handler.Release[0].TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { Environment.SetEnvironmentVariable("TEXEED_HOME", originalHome); }
            if (book.Turns.Count != 1 || AiFormGrading.IsReopened(book.Active, source.Id) || source.AdditionalData?.ContainsKey(AiFormGrading.StorageKey) == true || !File.Exists(journalPath))
                throw new Exception("Failed grade persistence exposed or retained an uncommitted grade, or discarded the checkpoint");
            var recovered = AiStorage.Load(project).Active;
            if (recovered.Turns.Count != 2 || !recovered.Turns.Last().IsIncomplete || AiFormGrading.IsReopened(recovered, source.Id))
                throw new Exception("Crash recovery after failed grade persistence unlocked the form");
            Console.WriteLine("PASS: grade save failure rolls back and retains checkpoint (" + (sharedStore ? "shared" : "local") + ").");
        }
        finally { handler.Release[0].TrySetResult(); host.Close(); }
    }
}
