using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiFormReadRaceAsync(MainWindow owner, string output)
    {
        var project = Path.Combine(output, "form-read-fixture.tex");
        var file = Path.Combine(output, "form-read.txt");
        await File.WriteAllTextAsync(file, "资料");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var conversation = book.Active;
        var source = AiForms.WithSubmission(new AiTurn("上传材料", "{{file:source:核对资料}}"), null);
        conversation.Turns.Add(source);
        var origin = new AiFormAttachmentOrigin(1, conversation.Id, conversation.Title, source.Id, 1, "source", "核对资料");
        var queue = AiFollowUpQueue.Load(conversation);
        var queued = queue.Enqueue("queue", "已排队的旧消息", [], null);
        queue.Store(conversation);
        var host = new Window { Owner = owner, Width = 800, Height = 600, Content = panel }; host.Show();
        var operationField = typeof(AiPanel).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steeringField = typeof(AiPanel).GetField("_steering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var liveField = typeof(AiPanel).GetField("_foldingLiveTurn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CancellationTokenSource? operation = null;
        try
        {
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "保留的用户草稿";
            var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = panel.AddAttachmentsAsync([file], origin, () => true, token => readGate.Task.WaitAsync(token));
            if (!(bool)Invoke(panel, "HasPendingFormAttachmentReads", conversation)!) throw new Exception("Reading state was not set synchronously");
            await panel.SubmitAsync();
            if (composer.Text != "保留的用户草稿" || conversation.Turns.Count != 1 || AiForms.SubmittedFor(conversation, source.Id) is not null)
                throw new Exception("A send during attachment read consumed the draft or froze the form");
            if (!((TextBlock)panel.FindName("StatusText")).Text.Contains("读取", StringComparison.Ordinal))
                throw new Exception("The blocked send did not explain the pending attachment read");
            await CallAsync(panel, "DispatchNextQueuedAsync");
            queue = AiFollowUpQueue.Load(conversation);
            if (queue.Items.Single().State != "pending" || queue.IsPaused) throw new Exception("The blocked queued message was dispatched or marked unknown");

            operation = new CancellationTokenSource(); operationField.SetValue(panel, operation);
            await CallAsync(panel, "AddFollowUpAsync", "queue");
            if (AiFollowUpQueue.Load(conversation).Items.Count != 1 || composer.Text != "保留的用户草稿")
                throw new Exception("Queueing during an attachment read consumed the draft or added a message");
            steeringField.SetValue(panel, Invoke(panel, "CreateSteeringController", book, source.Id)); liveField.SetValue(panel, source);
            await CallAsync(panel, "AddFollowUpAsync", "steer");
            await CallAsync(panel, "ConvertQueuedToSteerAsync", queued.Id);
            if (AiFollowUpQueue.Load(conversation).Items.Single().Kind != "queue") throw new Exception("Steering started while the attachment was reading");
            operationField.SetValue(panel, null); steeringField.SetValue(panel, null); liveField.SetValue(panel, null); operation.Dispose(); operation = null;

            var other = new AiConversation(); book.Conversations.Add(other); book.ActiveConversationId = other.Id;
            if ((bool)Invoke(panel, "HasPendingFormAttachmentReads", other)!) throw new Exception("An unrelated conversation was blocked");
            book.ActiveConversationId = conversation.Id;
            readGate.SetResult();
            if (!await read) throw new Exception("The attachment was not added after reading finished");
            queue = AiFollowUpQueue.Load(conversation);
            if (!queue.IsPaused || queue.Items.Single().State != "pending" || !queue.PauseReason!.Contains("不包含新附件", StringComparison.Ordinal))
                throw new Exception("The old queue snapshot did not pause for review after a new attachment was added");
            if (conversation.Entries.Count != 1 || composer.Text != "保留的用户草稿" || (bool)Invoke(panel, "HasPendingFormAttachmentReads", conversation)!)
                throw new Exception("Completed read did not retain attachment and draft or clear its pending state");

            queue.Remove(queued.Id); queue.Resume(); queue.Store(conversation);
            var failedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = panel.AddAttachmentsAsync([file], origin, () => true, token => failedGate.Task.WaitAsync(token));
            var succeedingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var succeeding = panel.AddAttachmentsAsync([file], origin, () => true, token => succeedingGate.Task.WaitAsync(token));
            var queue2 = AiFollowUpQueue.Load(conversation); queue2.Enqueue("queue", "待检查", [], null); queue2.Store(conversation);
            failedGate.SetException(new IOException("模拟读取失败"));
            if (await failed || !(bool)Invoke(panel, "HasPendingFormAttachmentReads", conversation)!) throw new Exception("One failed read incorrectly released another pending read");
            succeedingGate.SetResult();
            if (!await succeeding) throw new Exception("The second attachment was not added");
            queue2 = AiFollowUpQueue.Load(conversation);
            if (!queue2.IsPaused || !queue2.PauseReason!.Contains("未能添加", StringComparison.Ordinal)) throw new Exception("A failed read did not pause the existing queue for review");

            var canceled = await panel.AddAttachmentsAsync([file], origin, () => true, _ => Task.FromCanceled(new CancellationToken(true)));
            if (canceled || (bool)Invoke(panel, "HasPendingFormAttachmentReads", conversation)! || composer.Text != "保留的用户草稿")
                throw new Exception("A canceled read left the conversation blocked or lost the draft");
            Console.WriteLine("PASS: pending form attachment blocks send, queue and steering; failure and cancellation preserve drafts.");
        }
        finally
        {
            operationField.SetValue(panel, null); steeringField.SetValue(panel, null); liveField.SetValue(panel, null); operation?.Dispose(); host.Close();
        }
    }
}
