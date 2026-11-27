using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private AiTurn? _continuingInputTurn;
    private string? FollowUpTurnId => _foldingLiveTurn?.Id ?? _continuingInputTurn?.Id;

    private void RefreshInputReplyStatus(AiTurnView view)
    {
        view.SetReplyProgress(ReferenceEquals(view.Turn, _continuingInputTurn) ? _followUpBuffer?.PendingStatus.Text : null);
    }

    private bool BelongsToWaitingQuestion(AiEntry entry) =>
        AiFormAttachmentOrigins.TryGet(entry, out var source) && source!.ConversationId == _book.ActiveConversationId &&
        _book.Turns.Any(t => t.Id == source.TurnId && (t.IsWaitingForInput || source.InputSegmentId is not null));

    private AiForms.Submission? InputSubmission(AiTurn turn, out AiEntry[] files)
    {
        files = [];
        if (!turn.IsWaitingForInput || !ReferenceEquals(_book.Turns.FirstOrDefault(t => t.Id == turn.Id), turn)) return null;
        var submission = AiForms.Snapshot(turn);
        if (submission is null) return null;
        if (submission.Attachments is not null)
        {
            files = _book.Entries.Where(e => e.AttachmentSelected && e.HasAttachments &&
                AiFormAttachmentOrigins.TryGet(e, out var source) && source!.ConversationId == _book.ActiveConversationId && source.TurnId == turn.Id &&
                (source.InputSegmentId is null ? turn.InputReplies.Length == 0 : turn.PendingInputSegmentIds!.Contains(source.InputSegmentId))).ToArray();
            AiFormAttachmentOrigins.RecordSelected(submission, files);
        }
        return AiForms.HasCompleteInput(turn, submission) ? submission : null;
    }

    internal bool CanConfirmTurnInput(AiTurn turn) => _operation is null && InputSubmission(turn, out _) is not null;

    internal Task ConfirmTurnInputAsync(AiTurn turn) => ContinueTurnInputAsync(turn, true);
    private Task ResumeConfirmedInputAsync(AiTurn turn) => ContinueTurnInputAsync(turn, false);

    private async Task ContinueTurnInputAsync(AiTurn turn, bool confirm)
    {
        if (_project is null || _operation is not null || _disposed || _queueDispatching || _modelPreferenceSaving || _formUnlockSaving ||
            BlockPendingFormAttachmentRead(_book.Active) || !ReferenceEquals(_book.Turns.FirstOrDefault(t => t.Id == turn.Id), turn)) return;
        if (Settings.IsBusy) { StatusText.Text = "请等待连接完成后再继续。"; return; }
        AiEntry[] files = [];
        var submission = confirm ? InputSubmission(turn, out files) : null;
        if (confirm ? submission is null : !turn.CanRetryInputReply) return;
        if (files.Any(f => f.HasUnresolvedAttachmentMetadata) || files.Length > 20 || submission?.Values.Sum(v => (long)v.Value.Length) > 80000)
        { StatusText.Text = "回答或附件过长，或附件资料不完整，请检查后确认。"; return; }
        var book = _book;
        var success = false;
        await RunOperationAsync(async token =>
        {
            var reply = submission is null ? turn.InputReplies[^1] : new AiInputReply(turn.PendingInputSegmentIds!, submission.Values, submission.Attachments, DateTimeOffset.Now)
                { Files = files.Select((e, i) => SnapshotSelectedParts(e, false, true, i + 1)).ToList() };
            var data = turn.AdditionalData is null ? null : new Dictionary<string, System.Text.Json.JsonElement>(turn.AdditionalData);
            data?.Remove(AiForms.DraftKey);
            var running = turn with { InputAttemptId = Guid.NewGuid().ToString("N"), EndReason = "input-resuming", Incomplete = true, EndedAt = null, AnsweredAt = null, PendingInputSegmentIds = null,
                InputReplies = confirm ? [.. turn.InputReplies, reply] : turn.InputReplies, AdditionalData = data,
                RevisionRequest = turn.RevisionRequest is { } task ? task with { AwaitingReply = false } : null };
            await SaveCompletedFormTurnAsync(book, running, new HashSet<string>(), turn, files);
            foreach (var file in files) file.PropertyChanged -= AnnotationChanged;
            var buffer = new AiStreamBuffer();
            _continuingInputTurn = running;
            _followUpBuffer = buffer;
            StatusText.Text = "";
            Rebind(); RefreshTranscript();
            _steering = CreateSteeringController(book, running.Id);
            SteerButton.IsEnabled = true;
            var generation = _generation;
            var waiting = false;
            var approved = book.Active.Permission;
            var request = running.RevisionRequest;
            var runner = request is { Cancelled: false } && approved != AiPermission.ReadOnly ? CreateRevisionRunner(request, wait => waiting = wait) : null;
            var modelId = turn.Model;
            var effort = turn.ReasoningEffort;
            AiTurnCheckpoint? journal = null;
            string? endReason = null;
            IReadOnlySet<string> grading = new HashSet<string>();
            try
            {
                journal = await AiTurnCheckpoint.StartAsync(_project!, book.Active.Id, book.Active.Title, running, buffer, scoped: _store is not null);
                if (runner is not null) await SaveUserDraftAsync(request!.TargetTurnId, token);
                if (Settings.AvailableModels.Count == 0) await Settings.LoadModelsAsync(token);
                InitializeConversationModel();
                modelId = ConversationModelId ?? modelId;
                effort = ConversationEffort;
                var model = Settings.AvailableModels.FirstOrDefault(x => x.Id == modelId) ?? throw new InvalidOperationException("当前会话选择的模型暂不可用。");
                if (!AiEffort.Choices(model).Any(x => x.Value == effort)) throw new InvalidOperationException("当前推理强度暂不可用。");
                var selection = new AiForms.Submission(turn.Id, reply.Values) { Attachments = reply.Attachments };
                var controls = string.Join("\n", running.Segments.Where(s => reply.SegmentIds.Contains(s.Id)).Select(s => s.Text));
                var batch = new List<AiEntry> { new() { Title = "统一问题", Question = "继续本轮原任务。以下是已显示的题面（助手原文，仅供定位）：\n" + controls + "\n用户确认的作答：\n" + AiForms.Describe(selection) } };
                batch.AddRange(reply.Files);
                var steers = AiFollowUpQueue.Load(book.Active).History;
                var history = book.Active.InheritedTurns.Concat(book.Turns.TakeWhile(t => t.Id != turn.Id)).Append(running)
                    .Select(t => WithSteeringContext(t, steers)).ToArray();
                var projectFiles = ProjectFiles?.Invoke().ToArray() ?? [];
                var toolRunner = CreateToolRunner?.Invoke();
                await Task.Run(() => new AiResponses(_connection).RunAsync(model.Id, batch, history, projectFiles, _ => { }, _ => { },
                    token, effort, toolRunner, evt =>
                    {
                        buffer.Add(evt);
                        if (evt.Kind == "status")
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (_generation == generation && !_disposed && ReferenceEquals(_continuingInputTurn, running) &&
                                    _turnViews.TryGetValue(running.Id, out var view))
                                    RefreshInputReplyStatus(view);
                            });
                    }, permission: approved, contextWindow: model.ContextWindow, memory: book.Active.ContextMemory, steering: _steering,
                    usage: measured => Dispatcher.BeginInvoke(() =>
                    {
                        if (_generation == generation && !_disposed) { book.Active.ContextUsage = measured; UpdateContextUsage(); }
                    }),
                    proofread: AiMathProofreading.CheckAsync, resuming: !confirm, isBranch: book.Active.ParentConversationId is not null,
                    formSourceIds: history.Select(t => t.Id).ToHashSet(StringComparer.Ordinal), formGradingAvailable: ids => grading = ids,
                    revisionRunner: runner, revisionNotice: request is not null && runner is null ? "当前会话只读，仅讨论改稿。" : null,
                    waitForInput: () => waiting = true), token);
            }
            catch (Exception ex)
            {
                endReason = token.IsCancellationRequested ? "user" : "program";
                buffer.Add(new("failure", "activity", token.IsCancellationRequested ? "已停止继续本轮。" : "本轮未完成：" + ex.Message));
            }
            if (journal is not null) await journal.StopAsync();
            var fresh = buffer.Snapshot();
            foreach (var segment in fresh) segment.Id = running.InputAttemptId + "-" + segment.Id;
            var combined = running.Segments.Concat(fresh).ToList();
            var now = DateTimeOffset.Now;
            var next = running with { InputAttemptId = null, Segments = combined, Answer = string.Join("\n\n", combined.Where(s => s.Kind == "final").Select(s => s.Text)),
                EndReason = endReason, AnsweredAt = endReason is null ? now : null, EndedAt = now, Incomplete = endReason is not null,
                RevisionRequest = request is null ? null : request with { AwaitingReply = endReason is null && waiting }, Model = modelId, ReasoningEffort = effort };
            next = AttachSteeringHistory(next, AiFollowUpQueue.Load(book.Active).History);
            if (endReason is null && waiting) next = AiTurnInput.WithQuestion(next, fresh);
            next = AiTurnInput.AppendToRevisions(book.Active, running, next);
            await SaveCompletedFormTurnAsync(book, next, grading, running);
            journal?.Commit();
            RefreshTranscript(); Rebind();
            await FinishFollowUpTurnAsync(book, turn.Id, null, endReason is null);
            success = endReason is null;
            StatusText.Text = "";
        });
        if (!success) await PauseFollowUpsAfterFailureAsync(book);
        else if (!_disposed) _ = DispatchNextQueuedAsync();
    }

    internal async Task CancelTurnInputAsync(AiTurn turn)
    {
        if (!_disposed && _operation is null && turn.IsWaitingForInput && ReferenceEquals(_book.Turns.FirstOrDefault(t => t.Id == turn.Id), turn))
        {
            var finished = turn with { EndReason = "user", EndedAt = DateTimeOffset.Now,
                RevisionRequest = turn.RevisionRequest is { } task ? task with { AwaitingReply = false, Cancelled = true } : null };
            try { await SaveCompletedFormTurnAsync(_book, finished, new HashSet<string>(), turn); RefreshTranscript(); }
            catch (Exception ex) { ShowError(ex); return; }
            _ = DispatchNextQueuedAsync();
        }
    }
}
