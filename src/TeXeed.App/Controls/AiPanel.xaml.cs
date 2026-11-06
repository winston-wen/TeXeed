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
    private bool _mentorGlobalTransaction;
    private readonly Dictionary<(string Project, string Conversation), string> _pendingReceivedReplyVersions = [];
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
        _connection = connection; InitializeComponent(); InitializeComposer(); InitializeBookmarkUi(); InitializeSharedDocument();
        LiveQuestion.Loaded += (_, _) => BookmarkQuestionLoaded(LiveQuestion);
        LiveQuestion.Unloaded += (_, _) => BookmarkQuestionUnloaded(LiveQuestion);
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
            ContextScroll.MaxHeight = Math.Clamp(ActualHeight * .25, 40, 170);
            LimitComposerHeight();
            if (EntriesPopup.Child is FrameworkElement surface) surface.Width = Math.Clamp(ActualWidth - 24, 230, 480);
            ScheduleReadingContext(); ResizeChoices();
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
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
            else if (e.Key == Key.Escape && _bookmarkSurface?.Visibility == Visibility.Visible)
            { CloseBookmarkUi(); _bookmarkBall.Focus(); e.Handled = true; }
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
        foreach (var entry in _book.Entries.Where(e => e.State == "提交中")) entry.State = "上次提交未完成，可重试";
        if (_book.RequiresSave || File.Exists(AiTurnCheckpoint.PathFor(project))) await SaveAsync();
        ShowCurrentConversation();
    }
    internal void Bind(AiWorkspaceStore store, string id)
    {
        _store = store; _project = store.Project; _book = store.View(id);
        ShowCurrentConversation();
    }
    public bool AddEntry(AiEntry entry, bool focusComposer = true)
    {
        if (_project is null) { StatusText.Text = "请先打开项目。"; return false; }
        if (_book.Conversations.Sum(c => c.Entries.Concat(c.Turns.SelectMany(t => t.SentAnnotations)).Sum(e => e.PdfBlocks.Sum(p => (long)p.ImageBase64.Length))) + entry.PdfBlocks.Sum(p => (long)p.ImageBase64.Length) > 44_000_000) { StatusText.Text = "项目问题截图合计最多约 32 MB，请清除不再需要的条目。"; return false; }
        if (_book.Entries.Count >= 100) { StatusText.Text = "最多保留 100 个条目，请清除已完成条目。"; return false; }
        entry.Number = _book.Entries.Count + 1;
        _book.Entries.Add(entry); Rebind(); Dirty();
        if (focusComposer) Composer.Focus();
        return true;
    }
    private void Rebind()
    {
        for (var i = 0; i < _book.Entries.Count; i++) _book.Entries[i].Number = i + 1;
        foreach (var entry in _book.Entries)
        {
            entry.PropertyChanged -= AnnotationChanged; entry.PropertyChanged += AnnotationChanged;
        }
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        foreach (var layer in _answerLayers.Values) layer.Refresh();
        Entries.ItemsSource = null;
        Entries.ItemsSource = _book.Entries.ToArray();
        ContextExpander.Visibility = _book.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ContextExpander.Header = $"圈点与问题条目 · {_book.Entries.Count}";
        EntriesButton.Content = $"圈点 · {_book.Entries.Count}";
    }
    private void Dirty() { if (_updating || _disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public async Task SaveAsync(bool skipGlobalCarry = false)
    {
        if (_project is null) return;
        skipGlobalCarry |= _mentorGlobalTransaction;
        var project = _project;
        var conversation = _book.Active;
        var previousReplies = conversation.SharedDocument.Comments.SelectMany(comment => comment.Replies).Select(reply => reply.Id).ToHashSet();
        if (!skipGlobalCarry && !await WaitForGlobalCarryAsync(() => AiGlobalCarry.Synchronize(project, conversation))) StatusText.Text = "有跨会话回复与当前评论冲突，已保留待处理记录。";
        AiDocumentReply[] addedReplies = skipGlobalCarry ? [] : conversation.SharedDocument.Comments.SelectMany(comment => comment.Replies).Where(reply => !previousReplies.Contains(reply.Id)).ToArray();
        if (addedReplies.Length > 0) _pendingReceivedReplyVersions[(project, conversation.Id)] = addedReplies[^1].Author;
        if (addedReplies.Length > 0 && ReferenceEquals(_book.Active, conversation)) SharedDocumentView.RefreshDocument();
        _book.Draft = Composer.Text;
        if (!_restoringReading) _book.Active.ReadingOffset = MessagesScroll.VerticalOffset;
        CaptureReadingAnchor();
        if (_store is not null) await _store.SaveAsync();
        else { await _saveGate.WaitAsync(); try { var snapshot = _book.Snapshot(); await Task.Run(() => AiStorage.Save(project, snapshot)); } finally { _saveGate.Release(); } }
        await FlushDocumentVersionAsync();
        foreach (var item in _pendingReceivedReplyVersions.Where(item => string.Equals(item.Key.Project, project, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var source = _book.Conversations.FirstOrDefault(c => c.Id == item.Key.Conversation);
            if (source is null) continue;
            await AiDocumentRepository.SaveVersionAsync(project, source.Id, source.SharedDocument, item.Value, "接收跨会话评论回复");
            if (_pendingReceivedReplyVersions.GetValueOrDefault(item.Key) == item.Value) _pendingReceivedReplyVersions.Remove(item.Key);
        }
        if (!skipGlobalCarry) await WaitForGlobalCarryAsync(() => { AiGlobalCarry.AcknowledgePersisted(project, conversation.Id); return true; });
    }
    private static async Task<T> WaitForGlobalCarryAsync<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (AiGlobalCarryBusyException) when (attempt < 1200) { await Task.Delay(25); }
        }
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; SubmitButton.IsEnabled = false; StopButton.IsEnabled = true; if (_ownsSettings) Settings.IsEnabled = false; ModelControls.IsEnabled = false; PermissionPicker.IsEnabled = false; Entries.IsEnabled = false;
        UpdateRetryActions();
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；本轮已保留，可继续提问。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { SubmitButton.IsEnabled = true; StopButton.IsEnabled = false; if (_ownsSettings) Settings.IsEnabled = true; ModelControls.IsEnabled = true; PermissionPicker.IsEnabled = true; Entries.IsEnabled = true; UpdateRetryActions(); } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。问题已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    private void Add_Click(object sender, RoutedEventArgs e) => AddEntry(new());
    private void Remove_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is AiEntry entry) RemoveAnnotation(entry); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var entry in _book.Entries) entry.Selected = true; Rebind(); Dirty(); }
    private void ClearDone_Click(object sender, RoutedEventArgs e) { _book.Entries.RemoveAll(e => e.State == "已完成"); Rebind(); Dirty(); }
    private void Entry_Changed(object sender, RoutedEventArgs e) => Dirty();
    private void Question_Changed(object sender, TextChangedEventArgs e) => Dirty();
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not AiPdfBlock block) return;
        try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 500; bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(block.ImageBase64)); bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap; ImagePreview.Enable(image, $"PDF 第 {block.Page} 页圈选区域", () => ImagePreview.LoadBase64(block.ImageBase64)); }
        catch (Exception) { image.ToolTip = "截图无法读取"; }
    }
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
        if (_book.Turns.LastOrDefault() is { IsIncomplete: true } last) await SubmitAsync(last);
    }
    internal async Task SubmitAsync(AiTurn? retry = null)
    {
        if (_project is null || _operation is not null) return;
        if (retry is not null && (_book.Turns.LastOrDefault()?.Id != retry.Id || !retry.IsIncomplete)) return;
        if (Settings.IsBusy) { StatusText.Text = "AI 设置正在连接，请等待完成或在设置中取消。"; return; }
        var submittedDraft = Composer.Text;
        var prompt = retry is null ? submittedDraft.Trim() : retry.Prompt ?? (retry.SentAnnotations.Count == 0 ? retry.DisplayQuestion : "");
        var selected = retry is null ? _book.Entries.Where(e => e.Selected).ToArray() : retry.SentAnnotations.Select(e => e.Snapshot()).ToArray();
        if (selected.Length == 0 && prompt.Length == 0) { StatusText.Text = "请输入问题，或填写并勾选条目。"; return; }
        if (prompt.Length == 0 && selected.Any(e => string.IsNullOrWhiteSpace(e.Question))) { StatusText.Text = "请在编辑栏描述要问的问题。"; Composer.Focus(); return; }
        if (selected.Length > 20 || selected.Sum(e => e.Sources.Sum(s => s.Text.Length) + e.Question.Length + (e.AnswerAnchor?.Quote.Length ?? 0)) + prompt.Length > 80000 || selected.Sum(e => e.PdfBlocks.Sum(p => p.ImageBase64.Length)) > 24_000_000)
        { StatusText.Text = "每批最多 20 个问题、8 万字符和约 18 MB 截图，请分批提交。"; return; }
        var sentAnnotations = selected.Select((entry, i) => entry.Snapshot(i + 1)).ToList();
        if (retry is not null && retry.MentorRequest is null) { StatusText.Text = "此轮没有保存文稿请求快照，无法安全重试，请重新提问。"; return; }
        await RunOperationAsync(async token =>
        {
            var request = retry?.MentorRequest?.Snapshot() ?? CaptureMentorRequest();
            var frozen = new AiSharedDocument { Source = request.Source, InitialSource = request.InitialSource };
            var isInitial = frozen.Source.Length == 0 && frozen.InitialSource.Length == 0;
            var documentContext = AiMentorProtocol.MakeContextEntry(frozen, request.References);
            var totalCharacters = (long)documentContext.Question.Length + prompt.Length + sentAnnotations.Sum(entry => (long)entry.Question.Length + entry.Sources.Sum(source => (long)source.Text.Length) + (entry.AnswerAnchor?.Quote.Length ?? 0));
            if (totalCharacters > 80000) throw new InvalidOperationException("本次问题、共同文稿与选中评论合计超过 8 万字符；请减少携带的评论或缩短文稿。草稿已保留。");
            var batch = sentAnnotations.Select((x, i) => new AiEntry { Number = i + 1, AnswerAnchor = x.AnswerAnchor, Id = x.Id, Title = x.Title, Question = string.IsNullOrWhiteSpace(x.Question) ? "请结合统一问题分析此处。" : x.Question, Sources = [.. x.Sources], PdfBlocks = [.. x.PdfBlocks], PdfRevision = x.PdfRevision, IsPoint = x.IsPoint }).ToList();
            if (prompt.Length > 0) batch.Insert(0, new AiEntry { Title = "统一问题", Question = prompt });
            batch.Add(documentContext);
            var generation = _generation; var book = _book;
            var sentAt = DateTimeOffset.Now;
            DateTimeOffset? answeredAt = null;
            var permission = book.Active.Permission;
            var question = string.Join("\n", batch.Where(x => x.Title != "共同文稿及已选评论").Select(x => x.Title == "统一问题" ? x.Question : $"{x.DisplayTitle}：{x.Question}"));
            // Name the conversation as soon as the first valid question is accepted. The
            // initial save below persists it before any model request can fail or stall.
            if (book.Turns.Count == 0 && book.Active.Title == "新对话") book.Active.Title = AiConversation.MakeTitle(question);
            book.Active.UpdatedAt = DateTimeOffset.UtcNow;
            var buffer = new AiStreamBuffer();
            var pendingTurn = new AiTurn(question, "[本轮未完成]") { CreatedAt = sentAt, SentAt = sentAt, Incomplete = true, Prompt = prompt, SentAnnotations = sentAnnotations, MentorRequest = request };
            SharedDocumentView.SetQuestion(book.Turns.FirstOrDefault()?.Question ?? question, Settings.SelectedModel?.Name ?? "AI");
            _readingRestoreVersion++; _restoringReading = false;
            _liveSegments.Clear(); LiveQuestion.Markdown = question;
            LiveTurnNumber.Text = $"[{book.Turns.Count + 1}]";
            _liveOrdinalAlignment.Schedule();
            _liveBookmarkCheckpointReady = false; _foldingLiveTurn = pendingTurn; LiveSegments.Visibility = Visibility.Visible;
            LiveSentTime.Text = pendingTurn.SentTime; LiveReplyStatus.IsCollapsed = false;
            LiveReplyStatus.PendingText = "等待 AI 响应";
            SharedReplyStatus.PendingText = "等待 AI 响应"; SharedReplyStatus.Visibility = Visibility.Visible;
            LiveContainer.Visibility = Visibility.Visible; EmptyConversation.Visibility = Visibility.Collapsed;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (_foldingLiveTurn == pendingTurn && !_disposed) RevealLiveTurn(); }));
            AiTurnCheckpoint checkpoint;
            try { checkpoint = await AiTurnCheckpoint.StartAsync(_project!, book.Active.Id, book.Active.Title, pendingTurn, buffer, scoped: _store is not null); _liveBookmarkCheckpointReady = true; }
            catch { LiveContainer.Visibility = Visibility.Collapsed; _foldingLiveTurn = null; throw; }
            book.Active.LastSentAt = sentAt;
            book.Active.CommentCarry[pendingTurn.Id] = "temporary";
            if (retry is null && Composer.Text == submittedDraft) Composer.Clear(); StatusText.Text = "";
            if (retry is null)
                foreach (var entry in selected)
                {
                    entry.State = "提交中";
                    entry.PropertyChanged -= AnnotationChanged;
                    book.Entries.Remove(entry);
                }
            Rebind();
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
                if (retry is null)
                    foreach (var round in request.Rounds.Where(r => r.Carry == "temporary"))
                    {
                        if (!AiGlobalCarry.ConsumeTemporary(round.Project, round.ConversationId, round.RoundId, round.SelectionRevision)) continue;
                        var conversation = string.Equals(Path.GetFullPath(round.Project), Path.GetFullPath(_project!), StringComparison.OrdinalIgnoreCase) ? book.Conversations.FirstOrDefault(c => c.Id == round.ConversationId) : null;
                        if (conversation is not null && conversation.CommentCarry.TryGetValue(round.RoundId, out var current) && current == "temporary") conversation.CommentCarry[round.RoundId] = "none";
                    }
                await SaveAsync();
                foreach (var conversation in book.Conversations) await WaitForGlobalCarryAsync(() => { AiGlobalCarry.AcknowledgePersisted(_project!, conversation.Id); return true; });
                if (Settings.SelectedModel is null) await Settings.LoadModelsAsync(token);
                if (Settings.SelectedModel is not AiModel model) throw new InvalidOperationException("请选择可用模型。");
                modelId = model.Id; modelName = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name; effort = Settings.SelectedEffort;
                SharedDocumentView.SetQuestion(book.Turns.FirstOrDefault()?.Question ?? question, modelName);
                await Settings.SaveSelectionAsync(token);
                var files = ProjectFiles?.Invoke().ToArray() ?? [];
                var toolRunner = CreateToolRunner?.Invoke();
                var history = book.Turns.ToArray();
                await Task.Run(() => new AiResponses(_connection).RunAsync(model.Id, batch, history, files,
                    _ => { }, _ => { }, token, effort, toolRunner, evt =>
                    {
                        buffer.Add(evt);
                    }, permission: permission, usage: measured => Dispatcher.BeginInvoke(() =>
                    {
                        if (_generation == generation && !_disposed) { book.Active.ContextUsage = measured; UpdateContextUsage(); }
                    }), contextWindow: model.ContextWindow, memory: book.Active.ContextMemory, compacted: state => Dispatcher.Invoke(() => { if (_generation == generation && !_disposed) book.Active.ContextMemory = state; }), instructionsOverride: AiMentorProtocol.Instructions(permission, toolRunner is not null, isInitial), resuming: retry is not null, classifyCommentary: true), token);
                token.ThrowIfCancellationRequested();
                previewEnabled = false;
                var raw = buffer.Snapshot().LastOrDefault(s => s.Kind == "final")?.Text ?? "";
                if (!AiMentorProtocol.TryPrepare(raw, frozen, request.References, out var plan, out var error)) throw new InvalidOperationException(error);
                await ApplyMentorPlanAsync(book, frozen, request.References, plan!, modelName, pendingTurn.Id);
                answeredAt = DateTimeOffset.Now;
                renderTimer.Stop(); Flush();
                SharedDocumentView.RefreshDocument();
                try
                {
                    await SaveAsync();
                    await SaveSharedVersionAsync(modelName, "AI 更新共同文稿");
                    foreach (var id in plan!.Notes.Where(n => n.ReplyToCommentId.Length > 0 && n.ConversationId != book.Active.Id)
                        .Where(n => request.References.Any(r => r.ConversationId == n.ConversationId && r.Comment.Id == n.ReplyToCommentId &&
                            (r.Project.Length == 0 || string.Equals(Path.GetFullPath(r.Project), Path.GetFullPath(_project!), StringComparison.OrdinalIgnoreCase))))
                        .Select(n => n.ConversationId).Distinct())
                    {
                        var other = book.Conversations.First(c => c.Id == id);
                        await WaitForGlobalCarryAsync(() => AiGlobalCarry.Synchronize(_project!, other));
                        await WaitForGlobalCarryAsync(() => { AiGlobalCarry.AcknowledgePersisted(_project!, other.Id); return true; });
                        await AiDocumentRepository.SaveVersionAsync(_project!, other.Id, other.SharedDocument, modelName, "AI 回复评论");
                    }
                }
                catch (Exception ex) { StatusText.Text = "文稿已更新，但保存或版本记录失败：" + ex.Message; }
                foreach (var entry in selected) { entry.State = "已完成"; entry.Selected = false; }
            }
            catch (Exception ex)
            {
                previewEnabled = false;
                endReason = token.IsCancellationRequested ? "user" : "program";
                var unapplied = buffer.Snapshot().LastOrDefault(s => s.Kind is "final" or "text")?.Text;
                SharedDocumentView.RefreshDocument();
                var recoveryPending = ex is MentorTransactionException;
                if (!string.IsNullOrEmpty(unapplied)) SharedDocumentView.ShowUnapplied(unapplied, recoveryPending ? "本地文稿可能已更新，跨项目回复待恢复；以下为 AI 原始内容。" : token.IsCancellationRequested ? "生成已中止，以下内容尚未应用。" : "以下 AI 内容未应用：" + ex.Message);
                StatusText.Text = recoveryPending ? "本地文稿可能已更新，跨项目回复待恢复；请重新打开项目。" : token.IsCancellationRequested ? "生成已中止，可重试。" : "本轮未完成：" + ex.Message;
                foreach (var entry in selected) entry.State = "未完成，可重试";
                buffer.Add(new("failure", "activity", token.IsCancellationRequested ? "已停止。已完成的修改保留；可继续提问。" : "本轮未完成：" + ex.Message));
            }
            finally
            {
                var endedAt = answeredAt ?? DateTimeOffset.Now;
                SharedReplyStatus.Visibility = Visibility.Collapsed;
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
                    await SaveAsync(); checkpoint.Commit(); Rebind(); RefreshTranscript();
                }
                if (ReferenceEquals(_foldingLiveTurn, pendingTurn)) { _foldingLiveTurn = null; _liveBookmarkCheckpointReady = false; }
            }
        });
    }
    private AiMentorRequestSnapshot CaptureMentorRequest()
    {
        foreach (var conversation in _book.Conversations)
        {
            var previous = conversation.SharedDocument.Comments.SelectMany(comment => comment.Replies).Select(reply => reply.Id).ToHashSet();
            AiGlobalCarry.Synchronize(_project!, conversation);
            var added = conversation.SharedDocument.Comments.SelectMany(comment => comment.Replies).Where(reply => !previous.Contains(reply.Id)).ToArray();
            if (added.Length > 0)
            {
                _pendingReceivedReplyVersions[(_project!, conversation.Id)] = added[^1].Author;
                if (ReferenceEquals(_book.Active, conversation)) SharedDocumentView.RefreshDocument();
            }
        }
        var document = _book.Active.SharedDocument.Snapshot();
        var request = new AiMentorRequestSnapshot { Source = document.Source, InitialSource = document.InitialSource };
        var all = AiGlobalCarry.List();
        if (all.GroupBy(conversation => conversation.ConversationId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("不同项目存在相同会话标识，无法安全携带评论。请先取消或区分重复会话。");
        foreach (var conversation in all)
        {
            foreach (var selection in conversation.Rounds.Where(round => round.Mode is "temporary" or "long"))
            {
                request.Rounds.Add(new(conversation.ConversationId, selection.Id, selection.Mode, conversation.Project, selection.SelectionRevision));
                request.References.AddRange(conversation.Comments.Where(comment => comment.RoundId == selection.Id)
                    .Select(comment => new AiMentorReference(conversation.ConversationId, conversation.Title, comment.Snapshot(), conversation.Project)));
            }
        }
        return request;
    }

    private async Task ApplyMentorPlanAsync(AiNotebook book, AiSharedDocument frozen, IReadOnlyList<AiMentorReference> references, AiMentorPlan plan, string modelName, string roundId)
    {
        var localProject = _project!;
        bool IsRemote(AiMentorReference reference) => reference.Project.Length > 0 && !string.Equals(Path.GetFullPath(reference.Project), Path.GetFullPath(localProject), StringComparison.OrdinalIgnoreCase);
        var remote = plan.Notes.Select((note, index) => (note, index)).Where(item => item.note.ReplyToCommentId.Length > 0)
            .Select(item => (item.note, item.index, reference: references.First(r => r.ConversationId == item.note.ConversationId && r.Comment.Id == item.note.ReplyToCommentId)))
            .Where(item => IsRemote(item.reference)).ToArray();
        if (remote.Length == 0) { ApplyMentorPlan(book, frozen, references, plan, modelName, roundId, localProject); return; }

        await SaveAsync();
        var candidate = book.Snapshot();
        ApplyMentorPlan(candidate, frozen, references, plan, modelName, roundId, localProject);
        var changedIds = new HashSet<string> { book.Active.Id };
        foreach (var note in plan.Notes.Where(n => n.ReplyToCommentId.Length > 0))
        {
            var reference = references.First(r => r.ConversationId == note.ConversationId && r.Comment.Id == note.ReplyToCommentId);
            if (!IsRemote(reference)) changedIds.Add(note.ConversationId);
        }
        var changes = changedIds.Select(id => new AiGlobalLocalChange(id,
            book.Conversations.First(c => c.Id == id).SharedDocument.Snapshot(),
            candidate.Conversations.First(c => c.Id == id).SharedDocument.Snapshot())).ToArray();
        var updates = remote.Select(item => new AiGlobalReplyUpdate(item.reference.Project, item.reference.ConversationId, item.reference.Comment.Snapshot(),
            new AiDocumentReply { Id = AiStorage.Hash(roundId + ":" + item.reference.Comment.Id + ":" + item.index), Author = modelName, Role = "ai", Text = item.note.Text })).ToArray();
        using var prepared = AiGlobalCarry.PrepareReplies(localProject, updates, changes) ?? throw new InvalidOperationException("跨项目评论已变化，AI 内容未应用；请重新发送。");
        _mentorGlobalTransaction = true;
        try
        {
        var applied = new List<AiGlobalLocalChange>();
        foreach (var change in changes)
        {
            var document = book.Conversations.First(c => c.Id == change.ConversationId).SharedDocument;
            if (!document.TryRestore(change.Before, change.After))
            {
                foreach (var earlier in applied.AsEnumerable().Reverse())
                    if (!book.Conversations.First(c => c.Id == earlier.ConversationId).SharedDocument.TryRestore(earlier.After, earlier.Before))
                        throw new MentorTransactionException("本地文稿并发变化，已保留跨项目恢复记录。");
                prepared.AbortBeforeSave();
                throw new InvalidOperationException("文稿或评论在生成期间发生变化，AI 内容未应用；请重新发送。");
            }
            applied.Add(change);
        }
        // The transaction journal remains available if saving or committing fails.
        try { await SaveAsync(skipGlobalCarry: true); }
        catch (Exception saveError)
        {
            try { prepared.CommitAfterSave(); StatusText.Text = "文稿与跨项目回复已保存，但版本记录失败：" + saveError.Message; return; }
            catch (Exception commitError) { throw new MentorTransactionException("保存或提交跨项目回复失败，已保留恢复记录。", commitError); }
        }
        try { prepared.CommitAfterSave(); }
        catch (Exception ex) { throw new MentorTransactionException("跨项目回复尚未完成，已保留恢复记录。", ex); }
        }
        finally { _mentorGlobalTransaction = false; }
    }

    private sealed class MentorTransactionException(string message, Exception? inner = null) : Exception(message, inner);

    private static void ApplyMentorPlan(AiNotebook book, AiSharedDocument frozen, IReadOnlyList<AiMentorReference> references, AiMentorPlan plan, string modelName, string roundId, string localProject)
    {
        var document = book.Active.SharedDocument;
        foreach (var note in plan.Notes.Where(n => n.ReplyToCommentId.Length > 0))
        {
            var reference = references.First(r => r.ConversationId == note.ConversationId && r.Comment.Id == note.ReplyToCommentId);
            if (reference.Project.Length > 0 && !string.Equals(Path.GetFullPath(reference.Project), Path.GetFullPath(localProject), StringComparison.OrdinalIgnoreCase)) continue;
            var conversation = book.Conversations.FirstOrDefault(c => c.Id == note.ConversationId);
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
            document.Comments.Add(new AiDocumentComment { Start = start, Length = length, Quote = quote, Text = text, Author = modelName, Role = "ai", RoundId = roundId });
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
                var reference = references.First(r => r.ConversationId == note.ConversationId && r.Comment.Id == note.ReplyToCommentId);
                if (reference.Project.Length > 0 && !string.Equals(Path.GetFullPath(reference.Project), Path.GetFullPath(localProject), StringComparison.OrdinalIgnoreCase)) continue;
                var target = book.Conversations.First(c => c.Id == note.ConversationId).SharedDocument.Comments.First(c => c.Id == note.ReplyToCommentId);
                target.Replies.Add(new AiDocumentReply { Author = modelName, Role = "ai", Text = note.Text });
            }
            else AddComment(document.Source.IndexOf(note.Quote, StringComparison.Ordinal), note.Quote.Length, note.Quote, note.Text);
        }
        if (!string.IsNullOrWhiteSpace(plan.Message)) AddComment(0, 0, "", plan.Message);
    }
    private static bool SameDiscussion(AiDocumentComment a, AiDocumentComment b) =>
        a.Id == b.Id && a.Author == b.Author && a.Role == b.Role && a.Text == b.Text && a.Quote == b.Quote &&
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
    public async Task StopAndSaveAsync() { _operation?.Cancel(); _lifetime.Cancel(); if (_operationDone is { } pending) await pending.Task; if (_ownsSettings) await Settings.StopAsync(); await SaveAsync(); }
    public void Dispose() { _disposed = true; _documentSearchCancellation?.Cancel(); _documentSearchWindow?.Close(); ClearBookmarkMarkers(); _autoScroll.Dispose(); _liveOrdinalAlignment.Dispose(); _operation?.Cancel(); _lifetime.Cancel(); _saveTimer.Stop(); Settings.ConnectionChanged -= _settingsChanged; if (_ownsSettings) { Settings.Dispose(); _connection.Dispose(); } _lifetime.Dispose(); }
}
