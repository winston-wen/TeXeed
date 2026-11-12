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

    private static async Task CheckFollowUpPanelAsync(MainWindow owner, string output)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FollowUpHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "followup-fixture.tex"));
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 820, Height = 720, Content = panel }; host.Show();
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

        using var stalled = new FollowUpHandler();
        using var pausedPanel = new AiPanel(new AiConnection(stalled));
        var pausedProject = Path.Combine(output, "followup-paused.tex");
        await pausedPanel.SetProjectAsync(pausedProject);
        models = (ComboBox)pausedPanel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var pausedHost = new Window { Owner = owner, Width = 820, Height = 720, Content = pausedPanel }; pausedHost.Show();
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
        var steerHost = new Window { Owner = owner, Width = 820, Height = 720, Content = steerPanel }; steerHost.Show();
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
