using System.IO;
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
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly SemaphoreSlim _saveGate = new(1);
    private AiNotebook _book = new();
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
        Settings = new AiSettingsPanel(connection);
        Settings.ConnectionChanged += (_, _) => SyncModelChoices();
        SizeChanged += (_, _) =>
        {
            ContextScroll.MaxHeight = Math.Clamp(ActualHeight * .25, 40, 170);
            Composer.MaxHeight = Math.Clamp(ActualHeight * .18, 66, 150);
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
        Loaded += async (_, _) => { SyncModelChoices(); if (Settings.SelectedModel is null) await RefreshModelChoicesAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
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
        foreach (var entry in _book.Entries.Where(e => e.State == "提交中")) entry.State = "上次提交未完成，可重试";
        ShowCurrentConversation();
    }
    public void AddEntry(AiEntry entry)
    {
        if (_project is null) { StatusText.Text = "请先打开项目。"; return; }
        if (_book.Conversations.Sum(c => c.Entries.Sum(e => e.PdfBlocks.Sum(p => (long)p.ImageBase64.Length))) + entry.PdfBlocks.Sum(p => (long)p.ImageBase64.Length) > 44_000_000) { StatusText.Text = "项目问题截图合计最多约 32 MB，请清除不再需要的条目。"; return; }
        if (_book.Entries.Count >= 100) { StatusText.Text = "最多保留 100 个条目，请清除已完成条目。"; return; }
        _book.Entries.Add(entry); Rebind(); Dirty();
        Composer.Focus();
    }
    private void Rebind()
    {
        Entries.ItemsSource = null;
        Entries.ItemsSource = _book.Entries;
        ContextExpander.Visibility = _book.Entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ContextExpander.Header = $"圈点与问题条目 · {_book.Entries.Count}";
    }
    private void Dirty() { if (_updating || _disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public async Task SaveAsync()
    {
        if (_project is null) return;
        var project = _project;
        _book.Draft = Composer.Text;
        var snapshot = _book.Snapshot();
        await _saveGate.WaitAsync(); try { await Task.Run(() => AiStorage.Save(project, snapshot)); } finally { _saveGate.Release(); }
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; SubmitButton.IsEnabled = false; StopButton.IsEnabled = true; Settings.IsEnabled = false; ModelControls.IsEnabled = false; History.IsEnabled = false; Entries.IsEnabled = false; Composer.IsReadOnly = true;
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；未完成的条目可重新提交。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { SubmitButton.IsEnabled = true; StopButton.IsEnabled = false; Settings.IsEnabled = true; ModelControls.IsEnabled = true; History.IsEnabled = true; Entries.IsEnabled = true; Composer.IsReadOnly = false; } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。条目已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    private void Add_Click(object sender, RoutedEventArgs e) => AddEntry(new());
    private void Remove_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is AiEntry entry && entry.State != "提交中") { _book.Entries.Remove(entry); Rebind(); Dirty(); } }
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
        History.ItemsSource = null;
        History.ItemsSource = _book.Turns;
        EmptyConversation.Visibility = _book.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveContainer.Visibility = Visibility.Collapsed;
        ConversationTitle.Text = _book.Active.Title;
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
    private void Composer_Changed(object sender, TextChangedEventArgs e) => Dirty();
    private async void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.IsKeyDown(Key.ImeProcessed)) return;
        e.Handled = true;
        await SubmitAsync();
    }
    private async void Submit_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    internal async Task SubmitAsync()
    {
        if (_project is null || _operation is not null) return;
        if (Settings.IsBusy) { StatusText.Text = "AI 设置正在连接，请等待完成或在设置中取消。"; return; }
        var prompt = Composer.Text.Trim();
        var selected = _book.Entries.Where(e => e.Selected).ToArray();
        if (selected.Length == 0 && prompt.Length == 0) { StatusText.Text = "请输入问题，或填写并勾选条目。"; return; }
        if (prompt.Length == 0 && selected.Any(e => string.IsNullOrWhiteSpace(e.Question))) { StatusText.Text = "请在编辑栏描述要问的问题。"; Composer.Focus(); return; }
        if (selected.Length > 20 || selected.Sum(e => e.Sources.Sum(s => s.Text.Length) + e.Question.Length) + prompt.Length > 80000 || selected.Sum(e => e.PdfBlocks.Sum(p => p.ImageBase64.Length)) > 24_000_000)
        { StatusText.Text = "每批最多 20 个问题、8 万字符和约 18 MB 截图，请分批提交。"; return; }
        await RunOperationAsync(async token =>
        {
            if (Settings.SelectedModel is null) await Settings.LoadModelsAsync(token);
            if (Settings.SelectedModel is not AiModel model) throw new InvalidOperationException("请选择可用模型。");
            await Settings.SaveSelectionAsync(token);
            var effort = Settings.SelectedEffort;
            var batch = selected.Select(x => new AiEntry { Id = x.Id, Title = x.Title, Question = string.IsNullOrWhiteSpace(x.Question) ? "请结合统一问题分析此处。" : x.Question, Sources = [.. x.Sources], PdfBlocks = [.. x.PdfBlocks], PdfRevision = x.PdfRevision, IsPoint = x.IsPoint }).ToList();
            if (prompt.Length > 0) batch.Insert(0, new AiEntry { Title = "统一问题", Question = prompt });
            var files = ProjectFiles?.Invoke().ToArray() ?? []; var generation = _generation; var book = _book;
            foreach (var entry in selected) entry.State = "提交中"; Rebind(); await SaveAsync();
            var question = string.Join("\n", batch.Select(x => x.Title == "统一问题" ? x.Question : $"{x.Title}：{x.Question}")); var answer = new System.Text.StringBuilder(); LiveQuestion.Text = question; Transcript.Text = ""; LiveContainer.Visibility = Visibility.Visible; EmptyConversation.Visibility = Visibility.Collapsed; MessagesScroll.ScrollToEnd();
            StatusText.Text = "正在提交并等待回答…";
            try
            {
                await new AiResponses(_connection).RunAsync(model.Id, batch, book.Turns, files,
                    text =>
                    {
                        answer.Append(text);
                        if (_generation != generation) return;
                        var follow = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset < 40;
                        Transcript.Text = answer.ToString();
                        if (follow) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => MessagesScroll.ScrollToEnd()));
                    },
                    status => { if (_generation == generation) StatusText.Text = status; }, token, effort);
                foreach (var entry in selected) { entry.State = "已完成"; entry.Selected = false; }
                if (_generation == generation)
                {
                    if (book.Turns.Count == 0 && book.Active.Title == "新对话") book.Active.Title = AiConversation.MakeTitle(question);
                    book.Turns.Add(new(question, answer.ToString()) { Model = model.Id, ReasoningEffort = effort });
                    book.Active.UpdatedAt = DateTimeOffset.UtcNow;
                    Composer.Clear(); StatusText.Text = "本次对话已完成。";
                }
            }
            catch { foreach (var entry in selected) entry.State = "未完成，可重试"; if (_generation == generation && answer.Length > 0) book.Turns.Add(new(question, "[未完成的回复]\n" + answer)); throw; }
            finally { if (_generation == generation) { Rebind(); RefreshTranscript(); await SaveAsync(); } }
        });
    }
    public async Task StopAndSaveAsync() { _operation?.Cancel(); _lifetime.Cancel(); if (_operationDone is { } pending) await pending.Task; await Settings.StopAsync(); await SaveAsync(); }
    public void Dispose() { _disposed = true; _operation?.Cancel(); _lifetime.Cancel(); _saveTimer.Stop(); Settings.Dispose(); _connection.Dispose(); _lifetime.Dispose(); }
}
