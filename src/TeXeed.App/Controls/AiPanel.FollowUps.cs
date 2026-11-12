using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Text.Json;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private bool _followUpSaving, _queueDispatching;
    private AiConversation? _queueDispatchConversation;
    private readonly SemaphoreSlim _followUpGate = new(1, 1);
    private TaskCompletionSource? _followUpIdle;
    private TaskCompletionSource? _queueDispatchIdle;
    private TaskCompletionSource? _conversionIdle;
    private bool _followUpsClosing;

    private async Task StopFollowUpsAsync()
    {
        _followUpsClosing = true;
        _operation?.Cancel();
        if (_followUpIdle is { } pending) await pending.Task;
        if (_conversionIdle is { } conversion) await conversion.Task;
        if (_queueDispatchIdle is { } dispatch) await dispatch.Task;
        await _followUpGate.WaitAsync();
        _followUpGate.Release();
    }

    private async Task<bool> MutateFollowUpsAsync(AiNotebook book, Func<AiFollowUpQueue, bool> change)
    {
        var conversationId = book.ActiveConversationId;
        await _followUpGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(book, _book) || book.ActiveConversationId != conversationId)
                throw new InvalidOperationException("会话已切换，消息已保留在原会话。");
            var conversation = book.Active;
            var hadOriginal = conversation.AdditionalData?.TryGetValue(AiFollowUpQueue.StorageKey, out _) == true;
            var original = hadOriginal ? conversation.AdditionalData![AiFollowUpQueue.StorageKey] : default;
            var queue = AiFollowUpQueue.Load(conversation);
            if (!change(queue)) return false;
            queue.Store(conversation);
            var hasWritten = conversation.AdditionalData?.TryGetValue(AiFollowUpQueue.StorageKey, out _) == true;
            var written = hasWritten ? conversation.AdditionalData![AiFollowUpQueue.StorageKey] : default;
            try { await SaveAsync(); }
            catch
            {
                var hasCurrent = conversation.AdditionalData?.TryGetValue(AiFollowUpQueue.StorageKey, out _) == true;
                if (hasCurrent == hasWritten && (!hasCurrent || conversation.AdditionalData![AiFollowUpQueue.StorageKey].GetRawText() == written.GetRawText()))
                {
                    var extensions = conversation.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(conversation.AdditionalData);
                    if (hadOriginal) extensions[AiFollowUpQueue.StorageKey] = original;
                    else extensions.Remove(AiFollowUpQueue.StorageKey);
                    conversation.AdditionalData = extensions.Count == 0 ? null : extensions;
                }
                throw;
            }
            if (ReferenceEquals(book, _book)) RefreshFollowUps();
            return true;
        }
        finally { _followUpGate.Release(); }
    }

    private AiSteeringController CreateSteeringController(AiNotebook book, string turnId) => new(
        (ids, _) => Dispatcher.InvokeAsync(async () =>
        {
            if (!await MutateFollowUpsAsync(book, queue => queue.MarkDispatching(ids, turnId)))
                throw new InvalidOperationException("引导消息状态已改变。");
        }).Task.Unwrap(),
        (ids, _) => Dispatcher.InvokeAsync(async () =>
        {
            if (!await MutateFollowUpsAsync(book, queue => queue.Complete(ids)))
                throw new InvalidOperationException("无法确认引导消息。");
        }).Task.Unwrap());

    private async void Steer_Click(object sender, RoutedEventArgs e) => await AddFollowUpAsync("steer");
    private async void Queue_Click(object sender, RoutedEventArgs e) => await AddFollowUpAsync("queue");

    private async Task AddFollowUpAsync(string kind)
    {
        if (_project is null || _operation is null || _followUpsClosing || _followUpSaving || _formGradeCommitPending || kind == "steer" && (_steering is null || _foldingLiveTurn is null)) return;
        if (BlockPendingFormAttachmentRead(_book.Active)) return;
        var draft = Composer.Text;
        var prompt = draft.Trim();
        var parts = _book.Entries.Select(e => (Entry: e, Reference: e.HasReference && e.ReferenceSelected, Attachment: e.HasAttachments && e.AttachmentSelected)).Where(x => x.Reference || x.Attachment).ToArray();
        var entries = parts.Select((x, i) => SnapshotSelectedParts(x.Entry, x.Reference, x.Attachment, i + 1)).ToList();
        if (prompt.Length == 0 && entries.Count == 0) { StatusText.Text = "请输入消息，或选择引用及附件。"; return; }
        if (prompt.Length == 0 && entries.Any(e => string.IsNullOrWhiteSpace(e.Question))) { StatusText.Text = "请在编辑栏描述要问的问题。"; return; }
        if (entries.Any(e => e.HasUnresolvedAttachmentMetadata) || entries.Count > 20 || entries.Sum(e => e.Sources.Sum(s => s.Text.Length) + e.Question.Length + (e.AnswerAnchor?.Quote.Length ?? 0)) + prompt.Length > 80000 || entries.Sum(e => e.PdfBlocks.Sum(p => p.ImageBase64.Length)) > 24_000_000)
        { StatusText.Text = "引用或附件无法安全提交，或超过单批大小限制；请检查后重试。"; return; }
        var book = _book;
        var controller = _steering;
        _followUpIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _followUpSaving = true; Composer.IsReadOnly = true; Entries.IsEnabled = false; Attachments.IsEnabled = false; SteerButton.IsEnabled = false; QueueButton.IsEnabled = false;
        try
        {
            AiFollowUpMessage? item = null;
            var targetTurnId = kind == "steer" ? _foldingLiveTurn!.Id : null;
            await MutateFollowUpsAsync(book, queue => { item = queue.Enqueue(kind, prompt, entries, targetTurnId); return true; });
            if (Composer.Text == draft) { DetachUnsentMentions(parts.Where(x => x.Attachment).Select(x => x.Entry)); _editingMention = true; try { Composer.Clear(); } finally { _editingMention = false; } }
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (book.Entries.Contains(part.Entry) && JsonSerializer.Serialize(SnapshotSelectedParts(part.Entry, part.Reference, part.Attachment, i + 1)) == JsonSerializer.Serialize(entries[i]))
                    ConsumeSelectedParts(book, part.Entry, part.Reference, part.Attachment);
            }
            Rebind();
            await SaveAsync();
            if (kind == "steer")
            {
                if (controller?.TryEnqueue(new AiSteeringMessage(item!.Id, item.Prompt, item.Entries)) == true)
                {
                    _followUpBuffer?.Add(new("steer-" + item.Id, "activity", "引导：" + (prompt.Length > 0 ? prompt : "引用与附件")));
                    StatusText.Text = "引导已保存，等待当前任务交接。";
                }
                else { await MutateFollowUpsAsync(book, queue => queue.MarkUnsubmittedSteers(targetTurnId!)); StatusText.Text = "原轮次已结束；引导已保留，请检查后处理。"; }
            }
            else StatusText.Text = "消息已排队。";
            RefreshFollowUps();
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _followUpSaving = false; Composer.IsReadOnly = false; Entries.IsEnabled = true; Attachments.IsEnabled = true;
            QueueButton.IsEnabled = _operation is not null; SteerButton.IsEnabled = _operation is not null && _steering is not null;
            var idle = _followUpIdle; _followUpIdle = null; idle?.TrySetResult();
            if (!_followUpsClosing && !_disposed && _operation is null) _ = DispatchNextQueuedAsync();
        }
    }

    private async void RestoreFollowUps()
    {
        try { if (_operation is null) await MutateFollowUpsAsync(_book, queue => queue.RecoverWithoutActiveTurn()); RefreshFollowUps(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshFollowUps()
    {
        if (_project is null) return;
        var queue = AiFollowUpQueue.Load(_book.Active);
        var items = queue.Items;
        FollowUpDock.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FollowUpHeading.Text = queue.IsPaused ? "待处理消息 · 已暂停" : "待处理消息";
        FollowUpHeading.ToolTip = queue.IsPaused ? queue.PauseReason : null;
        ResumeQueueButton.Visibility = queue.IsPaused && items.Any(x => x.Kind == "queue" && x.State == "pending") ? Visibility.Visible : Visibility.Collapsed;
        FollowUpItems.Children.Clear();
        foreach (var item in items)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 0) };
            void AddButton(string text, string tip, Func<Task> action, bool enabled = true)
            {
                var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0), IsEnabled = enabled && item.State != "dispatching" };
                button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { ShowError(ex); } };
                DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
            }
            AddButton("×", "删除这条待处理消息", async () => await ChangeFollowUpAsync(q => q.Remove(item.Id)), item.Kind != "steer" || item.State != "pending");
            if (item.State is "unknown" or "failed") AddButton("重试", item.Kind == "steer" ? "将此消息作为下一轮排队发送" : "确认后再次发送", async () => { await ChangeFollowUpAsync(q => item.Kind == "steer" ? q.RetryAsQueue(item.Id) : q.Retry(item.Id)); });
            if (item.State == "pending" && item.Kind == "queue")
            {
                if (_operation is not null && _steering is not null && _foldingLiveTurn is not null)
                    AddButton("转为引导", "将排队消息交给当前任务", async () => await ConvertQueuedToSteerAsync(item.Id));
                AddButton("↓", "下移", async () => await ChangeFollowUpAsync(q => q.Move(item.Id, 1)));
                AddButton("↑", "上移", async () => await ChangeFollowUpAsync(q => q.Move(item.Id, -1)));
                AddButton("编辑", "编辑消息文字", async () =>
                {
                    var editor = new TextBox { Text = item.Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 45 };
                    var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
                    save.Click += async (_, _) => { try { await ChangeFollowUpAsync(q => q.Edit(item.Id, editor.Text, item.Entries)); } catch (Exception ex) { ShowError(ex); } };
                    row.Children.Clear(); var panel = new StackPanel(); panel.Children.Add(editor); panel.Children.Add(save); row.Children.Add(panel); editor.Focus();
                    await Task.CompletedTask;
                });
            }
            var label = new TextBlock { Text = (item.Kind == "steer" ? "引导" : "排队") + " · " + (item.State switch { "dispatching" => "发送中", "unknown" => "结果未知", "failed" => "失败", _ => "等待" }) + "  " + (item.Prompt.Length > 0 ? item.Prompt : "引用与附件") + (item.Entries.Count > 0 ? $"  [引用/附件 {item.Entries.Count}]" : ""), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Error ?? item.Prompt };
            row.Children.Add(label); FollowUpItems.Children.Add(row);
        }
    }

    private async Task ChangeFollowUpAsync(Func<AiFollowUpQueue, bool> change)
    {
        await MutateFollowUpsAsync(_book, change);
    }

    private async Task ConvertQueuedToSteerAsync(string id)
    {
        if (_followUpsClosing || _conversionIdle is not null || _operation is null || _steering is null || _foldingLiveTurn is null) return;
        if (BlockPendingFormAttachmentRead(_book.Active)) return;
        _conversionIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
        var book = _book;
        var controller = _steering;
        var turnId = _foldingLiveTurn.Id;
        AiFollowUpMessage? item = null;
        if (!await MutateFollowUpsAsync(book, queue => (item = queue.ConvertToSteer(id, turnId)) is not null)) return;
        if (controller.TryEnqueue(new AiSteeringMessage(item!.Id, item.Prompt, item.Entries)))
        {
            _followUpBuffer?.Add(new("steer-" + item.Id, "activity", "引导：" + (item.Prompt.Length > 0 ? item.Prompt : "引用与附件")));
            StatusText.Text = "排队消息已转为引导，等待当前任务交接。";
        }
        else
        {
            await MutateFollowUpsAsync(book, queue => queue.MarkUnsubmittedSteers(turnId));
            StatusText.Text = "原轮次已结束；引导已保留，请检查后处理。";
        }
        }
        finally { var idle = _conversionIdle; _conversionIdle = null; idle?.TrySetResult(); }
    }

    private async Task PauseFollowUpsAfterFailureAsync(AiNotebook book)
    {
        var conversationId = book.ActiveConversationId;
        if (_followUpsClosing || _disposed || !ReferenceEquals(book, _book)) return;
        try
        {
            await MutateFollowUpsAsync(book, queue =>
            {
                if (queue.Items.Count == 0 && !_followUpSaving) return false;
                queue.Pause("上一轮未完成，排队已暂停。");
                return true;
            });
        }
        catch (InvalidOperationException) when (_followUpsClosing || _disposed || !ReferenceEquals(book, _book) || book.ActiveConversationId != conversationId)
        {
            // Opening the original conversation restores any pending work as paused.
        }
    }

    private async void ResumeQueue_Click(object sender, RoutedEventArgs e)
    {
        try { await MutateFollowUpsAsync(_book, queue => { queue.Resume(); return true; }); await DispatchNextQueuedAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task FinishFollowUpTurnAsync(AiNotebook book, string turnId, AiFollowUpMessage? queued, bool success)
    {
        await MutateFollowUpsAsync(book, queue =>
        {
            var unknownSteers = queue.Items.Where(x => x.Kind == "steer" && x.TargetTurnId == turnId && x.State == "dispatching").Select(x => x.Id).ToArray();
            if (unknownSteers.Length > 0) queue.MarkUnknown(unknownSteers, "引导发送结果未知，请检查会话后决定是否重试。");
            var pendingSteers = queue.MarkUnsubmittedSteers(turnId);
            if (queued is not null)
            {
                if (success) queue.Complete([queued.Id]);
                else queue.MarkUnknown([queued.Id], "排队消息的执行结果未知，请检查会话后决定是否重试。");
            }
            if (!success && queue.Items.Count > 0) queue.Pause("上一轮未完成，排队已暂停。");
            return unknownSteers.Length > 0 || pendingSteers || queued is not null || !success && queue.Items.Count > 0;
        });
    }

    private async Task DispatchNextQueuedAsync()
    {
        if (_queueDispatching || _followUpsClosing || _operation is not null || _disposed || _project is null) return;
        if (BlockPendingFormAttachmentRead(_book.Active)) return;
        _queueDispatching = true;
        _queueDispatchIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var book = _book;
        _queueDispatchConversation = book.Active;
        try
        {
            while (!_disposed && !_followUpsClosing && _operation is null)
            {
                AiFollowUpMessage? item = null;
                await MutateFollowUpsAsync(book, queue =>
                {
                    if (BlockPendingFormAttachmentRead(book.Active)) return false;
                    item = queue.NextQueued();
                    return item is not null && queue.MarkDispatching([item.Id], null);
                });
                if (item is null) break;
                if (_followUpsClosing || _disposed)
                {
                    await MutateFollowUpsAsync(book, after => after.MarkUnknown([item.Id], "会话关闭，消息尚未开始，请确认后重试。"));
                    break;
                }
                await SubmitAsync(queued: item);
                if (await MutateFollowUpsAsync(book, after => after.MarkUnknown([item.Id], "未能确认排队消息是否发送，请检查后重试。"))) break;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _queueDispatching = false; _queueDispatchConversation = null; var idle = _queueDispatchIdle; _queueDispatchIdle = null; idle?.TrySetResult(); }
    }

    private async Task PauseQueueForFormAttachmentAsync(AiNotebook book, bool added)
    {
        try
        {
            var reason = added ? "表单附件已添加；已有排队消息不包含新附件，请检查后决定是否继续。" : "表单附件未能添加，请检查后决定是否继续排队。";
            var paused = await MutateFollowUpsAsync(book, queue =>
            {
                if (!queue.Items.Any(item => item.Kind == "queue" && item.State == "pending")) return false;
                queue.Pause(reason);
                return true;
            });
            if (paused && !_disposed && ReferenceEquals(book, _book)) StatusText.Text = reason;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { if (!_disposed && ReferenceEquals(book, _book)) ShowError(ex); }
    }
}
