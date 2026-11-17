using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class FollowUpHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var number = Interlocked.Increment(ref Requests);
            if (number == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"done\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class FollowUpHandoffHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var number = Interlocked.Increment(ref Requests);
            if (number == 1) { FirstEntered.TrySetResult(); await ReleaseFirst.Task.WaitAsync(token); }
            else if (number == 2) { SecondEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"done\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private static async Task CheckFollowUpPanelAsync(MainWindow owner, string output)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        Window Host(AiPanel panel) => new() { Owner = owner, Width = 820, Height = 720, Content = panel,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = owner.Left, Top = owner.Top, ShowActivated = owner.Left > -1000 };
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var crashProject = Path.Combine(output, "followup-crash-recovery.tex");
        var crashBook = new AiNotebook();
        var crashQueue = AiFollowUpQueue.Load(crashBook.Active);
        var crashSent = crashQueue.Enqueue("queue", "checkpointed dispatch", []);
        var crashWaiting = crashQueue.Enqueue("queue", "remaining dispatch", []);
        Check(crashQueue.MarkDispatching([crashSent.Id], "handoff-turn"), "Crash fixture could not mark dispatch");
        crashQueue.Store(crashBook.Active); AiStorage.Save(crashProject, crashBook);
        var crashTurn = new AiTurn("checkpointed dispatch", "[本轮未完成]") { Id = "handoff-turn", Prompt = "checkpointed dispatch", Incomplete = true };
        var crashCheckpoint = await AiTurnCheckpoint.StartAsync(crashProject, crashBook.Active.Id, crashBook.Active.Title, crashTurn, new AiStreamBuffer());
        await crashCheckpoint.StopAsync();
        var recovered = AiStorage.Load(crashProject);
        crashQueue = AiFollowUpQueue.Load(recovered.Active);
        Check(recovered.Turns.Single().Id == "handoff-turn" && crashQueue.RecoverWithoutActiveTurn(recovered.Active), "Checkpoint handoff was not recovered");
        crashQueue.Store(recovered.Active); AiStorage.Save(crashProject, recovered);
        var settled = AiStorage.Load(crashProject);
        Check(settled.Turns.Count == 1 && AiFollowUpQueue.Load(settled.Active).Items.Single().Id == crashWaiting.Id && AiFollowUpQueue.Load(settled.Active).IsPaused,
            "Checkpoint recovery duplicated or silently resent the dispatched message");
        using var handler = new FollowUpHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "followup-fixture.tex"));
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = Host(panel); host.Show();
        try
        {
            var composer = (TextBox)panel.FindName("Composer");
            composer.Text = "initial";
            var firstTurn = panel.SubmitAsync();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 1; i <= 2; i++)
            {
                composer.Text = "queued " + i;
                panel.AddEntry(new AiEntry { Title = "引用", Question = "comment " + i, Sources = [new("fixture.tex", 1, 1, 0, "text " + i, "revision")] });
                panel.AddEntry(new AiEntry { Title = "附件", EntryKind = "attachment", Files = [new("fixture" + i + ".txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes("file " + i)))] });
                await CallAsync(panel, "AddFollowUpAsync", "queue");
            }
            composer.Text = "new draft";
            var book = (AiNotebook)Field(panel, "_book")!;
            Check(AiFollowUpQueue.Load(book.Active).Items.Count == 2, "Two messages should be queued");
            host.UpdateLayout(); Capture(host, Path.Combine(output, "followup-queue.png"));
            host.Width = 420; host.UpdateLayout(); Capture(host, Path.Combine(output, "followup-queue-narrow.png"));
            host.Width = 820; host.UpdateLayout();
            handler.Release.TrySetResult();
            await firstTurn.WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && (panel.HasActiveOperation || AiFollowUpQueue.Load(book.Active).Items.Count != 0)) await Task.Delay(30);
            Check(book.Turns.Count == 3 && book.Turns[1].Prompt == "queued 1" && book.Turns[2].Prompt == "queued 2", "Queue did not deliver both messages in order");
            Check(book.Turns.Skip(1).All(t => t.SentAnnotations.Any(e => e.Sources.Count > 0) && t.SentAnnotations.Any(e => e.Files.Count > 0)), "Queue lost reference or attachment");
            Check(composer.Text == "new draft", "Queue dispatch consumed the new draft");
            await panel.StopAndSaveAsync();
            var restored = AiStorage.Load(Path.Combine(output, "followup-fixture.tex"));
            Check(restored.Active.Draft == "new draft" && restored.Active.Turns.Count == 3 && AiFollowUpQueue.Load(restored.Active).Items.Count == 0, "Closing did not persist completed work and draft");
            Console.WriteLine("PASS: queued panel order, references, attachments, draft and close persistence.");
        }
        finally { host.Close(); }

        using var handoff = new FollowUpHandoffHandler();
        using var handoffPanel = new AiPanel(new AiConnection(handoff));
        var handoffProject = Path.Combine(output, "followup-handoff.tex");
        await handoffPanel.SetProjectAsync(handoffProject);
        models = (ComboBox)handoffPanel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var handoffHost = Host(handoffPanel); handoffHost.Show();
        try
        {
            var composer = (TextBox)handoffPanel.FindName("Composer");
            composer.Text = "first";
            var first = handoffPanel.SubmitAsync();
            await handoff.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            composer.Text = "now running"; await CallAsync(handoffPanel, "AddFollowUpAsync", "queue");
            composer.Text = "still waiting"; await CallAsync(handoffPanel, "AddFollowUpAsync", "queue");
            composer.Text = "preserved draft";
            var book = (AiNotebook)Field(handoffPanel, "_book")!;
            var currentId = AiFollowUpQueue.Load(book.Active).Items[0].Id;
            handoff.ReleaseFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            await handoff.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queue = AiFollowUpQueue.Load(book.Active);
            Check(queue.Items.Count == 1 && queue.Items[0].Prompt == "still waiting", "A delivered message must leave the persisted pending queue");
            Check(((FrameworkElement)handoffPanel.FindName("LiveContainer")).IsVisible && ((AiTurn)Field(handoffPanel, "_foldingLiveTurn")!).Prompt == "now running", "The dispatched message must be visible as the live conversation turn");
            var pendingItems = (Panel)handoffPanel.FindName("FollowUpItems");
            var labels = Descendants<TextBlock>(pendingItems).Select(t => t.Text).ToArray();
            Check(labels.Any(t => t.Contains("still waiting")) && labels.All(t => !t.Contains("now running") && !t.Contains("发送中")), "The current turn remained in the waiting list");
            handoffHost.UpdateLayout(); Capture(handoffHost, Path.Combine(output, "followup-handoff-live.png"));
            Check(composer.Text == "preserved draft", "Dispatch changed the new draft");
            var checkpoint = AiStorage.Load(handoffProject);
            Check(checkpoint.Active.Turns.Any(t => t.Prompt == "now running") && AiFollowUpQueue.Load(checkpoint.Active).Items.All(i => i.Id != currentId), "Checkpoint recovery duplicated the dispatched message");
            ((Button)handoffPanel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !handoffPanel.HasActiveOperation, "Queued turn did not stop");
            await WaitUntilAsync(() => !(bool)Field(handoffPanel, "_queueDispatching")!, "Queue dispatch did not settle");
            var staleQueue = AiFollowUpQueue.Load(book.Active);
            var stale = staleQueue.Enqueue("queue", "already in transcript", []);
            Check(staleQueue.MarkDispatching([stale.Id], book.Turns.Last(t => t.Prompt == "now running").Id), "Could not create a failed-save queue record");
            staleQueue.Store(book.Active);
            Invoke(handoffPanel, "RefreshFollowUps");
            labels = Descendants<TextBlock>(pendingItems).Select(t => t.Text).ToArray();
            Check(labels.Any(t => t.Contains("still waiting")) && labels.All(t => !t.Contains("already in transcript"))
                && AiFollowUpQueue.Load(book.Active).Items.Any(i => i.Id == stale.Id && i.State == "dispatching"),
                "A failed queue save showed an already-saved turn as waiting or discarded the recovery record");
            var duplicateTurn = book.Turns.Last(t => t.Prompt == "now running") with { Answer = "another turn with the same ID" };
            book.Turns.Add(duplicateTurn); Invoke(handoffPanel, "RefreshFollowUps");
            labels = Descendants<TextBlock>(pendingItems).Select(t => t.Text).ToArray();
            Check(labels.Any(t => t.Contains("already in transcript")), "An ambiguous turn ID hid a recovery record");
            book.Turns.Remove(duplicateTurn);
            staleQueue = AiFollowUpQueue.Load(book.Active);
            Check(staleQueue.Complete([stale.Id]), "Could not clean up failed-save fixture"); staleQueue.Store(book.Active);
            await handoffPanel.StopAndSaveAsync();
            var reloaded = AiStorage.Load(handoffProject).Active;
            queue = AiFollowUpQueue.Load(reloaded);
            Check(reloaded.Turns.Count(t => t.Prompt == "now running") == 1 && reloaded.Turns.Last(t => t.Prompt == "now running").IsIncomplete && queue.Items.Count == 1 && queue.Items[0].Prompt == "still waiting" && queue.IsPaused,
                "Stopping a delivered turn must preserve one transcript retry and pause the remaining queue");
            Check(handoff.Requests == 2 && reloaded.Draft == "preserved draft", "Stopping retried a message or lost the draft");
            Console.WriteLine("PASS: accepted queued turn leaves waiting list, preserves checkpoint, and is not duplicated after stopping.");
        }
        finally { handoffHost.Close(); }

        using var stalled = new FollowUpHandler();
        using var pausedPanel = new AiPanel(new AiConnection(stalled));
        var pausedProject = Path.Combine(output, "followup-paused.tex");
        await pausedPanel.SetProjectAsync(pausedProject);
        models = (ComboBox)pausedPanel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var pausedHost = Host(pausedPanel); pausedHost.Show();
        try
        {
            var composer = (TextBox)pausedPanel.FindName("Composer");
            composer.Text = "interruptible";
            var initial = pausedPanel.SubmitAsync();
            await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            composer.Text = "pending after stop";
            await CallAsync(pausedPanel, "AddFollowUpAsync", "queue");
            composer.Text = "draft after stop";
            ((Button)pausedPanel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await initial.WaitAsync(TimeSpan.FromSeconds(10));
            var book = (AiNotebook)Field(pausedPanel, "_book")!;
            var queue = AiFollowUpQueue.Load(book.Active);
            Check(queue.IsPaused && queue.Items.Count == 1 && stalled.Requests == 1, "Cancel must pause pending messages without sending them");
            await pausedPanel.SetProjectAsync(Path.Combine(output, "another-project.tex"));
            var reloaded = AiStorage.Load(pausedProject).Active;
            queue = AiFollowUpQueue.Load(reloaded);
            Check(queue.IsPaused && queue.Items.Single().Prompt == "pending after stop" && reloaded.Draft == "draft after stop", "Project switch lost the paused queue or draft");
            Check(stalled.Requests == 1, "Project switch unexpectedly sent pending work");
            await pausedPanel.StopAndSaveAsync();
            Console.WriteLine("PASS: cancellation pauses pending messages and project switch preserves them.");
        }
        finally { pausedHost.Close(); }

        using var steeringHandler = new FollowUpHandler();
        using var steerPanel = new AiPanel(new AiConnection(steeringHandler));
        await steerPanel.SetProjectAsync(Path.Combine(output, "followup-convert.tex"));
        models = (ComboBox)steerPanel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var steerHost = Host(steerPanel); steerHost.Show();
        try
        {
            var composer = (TextBox)steerPanel.FindName("Composer");
            composer.Text = "initial";
            var run = steerPanel.SubmitAsync();
            await steeringHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            composer.Text = "convert me";
            steerPanel.AddEntry(new AiEntry { Question = "attached reference", Sources = [new("fixture.tex", 1, 1, 0, "source", "revision")] });
            await CallAsync(steerPanel, "AddFollowUpAsync", "queue");
            var book = (AiNotebook)Field(steerPanel, "_book")!;
            var id = AiFollowUpQueue.Load(book.Active).Items.Single().Id;
            Check(Descendants<Button>(steerPanel).Any(b => Equals(b.Content, "转为引导")), "Queued message has no conversion action");
            await CallAsync(steerPanel, "ConvertQueuedToSteerAsync", id);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            var queue = AiFollowUpQueue.Load(book.Active);
            Check(book.Turns.Count == 1 && queue.Items.Count == 0 && queue.History.Single().Id == id && queue.History[0].Entries.Count == 1, "Conversion lost the accepted steering payload");
            Check(steeringHandler.Requests == 2, "Conversion should hand over to the same turn exactly once");
            await steerPanel.StopAndSaveAsync();
            Console.WriteLine("PASS: queued message converted to current-turn steering.");
        }
        finally { steerHost.Close(); }
    }
}
