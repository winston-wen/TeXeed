using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.Core;

namespace TeXeed.App.Controls;

public partial class AiPanel : UserControl, IDisposable
{
    private readonly AiConnection _connection;
    private readonly bool _ownsSettings;
    private readonly EventHandler _settingsChanged;
    private AiWorkspaceStore? _store;
    internal string ConversationId => _book.ActiveConversationId;
    internal AiConversation Conversation => _book.Active;
    private readonly ScrollViewer MessagesScroll;
    private readonly AutoScrollController _autoScroll;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly SemaphoreSlim _saveGate = new(1);
    private AiNotebook _book = new();
    private readonly ObservableCollection<AiTurn> _visibleTurns = [];
    private readonly ObservableCollection<object> _transcriptItems = [];
    private readonly ObservableCollection<AiSegment> _liveSegments = [];
    internal Func<AiToolRunner>? CreateToolRunner { get; set; }
    internal HashSet<string> ShellApprovals => _book.Active.ShellApprovals;
    internal AiPermission Permission => _book.Active.Permission;
    private string? _project;
    private bool _updating, _disposed;
    public AiSettingsPanel Settings { get; }
    public event EventHandler? SettingsRequested;
    private int _generation;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationDone;
    private AiTurn? _foldingLiveTurn;
    private readonly ChatOrdinalAlignment _liveOrdinalAlignment;
    public Func<IReadOnlyList<ProjectFile>>? ProjectFiles { get; set; }
    public AiPanel() : this(new AiConnection()) { }
    internal AiPanel(AiConnection connection, AiSettingsPanel? sharedSettings = null)
    {
        _connection = connection; InitializeComponent(); InitializeComposer(); InitializeSharedDocument();
        LiveTurnNumber.FontFamily = LiveQuestion.FontFamily;
        LiveTurnNumber.FontWeight = FontWeights.Normal;
        LiveSentTime.Focusable = true; LiveSentTime.Cursor = Cursors.Hand;
        LiveTurnNumber.Margin = new Thickness(-40, 8 + (LiveQuestion.FontFamily.Baseline - LiveTurnNumber.FontFamily.Baseline) * AiChatTypography.FontSize, 10, 0);
        _liveOrdinalAlignment = new ChatOrdinalAlignment(LiveQuestion, LiveTurnNumber);
        LiveSentTime.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ToggleLiveTurn(); e.Handled = true; } };
        LiveSentTime.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) ToggleLiveTurn(); e.Handled = true; } };
        History.ApplyTemplate();
        MessagesScroll = (ScrollViewer)History.Template.FindName("MessagesScroll", History);
        _autoScroll = new AutoScrollController(MessagesScroll);
        RegisterName(nameof(MessagesScroll), MessagesScroll);
        // The ItemsPresenter must be the direct ScrollViewer content so WPF can
        // use IScrollInfo. A wrapping StackPanel silently disables virtualization.
        History.Items.Clear(); _transcriptItems.Add(EmptyConversation); _transcriptItems.Add(LiveContainer);
        History.ItemsSource = _transcriptItems; LiveSegments.ItemsSource = _liveSegments;
        _visibleTurns.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            { _transcriptItems.Clear(); _transcriptItems.Add(EmptyConversation); _transcriptItems.Add(LiveContainer); }
            else if (change.NewItems is not null)
                for (var i = 0; i < change.NewItems.Count; i++) _transcriptItems.Insert(change.NewStartingIndex + i + 1, change.NewItems[i]!);
        };
        _ownsSettings = sharedSettings is null; Settings = sharedSettings ?? new AiSettingsPanel(connection);
        PermissionPicker.ItemsSource = AiPermissionChoice.Choices;
        SyncPermission();
        _settingsChanged = (_, _) => SyncModelChoices(); Settings.ConnectionChanged += _settingsChanged;
        SizeChanged += (_, _) =>
        {
            LimitComposerHeight();
            ScheduleReadingContext(); ResizeChoices();
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await SaveAsync(); if (!_disposed) _commentSidebar.Refresh(); } catch (Exception ex) { ShowError(ex); } };
        MessagesScroll.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, MessagesScroll)) return;
            if (!_restoringReading && e.VerticalChange != 0) { _book.Active.ReadingOffset = MessagesScroll.VerticalOffset; Dirty(); }
            ScheduleReadingContext();
        };
        Loaded += async (_, _) => { SyncModelChoices(); if (Settings.SelectedModel is null) await RefreshModelChoicesAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            if (IsSendGesture(e.Key, Keyboard.Modifiers) && Composer.IsKeyboardFocusWithin)
                Composer_KeyDown(this, e);
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            { OpenDocumentSearch(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { MessageSearchPanel.Visibility = Visibility.Visible; MessageSearchBox.Focus(); MessageSearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && MessageSearchPanel.IsVisible)
            { MessageSearchPanel.Visibility = Visibility.Collapsed; Composer.Focus(); e.Handled = true; }
        };
    }
    public async Task SetProjectAsync(string project)
    {
        _operation?.Cancel(); if (_operationDone is { } pending) await pending.Task;
        _generation++; _saveTimer.Stop(); await SaveAsync();
        _store = null;
        var loaded = await AiStorage.LoadAsync(project);
        _project = project; _book = loaded;
        _restoringReading = true;
        _updating = true; Composer.Text = _book.Draft; _updating = false;
        if (_book.RequiresSave || File.Exists(AiTurnCheckpoint.PathFor(project))) await SaveAsync();
        ShowCurrentConversation();
    }
    internal void Bind(AiWorkspaceStore store, string id)
    {
        _store = store; _project = store.Project; _book = store.View(id);
        ShowCurrentConversation();
    }
    private void Dirty() { if (_updating || _disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public async Task SaveAsync(string? resourceRestoreToken = null)
    {
        if (_project is null) return;
        var project = _project;
        _book.Draft = Composer.Text;
        if (!_restoringReading) _book.Active.ReadingOffset = MessagesScroll.VerticalOffset;
        CaptureReadingAnchor();
        if (_store is not null) await _store.SaveAsync();
        else { await _saveGate.WaitAsync(); try { var snapshot = _book.Snapshot(); await Task.Run(() => AiStorage.Save(project, snapshot)); } finally { _saveGate.Release(); } }
        await FlushDocumentVersionAsync(resourceRestoreToken);
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; SubmitButton.IsEnabled = false; StopButton.IsEnabled = true; if (_ownsSettings) Settings.IsEnabled = false; ModelControls.IsEnabled = false; PermissionPicker.IsEnabled = false;
        UpdateRetryActions();
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；本轮已保留，可继续提问。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { SubmitButton.IsEnabled = true; StopButton.IsEnabled = false; if (_ownsSettings) Settings.IsEnabled = true; ModelControls.IsEnabled = true; PermissionPicker.IsEnabled = true; UpdateRetryActions(); } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。问题已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void RefreshTranscript()
    {
        // Keep existing document visuals when appending a follow-up. Never bind the mutable
        // notebook List directly: WPF's item generator needs collection notifications.
        if (_visibleTurns.Count > _book.Turns.Count || !_visibleTurns.SequenceEqual(_book.Turns.Take(_visibleTurns.Count)))
            _visibleTurns.Clear();
        foreach (var turn in _book.Turns.Skip(_visibleTurns.Count)) _visibleTurns.Add(turn);
        EmptyConversation.Visibility = _book.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveContainer.Visibility = Visibility.Collapsed;
        _liveSegments.Clear();
        UpdateRetryActions();
        MessageSearch_Changed(MessageSearchBox, null!);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void Composer_Changed(object sender, TextChangedEventArgs e) { if (!_updating) _book.Draft = Composer.Text; Dirty(); }
    private async void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSendGesture(e.Key, Keyboard.Modifiers)) return;
        e.Handled = true;
        if (!e.IsRepeat) await SubmitAsync();
    }
    internal static bool IsSendGesture(Key key, ModifierKeys modifiers) => key == Key.Enter && modifiers == ModifierKeys.Control;
    private async void Submit_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    private async void SharedRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_book.Turns.LastOrDefault() is not { IsIncomplete: true } last) return;
        if (last.MentorRequest is not null) { await SubmitAsync(last); return; }
        var prompt = ConfirmLegacyResend(last);
        if (prompt is not null) await SubmitAsync(currentDocumentPrompt: prompt);
    }
    private string? ConfirmLegacyResend(AiTurn turn)
    {
        var input = new TextBox { Text = turn.Prompt ?? turn.DisplayQuestion, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinWidth = 360, MinHeight = 90, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var send = new Button { Content = "用当前文稿发送", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(send);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "这条旧记录没有保存当时的文稿和评论快照，无法原样重试。可以检查原问题后，使用当前文稿和评论重新发送；已有草稿会保留。", TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(input); content.Children.Add(buttons);
        var dialog = NewDocumentDialog("重新发送旧问题", 500, 290); dialog.Content = content;
        send.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => send.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        send.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    internal async Task SubmitAsync(AiTurn? retry = null, string? currentDocumentPrompt = null)
    {
        if (_project is null || _operation is not null) return;
        if (retry is not null && (_book.Turns.LastOrDefault()?.Id != retry.Id || !retry.IsIncomplete)) return;
        if (Settings.IsBusy) { StatusText.Text = "AI 设置正在连接，请等待完成或在设置中取消。"; return; }
        var submittedDraft = Composer.Text;
        var prompt = retry is null ? (currentDocumentPrompt ?? submittedDraft).Trim() : retry.Prompt ?? retry.DisplayQuestion;
        if (prompt.Length == 0) { StatusText.Text = "请输入附言。"; return; }
        if (retry is not null && retry.MentorRequest is null) { StatusText.Text = "此轮没有保存文稿请求快照，无法安全重试，请重新提问。"; return; }
        await RunOperationAsync(async token =>
        {
            var request = retry?.MentorRequest?.Snapshot() ?? CaptureMentorRequest();
            var frozen = new AiSharedDocument { Source = request.Source, InitialSource = request.InitialSource };
            var isInitial = frozen.Source.Length == 0 && frozen.InitialSource.Length == 0;
            var documentContext = AiMentorProtocol.MakeContextEntry(frozen, request.References);
            var batch = new List<AiEntry> { new() { Title = "统一问题", Question = prompt }, documentContext };
            var generation = _generation; var book = _book;
            var sentAt = DateTimeOffset.Now;
            DateTimeOffset? answeredAt = null;
            var permission = book.Active.Permission;
            var question = prompt;
            // Name the conversation as soon as the first valid question is accepted. The
            // initial save below persists it before any model request can fail or stall.
            if (book.Turns.Count == 0 && book.Active.Title == "新对话") book.Active.Title = AiConversation.MakeTitle(question);
            book.Active.UpdatedAt = DateTimeOffset.UtcNow;
            var buffer = new AiStreamBuffer();
            var pendingTurn = new AiTurn(question, "[本轮未完成]") { CreatedAt = sentAt, SentAt = sentAt, Incomplete = true, Prompt = prompt, MentorRequest = request };
            SharedDocumentView.SetQuestion(book.Turns.FirstOrDefault()?.Question ?? question, Settings.SelectedModel?.Name ?? "AI");
            _readingRestoreVersion++; _restoringReading = false;
            _liveSegments.Clear(); LiveQuestion.Markdown = question;
            LiveTurnNumber.Text = $"[{book.Turns.Count + 1}]";
            _liveOrdinalAlignment.Schedule();
            _foldingLiveTurn = pendingTurn; LiveSegments.Visibility = Visibility.Visible;
            LiveSentTime.Text = pendingTurn.SentTime; LiveReplyStatus.IsCollapsed = false;
            LiveReplyStatus.PendingText = "等待 AI 响应";
            SharedReplyStatus.PendingText = "等待 AI 响应"; SharedReplyStatus.Visibility = Visibility.Visible;
            SharedSubmittedText.Text = SubmittedPreview(question); SharedSubmittedMessage.Visibility = Visibility.Visible;
            LiveContainer.Visibility = Visibility.Visible; EmptyConversation.Visibility = Visibility.Collapsed;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (_foldingLiveTurn == pendingTurn && !_disposed) RevealLiveTurn(); }));
            AiTurnCheckpoint checkpoint;
            try { checkpoint = await AiTurnCheckpoint.StartAsync(_project!, book.Active.Id, book.Active.Title, pendingTurn, buffer, scoped: _store is not null); }
            catch { SharedSubmittedMessage.Visibility = Visibility.Collapsed; SharedSubmittedText.Text = ""; SharedReplyStatus.Visibility = Visibility.Collapsed; LiveContainer.Visibility = Visibility.Collapsed; _foldingLiveTurn = null; throw; }
            book.Active.LastSentAt = sentAt;
            if (retry is null && currentDocumentPrompt is null && Composer.Text == submittedDraft) Composer.Clear(); StatusText.Text = "";
            var renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            long renderedVersion = -1;
            long renderedStatusVersion = -1;
            var previewEnabled = isInitial;
            void Flush()
            {
                if (_generation != generation || _disposed) return;
                var pendingStatus = buffer.PendingStatus;
                if (pendingStatus.Version != renderedStatusVersion)
                {
                    renderedStatusVersion = pendingStatus.Version;
                    LiveReplyStatus.PendingText = pendingStatus.Text;
                    SharedReplyStatus.PendingText = pendingStatus.Text;
                }
                var version = buffer.Version;
                if (version == renderedVersion) return;
                renderedVersion = version;
                var follow = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 40;
                var snapshot = buffer.Snapshot();
                if (previewEnabled && book.Active.SharedDocument.Revision == frozen.Revision)
                {
                    var partial = snapshot.LastOrDefault(s => s.Kind is "text" or "final")?.Text;
                    if (!string.IsNullOrEmpty(partial)) SharedDocumentView.PreviewStreaming(partial);
                }
                foreach (var stale in _liveSegments.Where(s => snapshot.All(current => current.Id != s.Id)).ToArray()) _liveSegments.Remove(stale);
                foreach (var segment in snapshot)
                {
                    segment.ReferenceTime = sentAt;
                    var existing = _liveSegments.FirstOrDefault(s => s.Id == segment.Id);
                    if (existing is null) _liveSegments.Add(segment);
                    else { if (existing.Text != segment.Text) existing.Text = segment.Text; if (existing.Kind != segment.Kind) existing.Kind = segment.Kind; existing.CreatedAt = segment.CreatedAt; existing.Details = segment.Details; }
                }
                if (follow) MessagesScroll.ScrollToEnd();
            }
            renderTimer.Tick += (_, _) => Flush(); Flush(); renderTimer.Start();
            var modelId = ""; var modelName = ""; var effort = ""; string? endReason = null;
            try
            {
                await SaveAsync();
                if (Settings.SelectedModel is null) await Settings.LoadModelsAsync(token);
                if (Settings.SelectedModel is not AiModel model) throw new InvalidOperationException("请选择可用模型。");
                modelId = model.Id; modelName = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name; effort = Settings.SelectedEffort;
                SharedDocumentView.SetQuestion(book.Turns.FirstOrDefault()?.Question ?? question, modelName);
                await Settings.SaveSelectionAsync(token);
                var files = ProjectFiles?.Invoke().ToArray() ?? [];
                var toolRunner = CreateToolRunner?.Invoke();
                var history = retry is null ? [] : new[] { retry };
                await Task.Run(() => new AiResponses(_connection).RunAsync(model.Id, batch, history, files,
                    _ => { }, _ => { }, token, effort, toolRunner, evt =>
                    {
                        buffer.Add(evt);
                    }, permission: permission, usage: measured => Dispatcher.BeginInvoke(() =>
                    {
                        if (_generation == generation && !_disposed) { book.Active.ContextUsage = measured; UpdateContextUsage(); }
                    }), contextWindow: model.ContextWindow, instructionsOverride: AiMentorProtocol.Instructions(permission, toolRunner is not null, isInitial), resuming: retry is not null, classifyCommentary: true, preserveDocumentContext: true), token);
                token.ThrowIfCancellationRequested();
                previewEnabled = false;
                var raw = buffer.Snapshot().LastOrDefault(s => s.Kind == "final")?.Text ?? "";
                if (!AiMentorProtocol.TryPrepare(raw, frozen, request.References, out var plan, out var error)) throw new InvalidOperationException(error);
                ApplyMentorPlan(book, frozen, request.References, plan!, modelName);
                answeredAt = DateTimeOffset.Now;
                renderTimer.Stop(); Flush();
                SharedDocumentView.RefreshDocument();
                _commentSidebar.Refresh();
                SharedDocumentChanged?.Invoke(this, EventArgs.Empty);
                SharedCommentsChanged?.Invoke(this, EventArgs.Empty);
                try
                {
                    await SaveAsync();
                    await SaveSharedVersionAsync(modelName, "AI 更新共同文稿");
                }
                catch (Exception ex) { StatusText.Text = "文稿已更新，但保存或版本记录失败：" + ex.Message; }
            }
            catch (Exception ex)
            {
                previewEnabled = false;
                if (ex is AiContextLimitException && retry is null && currentDocumentPrompt is null && Composer.Text.Length == 0) Composer.Text = submittedDraft;
                endReason = token.IsCancellationRequested ? "user" : "program";
                var unapplied = buffer.Snapshot().LastOrDefault(s => s.Kind is "final" or "text")?.Text;
                SharedDocumentView.RefreshDocument();
                if (!string.IsNullOrEmpty(unapplied)) SharedDocumentView.ShowUnapplied(unapplied, token.IsCancellationRequested ? "生成已中止，以下内容尚未应用。" : "以下 AI 内容未应用：" + ex.Message);
                StatusText.Text = token.IsCancellationRequested ? "生成已中止，可重试。" : "本轮未完成：" + ex.Message;
                buffer.Add(new("failure", "activity", token.IsCancellationRequested ? "已停止。已完成的修改保留；可继续提问。" : "本轮未完成：" + ex.Message));
            }
            finally
            {
                var endedAt = answeredAt ?? DateTimeOffset.Now;
                SharedReplyStatus.Visibility = Visibility.Collapsed;
                SharedSubmittedMessage.Visibility = Visibility.Collapsed; SharedSubmittedText.Text = "";
                renderTimer.Stop();
                await checkpoint.StopAsync();
                if (_generation == generation)
                {
                    Flush();
                    var segments = buffer.Snapshot();
                    var answer = string.Join("\n\n", segments.Where(s => s.Kind == "final").Select(s => s.Text));
                    if (answer.Length == 0) answer = "[本轮未完成]\n" + string.Join("\n", segments.Where(s => s.Kind == "text" || s.Id == "failure").Select(s => s.Text));
                    book.Turns.Add(pendingTurn with { Answer = answer, AnsweredAt = answeredAt, EndedAt = endedAt, Incomplete = answeredAt is null, EndReason = endReason, Model = modelId, ReasoningEffort = effort, Segments = segments });
                    book.Active.UpdatedAt = DateTimeOffset.UtcNow;
                    // Save the completed exchange before constructing its rich document.
                    await SaveAsync(); checkpoint.Commit(); RefreshTranscript();
                }
                if (ReferenceEquals(_foldingLiveTurn, pendingTurn)) _foldingLiveTurn = null;
            }
        });
    }
    private AiMentorRequestSnapshot CaptureMentorRequest() => AiMentorProtocol.CaptureRequest(_book.Active);

    internal static string SubmittedPreview(string text)
    {
        const int limit = 600;
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
        return text[..length] + "…（其余略）";
    }

    internal static void ApplyMentorPlan(AiNotebook book, AiSharedDocument frozen, IReadOnlyList<AiMentorReference> references, AiMentorPlan plan, string modelName)
    {
        var live = book.Active.SharedDocument;
        var before = live.Snapshot();
        var document = before.Snapshot();
        foreach (var note in plan.Notes.Where(n => n.ReplyToCommentId.Length > 0))
        {
            var conversation = note.ConversationId == book.Active.Id ? book.Active : null;
            var current = conversation?.SharedDocument.Comments.FirstOrDefault(c => c.Id == note.ReplyToCommentId);
            var original = references.FirstOrDefault(r => r.ConversationId == note.ConversationId && r.Comment.Id == note.ReplyToCommentId)?.Comment;
            if (current is null || original is null || !SameDiscussion(current, original))
                throw new InvalidOperationException("回复所关联的评论已变化或删除，AI 内容未应用；请重新发送。");
        }
        var initial = frozen.Source.Length == 0 && frozen.InitialSource.Length == 0;
        var applied = initial ? document.TryInitialize(plan.InitialSource, modelName, "ai") :
            document.TryApplyEdits(frozen.Revision, plan.Edits.Select(item => item.Edit).ToArray(), modelName, "ai");
        if (!applied) throw new InvalidOperationException("文稿在生成期间发生变化，AI 内容未应用；请重新发送。");
        void AddComment(int start, int length, string quote, string text)
        {
            document.Comments.Add(new AiDocumentComment { Start = start, Length = length, Quote = quote, AnchorRevision = document.Revision, Text = text, Author = modelName, Role = "ai" });
        }
        var offset = 0;
        foreach (var item in plan.Edits.OrderBy(item => item.Edit.Start))
        {
            var edit = item.Edit;
            if (!string.IsNullOrWhiteSpace(item.Reason))
                AddComment(edit.Text.Length == 0 ? 0 : edit.Start + offset, edit.Text.Length, edit.Text, item.Reason);
            offset += edit.Text.Length - edit.Length;
        }
        foreach (var note in plan.Notes)
        {
            if (note.ReplyToCommentId.Length > 0)
            {
                var target = document.Comments.First(c => c.Id == note.ReplyToCommentId);
                target.Replies.Add(new AiDocumentReply { Author = modelName, Role = "ai", Text = note.Text });
            }
            else AddComment(document.Source.IndexOf(note.Quote, StringComparison.Ordinal), note.Quote.Length, note.Quote, note.Text);
        }
        var editedNote = !AiDocumentPostscript.SameBlock(frozen.Source, document.Source);
        if (!AiDocumentPostscript.TryUpdate(document, plan.Message, modelName, editedNote, out var noteError))
            throw new InvalidOperationException(noteError);
        if (!live.TryRestore(before, document))
            throw new InvalidOperationException("文稿或评论在生成期间发生变化，AI 内容未应用；请重新发送。");
    }
    private static bool SameDiscussion(AiDocumentComment a, AiDocumentComment b) =>
        a.Id == b.Id && a.Author == b.Author && a.Role == b.Role && a.Text == b.Text && a.Quote == b.Quote && a.AnchorRevision == b.AnchorRevision &&
        a.Start == b.Start && a.Length == b.Length && a.Orphaned == b.Orphaned &&
        a.Replies.Count == b.Replies.Count && a.Replies.Zip(b.Replies).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Author == pair.Second.Author && pair.First.Role == pair.Second.Role && pair.First.Text == pair.Second.Text);
    private void ToggleLiveTurn()
    {
        if (_foldingLiveTurn is not { } turn) return;
        turn.Collapsed = !turn.Collapsed;
        LiveSentTime.Text = turn.SentTime;
        LiveSegments.Visibility = turn.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        LiveReplyStatus.IsCollapsed = turn.Collapsed;
        ScheduleReadingContext();
    }
    public async Task StopAndSaveAsync() { _operation?.Cancel(); if (_operationDone is { } pending) await pending.Task; if (_ownsSettings) await Settings.StopAsync(); await SaveAsync(); }
    public void Dispose() { _disposed = true; _documentSearchCancellation?.Cancel(); _documentSearchWindow?.Close(); _autoScroll.Dispose(); _liveOrdinalAlignment.Dispose(); _operation?.Cancel(); _lifetime.Cancel(); _saveTimer.Stop(); Settings.ConnectionChanged -= _settingsChanged; if (_ownsSettings) { Settings.Dispose(); _connection.Dispose(); } _lifetime.Dispose(); }
}
