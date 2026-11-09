using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Text.Json;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiReliabilityAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        new AiCredentials { ApiKey = "fixture-never-send", UseApiKey = true }.Save();
        var paragraph = "正文包含 **强调文字**、`main_file.tex` 与 [外部链接](https://example.com)。";
        var streaming = new MarkdownView { IsStreaming = true, Markdown = "第一段" };
        var streamDocument = streaming.Document;
        for (var i = 0; i < 200; i++) streaming.Markdown += "\n\n" + paragraph + " $x_i$";
        Check(ReferenceEquals(streaming.Document, streamDocument) && Descendants<ChatFormula>(streaming).Count() == 0, "Stream replaced documents or queued transient TeX jobs");
        Check(new System.Windows.Documents.TextRange(streaming.Document.ContentStart, streaming.Document.ContentEnd).Text.Contains(streaming.Markdown), "Incremental stream lost text");
        var recoveryProject = Path.Combine(output, "checkpoint-" + Guid.NewGuid().ToString("N") + ".tex");
        var original = new AiNotebook();
        var buffer = new AiStreamBuffer(); var pending = new AiTurn("检查并纠正写错的公式", "") { Prompt = "检查并纠正写错的公式", SentAnnotations = [new() { Question = "保留批注", PdfBlocks = [new(1, 0, 0, 10, 10, "snapshot-data")] }] };
        original.Entries.Add(pending.SentAnnotations.Single().Snapshot()); AiStorage.Save(recoveryProject, original);
        var journal = await AiTurnCheckpoint.StartAsync(recoveryProject, original.Active.Id, "恢复测试", pending, buffer);
        Check(AiStorage.Load(recoveryProject).Turns.Single().Question == pending.Question, "Accepted question was not durable before a response");
        Check(AiStorage.Load(recoveryProject).Entries.Count == 1, "Checkpoint recovery changed legacy draft data");
        buffer.Add(new("partial", "text", "正在检查公式 $x_i$，部分回答。"));
        // Deliberately stop pumping WPF: persistence must not depend on a UI timer.
        Thread.Sleep(1100);
        var recovered = await Task.Run(() => AiStorage.Load(recoveryProject));
        Check(recovered.Turns.Single().Answer.Contains("部分回答") && recovered.Turns.Single().SentAnnotations.Single().PdfBlocks.Single().ImageBase64 == "snapshot-data", "Checkpoint failed while dispatcher was blocked");
        Check(recovered.Turns.Single().AnsweredAt is null && recovered.Turns.Single().Answer.StartsWith("[本轮未完成]"), "Interrupted checkpoint pretended to be completed");
        await journal.StopAsync();
        using (var locked = new FileStream(AiStorage.NotebookPath(recoveryProject), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = false;
            try { AiStorage.Save(recoveryProject, recovered); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && File.Exists(AiTurnCheckpoint.PathFor(recoveryProject)), "Failed normal save discarded the recovery journal");
        }
        AiStorage.Save(recoveryProject, recovered);
        Check(!File.Exists(AiTurnCheckpoint.PathFor(recoveryProject)) && AiStorage.Load(recoveryProject).Turns.Count == 1, "Recovered turn was duplicated or journal not retired");
        var largeBudget = new AiContextBudget(1_000_000);
        Check(largeBudget.Size(new string('x', 200_000)) < largeBudget.Limit, "Large model still compacts at the old 96k character ceiling");
        Check(new AiContextBudget(128000).Size(new string('中', 50000)) > new AiContextBudget(128000).Limit, "Conservative Unicode budget lost overflow protection");
        var compaction = new CompactionHandler { Working = true };
        using (var largeConnection = new AiConnection(compaction))
        {
            var history = new[] { new AiTurn("保留原始约束", new string('x', 200000)) };
            await new AiResponses(largeConnection).RunAsync("fixture", [new() { Question = "继续" }], history, [], _ => { }, _ => { }, CancellationToken.None,
                contextWindow: 1_000_000, toolRunner: (name, _, _) => Task.FromResult(name == "list_files" ? "main.tex" : new string('x', 200000)));
            Check(compaction.Summaries == 0 && compaction.LastInput.Contains("保留原始约束"), "Low-usage large-model context was prematurely compacted");
        }
        using (var stalled = new StalledAiHandler())
        using (var stalledConnection = new AiConnection(stalled))
        using (var sending = new AiPanel(stalledConnection))
        {
            var sendHost = new Window { Owner = owner, Content = sending, Width = 780, Height = 800 };
            sendHost.Show();
            var sendProject = Path.Combine(output, "send-checkpoint-" + Guid.NewGuid().ToString("N") + ".tex"); await sending.SetProjectAsync(sendProject);
            var models = (ComboBox)sending.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            await sending.Settings.SaveSelectionAsync(CancellationToken.None);
            ((TextBox)sending.FindName("Composer")).Text = "检查并纠正公式，网络尚未返回";
            var send = sending.SubmitAsync(); await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var accepted = await Task.Run(() => AiStorage.Load(sendProject));
            Check(accepted.Turns.Single().Question.Contains("网络尚未返回"), "Actual Submit cleared an unsaved accepted question");
            ((Button)sending.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            var canceled = AiStorage.Load(sendProject);
            Check(canceled.Turns.Count == 1 && canceled.Turns.Single().Answer.StartsWith("[本轮未完成]") && !File.Exists(AiTurnCheckpoint.PathFor(sendProject)), "Stopped request lost/duplicated the accepted question");
            await sending.SetProjectAsync(sendProject);
            var rejectedProject = Path.Combine(output, "rejected-acceptance-" + Guid.NewGuid().ToString("N") + ".tex"); await sending.SetProjectAsync(rejectedProject);
            var composer = (TextBox)sending.FindName("Composer"); composer.Text = "保留未接受的主草稿";
            var journalPath = AiTurnCheckpoint.PathFor(rejectedProject);
            using (var lockedJournal = new FileStream(journalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await sending.SubmitAsync();
                Check(composer.Text == "保留未接受的主草稿", "Failed durable acceptance consumed the composer draft");
            }
            File.Delete(journalPath); await sending.SaveAsync();
            sendHost.Close();
        }
        Console.WriteLine($"AI reliability: {checks} checks passed.");
    }
    private sealed class StalledAiHandler : System.Net.Http.DelegatingHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StalledAiHandler() : base(new AiFakeHandler()) { }
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/responses")) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return await base.SendAsync(request, token);
        }
    }
}
