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
        using var connection = new AiConnection(new AiFakeHandler());
        using var panel = new AiPanel(connection);
        var host = new Window { Owner = owner, Width = 820, Height = 900, Content = panel };
        host.Show(); await panel.SetProjectAsync(Path.Combine(output, "reliability.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        var paragraph = "正文用于检验鼠标命中测试、滚轮和滚动条的延迟。包含 **强调文字**、`main_file.tex` 与 [外部链接](https://example.com)。";
        var answer = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => "## 小标题 " + i + "\n\n" + paragraph + "\n\n$$x_i^2+\\frac{a}{b}$$"));
        for (var i = 0; i < 8; i++) book.Turns.Add(new("压力测试 " + i, answer));
        Console.WriteLine("Stress: constructing 8 turns / 480 sections and formulas");
        Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
        await AwaitMathAsync(panel); await Task.Delay(150); host.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var initialRealized = Descendants<AiTurnView>(panel).Count();
        Console.WriteLine($"Stress: realized turns={initialRealized}/8, viewport={scroll.ViewportHeight:F1}, extent={scroll.ExtentHeight:F1}");
        Check(initialRealized < 8, "Conversation still creates every off-screen rich document");
        var worst = 0d; var total = Stopwatch.StartNew();
        var lastInput = Stopwatch.GetTimestamp(); var inputGap = 0d;
        var inputTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        inputTimer.Tick += (_, _) => { var now = Stopwatch.GetTimestamp(); inputGap = Math.Max(inputGap, Stopwatch.GetElapsedTime(lastInput, now).TotalMilliseconds); lastInput = now; };
        inputTimer.Start();
        for (var i = 0; i < 30; i++)
        {
            var watch = Stopwatch.StartNew();
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * i / 30);
            host.UpdateLayout();
            var layoutMs = watch.Elapsed.TotalMilliseconds;
            for (var y = 80; y < 600; y += 25) _ = panel.InputHitTest(new Point(200, y));
            var hitMs = watch.Elapsed.TotalMilliseconds - layoutMs;
            // Measure the priority used by real mouse/keyboard input. Background
            // Markdown preparation is deliberately allowed to continue between frames.
            await Dispatcher.Yield(DispatcherPriority.Input);
            if (watch.ElapsedMilliseconds > 30) Console.WriteLine($"Scroll sample {i}: layout={layoutMs:F1}ms hit={hitMs:F1}ms queued={watch.Elapsed.TotalMilliseconds - layoutMs - hitMs:F1}ms");
            worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
            await Task.Delay(16);
        }
        var scrollTotal = total.ElapsedMilliseconds;
        inputTimer.Stop();
        Console.WriteLine($"Stress: scroll + pointer hit-test worst={worst:F1}ms input-gap={inputGap:F1}ms total={scrollTotal}ms");
        Check(worst < 250, "A scroll/pointer frame exceeded the 250ms regression budget");
        var inputDelay = Math.Max(0, inputGap - inputTimer.Interval.TotalMilliseconds);
        Check(inputDelay < 400, "Cold rich-document layout delayed real input ticks over the 400ms budget");
        var bar = Descendants<ScrollBar>(scroll).First(b => b.Orientation == Orientation.Vertical && b.Maximum > 0);
        var thumb = Descendants<Thumb>(bar).Single();
        var beforeDrag = scroll.VerticalOffset;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, -80) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, -80, false) { RoutedEvent = Thumb.DragCompletedEvent });
        host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background);
        Check(Math.Abs(scroll.VerticalOffset - beforeDrag) > 1, "Scrollbar thumb did not scroll the conversation");
        GetCursorPos(out var oldPointer);
        try
        {
            host.Activate();
            for (var i = 0; i < 25; i++)
            {
                var point = panel.PointToScreen(new Point(180 + i * 3, 80 + i * 15));
                var watch = Stopwatch.StartNew(); SetCursorPos((int)point.X, (int)point.Y);
                await Task.Delay(16); await Dispatcher.Yield(DispatcherPriority.Background);
                Check(watch.ElapsedMilliseconds < 500, "Native pointer movement stalled the dispatcher");
            }
        }
        finally { SetCursorPos(oldPointer.X, oldPointer.Y); }
        scroll.ScrollToVerticalOffset(100); host.UpdateLayout();
        for (var settle = 0; settle < 4; settle++) { await AwaitMathAsync(panel); await Task.Delay(100); host.UpdateLayout(); }
        var viewport = scroll.ViewportHeight; var offset = scroll.VerticalOffset;
        var changes = 0; ScrollChangedEventHandler changed = (_, e) => { if (ReferenceEquals(e.OriginalSource, scroll)) changes++; };
        scroll.ScrollChanged += changed;
        for (var i = 0; i < 20; i++) { Invoke(panel, "UpdateReadingContext"); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); }
        scroll.ScrollChanged -= changed;
        Check(changes == 0 && viewport == scroll.ViewportHeight && offset == scroll.VerticalOffset, "Reading context caused a viewport/layout feedback loop");
        var title = (TextBlock)panel.FindName("ReadingContextTitle"); var annotations = (TextBlock)panel.FindName("ReadingContextAnnotations");
        Check(annotations.TranslatePoint(new Point(), title).Y >= title.ActualHeight, "Reading title and annotation summary overlap");
        var streaming = new MarkdownView { IsStreaming = true, Markdown = "第一段" };
        var streamDocument = streaming.Document;
        for (var i = 0; i < 200; i++) streaming.Markdown += "\n\n" + paragraph + " $x_i$";
        Check(ReferenceEquals(streaming.Document, streamDocument) && Descendants<ChatFormula>(streaming).Count() == 0, "Stream replaced documents or queued transient TeX jobs");
        Check(new System.Windows.Documents.TextRange(streaming.Document.ContentStart, streaming.Document.ContentEnd).Text.Contains(streaming.Markdown), "Incremental stream lost text");
        var folds = Descendants<AiTurnView>(panel).First();
        Check(Descendants<Button>(folds.Answers.First()).Count() == 0, "Headings still allocate embedded button controls");
        await File.WriteAllTextAsync(Path.Combine(output, "ai-performance.json"), JsonSerializer.Serialize(new { Turns = 8, Sections = 480, InitiallyRealizedTurns = initialRealized, WorstScrollAndHitTestMs = worst, MaxInputDelayMs = inputDelay, TotalMs = scrollTotal }));
        ((TextBox)panel.FindName("MessageSearchBox")).Text = "压力测试 7";
        await Task.Delay(100); await AwaitMathAsync(panel); host.UpdateLayout();
        Check(Descendants<AiTurnView>(panel).Any(v => v.Turn.Id == book.Turns[7].Id), "Search did not realize an off-screen result");
        var currentView = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == book.Turns[7].Id);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + currentView.TranslatePoint(new Point(), scroll).Y + 150);
        host.UpdateLayout(); await Task.Delay(120); await panel.SaveAsync();
        var rememberedId = book.Active.Id;
        var rememberedTurn = book.Active.ReadingTurnId; var rememberedOffset = book.Active.ReadingTurnOffset;
        await panel.NewConversationAsync(); await panel.SelectConversationAsync(rememberedId);
        for (var settle = 0; settle < 4; settle++) { await Task.Delay(100); await AwaitMathAsync(panel); host.UpdateLayout(); }
        Invoke(panel, "CaptureReadingAnchor");
        Check(book.Active.ReadingTurnId == rememberedTurn && Math.Abs(book.Active.ReadingTurnOffset - rememberedOffset) < 4, "Virtualization lost the turn-relative reading position");
        Capture(host, Path.Combine(output, "ai-stress.png"));
        host.Close();
        var recoveryProject = Path.Combine(output, "checkpoint.tex");
        var original = new AiNotebook();
        var buffer = new AiStreamBuffer(); var pending = new AiTurn("检查并纠正写错的公式", "") { Prompt = "检查并纠正写错的公式", SentAnnotations = [new() { Question = "保留批注", PdfBlocks = [new(1, 0, 0, 10, 10, "snapshot-data")] }] };
        original.Entries.Add(pending.SentAnnotations.Single().Snapshot()); AiStorage.Save(recoveryProject, original);
        var journal = await AiTurnCheckpoint.StartAsync(recoveryProject, original.Active.Id, "恢复测试", pending, buffer);
        Check(AiStorage.Load(recoveryProject).Turns.Single().Question == pending.Question, "Accepted question was not durable before a response");
        Check(AiStorage.Load(recoveryProject).Entries.Count == 0, "Crash between checkpoint and pending removal resurrected a sent annotation");
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
            var sendProject = Path.Combine(output, "send-checkpoint.tex"); await sending.SetProjectAsync(sendProject);
            var models = (ComboBox)sending.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            await sending.Settings.SaveSelectionAsync(CancellationToken.None);
            ((TextBox)sending.FindName("Composer")).Text = "检查并纠正公式，网络尚未返回";
            var sentEntry = new AiEntry { Question = "本轮发送的圈点", Sources = [new("main.tex", 1, 1, 0, "$x_i$", "revision")] };
            var skippedEntry = new AiEntry { Question = "未勾选圈点", Selected = false };
            sending.AddEntry(sentEntry); sending.AddEntry(skippedEntry);
            var send = sending.SubmitAsync(); await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(sending.AnnotationEntries.Select(e => e.Id).SequenceEqual(new[] { skippedEntry.Id }), "Accepted annotation still occupies the composer while response is pending");
            var newEntry = new AiEntry { Question = "等待期间的新圈点" }; sending.AddEntry(newEntry);
            var accepted = await Task.Run(() => AiStorage.Load(sendProject));
            Check(accepted.Turns.Single().Question.Contains("网络尚未返回"), "Actual Submit cleared an unsaved accepted question");
            Check(accepted.Turns.Single().SentAnnotations.Single().Sources.Single().Text == "$x_i$" && accepted.Entries.All(e => e.Id != sentEntry.Id), "Durable acceptance lost the sent snapshot or retained the pending duplicate");
            ((Button)sending.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            var canceled = AiStorage.Load(sendProject);
            Check(canceled.Turns.Count == 1 && canceled.Turns.Single().Answer.StartsWith("[本轮未完成]") && !File.Exists(AiTurnCheckpoint.PathFor(sendProject)), "Stopped request lost/duplicated the accepted question");
            Check(canceled.Entries.Select(e => e.Id).SequenceEqual(new[] { skippedEntry.Id, newEntry.Id }) && sending.AnnotationEntries.Count == 2, "Stop restored old circles or removed unselected/new pending circles");
            Check(canceled.Turns.Single().SentAnnotations.Single().Id == sentEntry.Id, "Stop lost the sent annotation archive");
            await sending.SetProjectAsync(sendProject);
            Check(sending.AnnotationEntries.Select(e => e.Id).SequenceEqual(new[] { skippedEntry.Id, newEntry.Id }), "Reopening a stopped conversation resurrected sent circles");
            var rejectedProject = Path.Combine(output, "rejected-acceptance.tex"); await sending.SetProjectAsync(rejectedProject);
            var rejectedEntry = new AiEntry { Question = "尚未成功记录的圈点" }; sending.AddEntry(rejectedEntry);
            var composer = (TextBox)sending.FindName("Composer"); composer.Text = "保留未接受的主草稿";
            var journalPath = AiTurnCheckpoint.PathFor(rejectedProject);
            using (var lockedJournal = new FileStream(journalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await sending.SubmitAsync();
                Check(sending.AnnotationEntries.Single().Id == rejectedEntry.Id && composer.Text == "保留未接受的主草稿", "Failed durable acceptance consumed an unsent circle or composer draft");
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
