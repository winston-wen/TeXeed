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
    private readonly ScrollViewer MessagesScroll;
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
    public Func<IReadOnlyList<ProjectFile>>? ProjectFiles { get; set; }
    public AiPanel() : this(new AiConnection()) { }
    internal AiPanel(AiConnection connection)
    {
        _connection = connection; InitializeComponent();
        History.ApplyTemplate();
        MessagesScroll = (ScrollViewer)History.Template.FindName("MessagesScroll", History);
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
        Settings = new AiSettingsPanel(connection);
        PermissionPicker.ItemsSource = AiPermissionChoice.Choices;
        SyncPermission();
        Settings.ConnectionChanged += (_, _) => SyncModelChoices();
        SizeChanged += (_, _) =>
        {
            ContextScroll.MaxHeight = Math.Clamp(ActualHeight * .25, 40, 170);
            Composer.MaxHeight = Math.Clamp(ActualHeight * .18, 66, 150);
            if (EntriesPopup.Child is FrameworkElement surface) surface.Width = Math.Clamp(ActualWidth - 24, 230, 480);
            ScheduleReadingContext(); ResizeChoices();
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
        _headerCloseTimer.Tick += Header_CloseTick;
        HeadingPeek.MouseDoubleClick += Header_DoubleClick;
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
        var loaded = await Task.Run(() => AiStorage.Load(project));
        _project = project; _book = loaded;
        _restoringReading = true;
        _updating = true; Composer.Text = _book.Draft; _updating = false;
        MigrateSnapshots();
        foreach (var entry in _book.Entries.Where(e => e.State == "提交中")) entry.State = "上次提交未完成，可重试";
        if (File.Exists(AiTurnCheckpoint.PathFor(project))) await SaveAsync();
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
        _book.Active.NextAnnotationNumber = _book.Entries.Count + 1;
        for (var i = 0; i < _book.Entries.Count; i++) _book.Entries[i].Number = i + 1;
        foreach (var entry in _book.Entries)
        {
            if (entry.Number == 0) entry.Number = _book.Entries.Count + 1;
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
    public async Task SaveAsync()
    {
        if (_project is null) return;
        var project = _project;
        _book.Draft = Composer.Text;
        if (!_restoringReading) _book.Active.ReadingOffset = MessagesScroll.VerticalOffset;
        CaptureReadingAnchor();
        var snapshot = _book.Snapshot();
        await _saveGate.WaitAsync(); try { await Task.Run(() => AiStorage.Save(project, snapshot)); } finally { _saveGate.Release(); }
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; SubmitButton.IsEnabled = false; StopButton.IsEnabled = true; Settings.IsEnabled = false; ModelControls.IsEnabled = false; PermissionPicker.IsEnabled = false; Entries.IsEnabled = false;
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；未完成的条目可重新提交。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { SubmitButton.IsEnabled = true; StopButton.IsEnabled = false; Settings.IsEnabled = true; ModelControls.IsEnabled = true; PermissionPicker.IsEnabled = true; Entries.IsEnabled = true; } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。条目已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    private void Add_Click(object sender, RoutedEventArgs e) => AddEntry(new());
    private void Remove_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is AiEntry entry && entry.State != "提交中") { RemoveAnnotation(entry); } }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var entry in _book.Entries) entry.Selected = true; Rebind(); Dirty(); }
    private void ClearDone_Click(object sender, RoutedEventArgs e) { _book.Entries.RemoveAll(e => e.State == "已完成"); Rebind(); Dirty(); }
    private void Entry_Changed(object sender, RoutedEventArgs e) => Dirty();
    private void Question_Changed(object sender, TextChangedEventArgs e) => Dirty();
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not AiPdfBlock block) return;
        try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 500; bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(block.ImageBase64)); bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap; }
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
        ConversationTitle.Text = AiConversation.MakeTitle(_book.Active.Title);
        MessageSearch_Changed(MessageSearchBox, null!);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void UpdateConnectionLabel()
    {
        try
        {
            var saved = AiCredentials.Load();
            ConnectionLabel.Text = (saved.UseApiKey ? "API Key" : "ChatGPT") + " · " + (string.IsNullOrEmpty(saved.Model) ? "选择模型" : saved.Model);
        }
        catch { ConnectionLabel.Text = "请在 AI 设置中检查连接"; }
    }
    private void Composer_Changed(object sender, TextChangedEventArgs e) { if (ComposerLines is not null) ComposerLines.Text = string.Join("\n", Enumerable.Range(1, Math.Max(1, Composer.LineCount))); Dirty(); }
    private async void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSendGesture(e.Key, Keyboard.Modifiers)) return;
        e.Handled = true;
        if (!e.IsRepeat) await SubmitAsync();
    }
    internal static bool IsSendGesture(Key key, ModifierKeys modifiers) => key == Key.Enter && modifiers == ModifierKeys.Control;
    private async void Submit_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    internal async Task SubmitAsync(AiEntry? single = null)
    {
        if (_project is null || _operation is not null) return;
        if (Settings.IsBusy) { StatusText.Text = "AI 设置正在连接，请等待完成或在设置中取消。"; return; }
        var submittedDraft = Composer.Text;
        var prompt = single is null ? submittedDraft.Trim() : "";
        var selected = _book.Entries.Where(e => single is null ? e.Selected : e.Id == single.Id).ToArray();
        if (selected.Length == 0 && prompt.Length == 0) { StatusText.Text = "请输入问题，或填写并勾选条目。"; return; }
        if (prompt.Length == 0 && selected.Any(e => string.IsNullOrWhiteSpace(e.Question))) { StatusText.Text = single is null ? "请在编辑栏描述要问的问题。" : "请先填写这条批注的问题。"; if (single is null) Composer.Focus(); return; }
        if (selected.Length > 20 || selected.Sum(e => e.Sources.Sum(s => s.Text.Length) + e.Question.Length + (e.AnswerAnchor?.Quote.Length ?? 0)) + prompt.Length > 80000 || selected.Sum(e => e.PdfBlocks.Sum(p => p.ImageBase64.Length)) > 24_000_000)
        { StatusText.Text = "每批最多 20 个问题、8 万字符和约 18 MB 截图，请分批提交。"; return; }
        var sentAnnotations = selected.Select((entry, i) => entry.Snapshot(i + 1)).ToList();
        await RunOperationAsync(async token =>
        {
            var batch = sentAnnotations.Select((x, i) => new AiEntry { Number = i + 1, AnswerAnchor = x.AnswerAnchor, Id = x.Id, Title = x.Title, Question = string.IsNullOrWhiteSpace(x.Question) ? "请结合统一问题分析此处。" : x.Question, Sources = [.. x.Sources], PdfBlocks = [.. x.PdfBlocks], PdfRevision = x.PdfRevision, IsPoint = x.IsPoint }).ToList();
            if (prompt.Length > 0) batch.Insert(0, new AiEntry { Title = "统一问题", Question = prompt });
            var generation = _generation; var book = _book;
            var sentAt = DateTimeOffset.Now;
            DateTimeOffset? answeredAt = null;
            var permission = book.Active.Permission;
            var question = string.Join("\n", batch.Select(x => x.Title == "统一问题" ? x.Question : $"{x.DisplayTitle}：{x.Question}"));
            // Name the conversation as soon as the first valid question is accepted. The
            // initial save below persists it before any model request can fail or stall.
            if (book.Turns.Count == 0 && book.Active.Title == "新对话") book.Active.Title = AiConversation.MakeTitle(question);
            book.Active.UpdatedAt = DateTimeOffset.UtcNow;
            ConversationTitle.Text = book.Active.Title;
            var buffer = new AiStreamBuffer();
            var pendingTurn = new AiTurn(question, "[本轮未完成]") { CreatedAt = sentAt, SentAt = sentAt, Incomplete = true, Prompt = prompt, SentAnnotations = sentAnnotations };
            var checkpoint = await AiTurnCheckpoint.StartAsync(_project!, book.Active.Id, book.Active.Title, pendingTurn, buffer);
            if (single is null && Composer.Text == submittedDraft) Composer.Clear(); StatusText.Text = "";
            foreach (var entry in selected) entry.State = "提交中"; Rebind();
            _liveSegments.Clear(); LiveQuestion.Markdown = question;
            LiveSentTime.Text = "发送时间：" + AiTimestamp.Full(sentAt);
            LiveContainer.Visibility = Visibility.Visible; EmptyConversation.Visibility = Visibility.Collapsed; MessagesScroll.ScrollToEnd();
            var renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            long renderedVersion = -1;
            void Flush()
            {
                if (_generation != generation || _disposed) return;
                var version = buffer.Version;
                if (version == renderedVersion) return;
                renderedVersion = version;
                var follow = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 40;
                foreach (var segment in buffer.Snapshot())
                {
                    segment.ReferenceTime = sentAt;
                    var existing = _liveSegments.FirstOrDefault(s => s.Id == segment.Id);
                    if (existing is null) _liveSegments.Add(segment);
                    else { if (existing.Text != segment.Text) existing.Text = segment.Text; if (existing.Kind != segment.Kind) existing.Kind = segment.Kind; existing.CreatedAt = segment.CreatedAt; existing.Details = segment.Details; }
                }
                if (follow) MessagesScroll.ScrollToEnd();
            }
            renderTimer.Tick += (_, _) => Flush(); Flush(); renderTimer.Start();
            var modelId = ""; var effort = "";
            try
            {
                await SaveAsync();
                if (Settings.SelectedModel is null) await Settings.LoadModelsAsync(token);
                if (Settings.SelectedModel is not AiModel model) throw new InvalidOperationException("请选择可用模型。");
                modelId = model.Id; effort = Settings.SelectedEffort;
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
                    }), contextWindow: model.ContextWindow, memory: book.Active.ContextMemory, compacted: state => Dispatcher.Invoke(() => { if (_generation == generation && !_disposed) book.Active.ContextMemory = state; }), proofread: AiMathProofreading.CheckAsync), token);
                answeredAt = DateTimeOffset.Now;
                renderTimer.Stop(); Flush();
                foreach (var entry in selected) { entry.State = "已完成"; entry.Selected = false; }
            }
            catch (Exception ex)
            {
                foreach (var entry in selected) entry.State = "未完成，可重试";
                buffer.Add(new("failure", "activity", ex is OperationCanceledException ? "已停止。已完成的修改保留；可继续提问。" : "本轮未完成：" + ex.Message));
            }
            finally
            {
                var endedAt = answeredAt ?? DateTimeOffset.Now;
                renderTimer.Stop();
                await checkpoint.StopAsync();
                if (_generation == generation)
                {
                    Flush();
                    var segments = buffer.Snapshot();
                    var answer = string.Join("\n\n", segments.Where(s => s.Kind == "final").Select(s => s.Text));
                    if (answer.Length == 0) answer = "[本轮未完成]\n" + string.Join("\n", segments.Where(s => s.Kind == "text" || s.Id == "failure").Select(s => s.Text));
                    book.Turns.Add(pendingTurn with { Answer = answer, AnsweredAt = answeredAt, EndedAt = endedAt, Incomplete = answeredAt is null, Model = modelId, ReasoningEffort = effort, Segments = segments });
                    if (answeredAt is not null) book.Entries.RemoveAll(e => selected.Any(sent => sent.Id == e.Id));
                    book.Active.UpdatedAt = DateTimeOffset.UtcNow;
                    // Persist before constructing a settled rich document. Even an
                    // unexpected renderer failure cannot erase the completed exchange.
                    await SaveAsync(); checkpoint.Commit(); Rebind(); RefreshTranscript();
                }
            }
        });
    }
    public async Task StopAndSaveAsync() { _operation?.Cancel(); _lifetime.Cancel(); if (_operationDone is { } pending) await pending.Task; await Settings.StopAsync(); await SaveAsync(); }
    public void Dispose() { _disposed = true; _operation?.Cancel(); _lifetime.Cancel(); _saveTimer.Stop(); _headerCloseTimer.Stop(); Settings.Dispose(); _connection.Dispose(); _lifetime.Dispose(); }
}
