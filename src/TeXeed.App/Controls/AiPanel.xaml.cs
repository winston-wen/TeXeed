using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
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
    private bool _updating, _loaded, _disposed;
    private int _generation;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationDone;
    public Func<IReadOnlyList<ProjectFile>>? ProjectFiles { get; set; }
    public AiPanel() : this(new AiConnection()) { }
    internal AiPanel(AiConnection connection)
    {
        _connection = connection; InitializeComponent();
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; try { RefreshAccounts(); } catch (Exception ex) { ShowError(ex); } } };
    }
    public async Task SetProjectAsync(string project)
    {
        _operation?.Cancel(); if (_operationDone is { } pending) await pending.Task;
        _generation++; _saveTimer.Stop(); await SaveAsync();
        var loaded = await Task.Run(() => AiStorage.Load(project));
        _project = project; _book = loaded;
        foreach (var entry in _book.Entries.Where(e => e.State == "提交中")) entry.State = "上次提交未完成，可重试";
        Rebind(); RefreshTranscript(); StatusText.Text = "问题保存在本机，勾选后统一提交。";
    }
    public void AddEntry(AiEntry entry)
    {
        if (_project is null) { StatusText.Text = "请先打开项目。"; return; }
        if (_book.Entries.Sum(e => e.PdfBlocks.Sum(p => (long)p.ImageBase64.Length)) + entry.PdfBlocks.Sum(p => (long)p.ImageBase64.Length) > 44_000_000) { StatusText.Text = "项目问题截图合计最多约 32 MB，请清除不再需要的条目。"; return; }
        if (_book.Entries.Count >= 100) { StatusText.Text = "最多保留 100 个条目，请清除已完成条目。"; return; }
        _book.Entries.Add(entry); Rebind(); Dirty();
    }
    private void Rebind() { Entries.ItemsSource = null; Entries.ItemsSource = _book.Entries; }
    private void Dirty() { if (_updating || _disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public async Task SaveAsync()
    {
        if (_project is null) return;
        var project = _project;
        var snapshot = new AiNotebook { Entries = _book.Entries.Select(e => new AiEntry { Id = e.Id, Title = e.Title, Question = e.Question, Selected = e.Selected, State = e.State, Sources = [.. e.Sources], PdfBlocks = [.. e.PdfBlocks], PdfRevision = e.PdfRevision }).ToList(), Turns = [.. _book.Turns] };
        await _saveGate.WaitAsync(); try { await Task.Run(() => AiStorage.Save(project, snapshot)); } finally { _saveGate.Release(); }
    }
    private void RefreshAccounts()
    {
        _updating = true;
        try
        {
            var saved = AiCredentials.Load(); Accounts.ItemsSource = saved.Accounts; Accounts.SelectedItem = saved.Active; UseKey.IsChecked = saved.UseApiKey;
            AccountStatus.Text = saved.UseApiKey ? (saved.ApiKey.Length > 0 ? "使用已保存的 API Key · 单独计费" : "尚未设置 API Key") : saved.Active is { } a && a.AccessToken.Length > 0 ? "ChatGPT · " + a : "尚未登录 ChatGPT";
        }
        finally { _updating = false; }
    }
    private async Task MutateCredentialsAsync(Action<AiCredentials> action)
    { using (await AiCredentials.LockAsync(CancellationToken.None)) { var c = AiCredentials.Load(); action(c); c.Save(); } RefreshAccounts(); }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; SubmitButton.IsEnabled = false; StopButton.IsEnabled = true; ConnectionPanel.IsEnabled = false; Entries.IsEnabled = false;
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；未完成的条目可重新提交。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { SubmitButton.IsEnabled = true; StopButton.IsEnabled = false; ConnectionPanel.IsEnabled = true; Entries.IsEnabled = true; } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。条目已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    private async Task LoadModelsAsync(CancellationToken token)
    {
        var models = await _connection.ModelsAsync(token); var selected = AiCredentials.Load().Model;
        Models.ItemsSource = models; Models.SelectedItem = models.FirstOrDefault(m => m.Id == selected) ?? models.FirstOrDefault();
        StatusText.Text = models.Count > 0 ? "模型已就绪。" : "该账号尚无可用模型。";
        if (models.Count > 0) ConnectionPanel.IsExpanded = false;
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    { StatusText.Text = "请在系统浏览器完成登录；可点停止取消。"; await _connection.LoginAsync((Accounts.SelectedItem as AiAccount)?.ClientId, token); RefreshAccounts(); await LoadModelsAsync(token); });
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    { StatusText.Text = "请在系统浏览器授权新的账号或工作区。"; await _connection.LoginAsync(null, token); RefreshAccounts(); await LoadModelsAsync(token); });
    private async void Logout_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token => { StatusText.Text = await _connection.LogoutAsync(token); Models.ItemsSource = null; RefreshAccounts(); });
    private async void Models_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(LoadModelsAsync);
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Accounts.SelectedItem is not AiAccount account) return;
        await RunOperationAsync(async token => { await MutateCredentialsAsync(c => { c.ActiveClient = account.ClientId; c.UseApiKey = false; c.Model = ""; }); Models.ItemsSource = null; if (account.AccessToken.Length > 0) await LoadModelsAsync(token); });
    }
    private async void UseKey_Click(object sender, RoutedEventArgs e)
    { try { var value = UseKey.IsChecked == true; await MutateCredentialsAsync(c => { c.UseApiKey = value; c.Model = ""; }); Models.ItemsSource = null; } catch (Exception ex) { ShowError(ex); } }
    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    { try { var key = ApiKeyBox.Password.Trim(); if (key.Length == 0) { StatusText.Text = "请先输入 API Key。"; return; } await MutateCredentialsAsync(c => { c.ApiKey = key; c.UseApiKey = true; c.Model = ""; }); ApiKeyBox.Clear(); Models.ItemsSource = null; await RunOperationAsync(LoadModelsAsync); } catch (Exception ex) { ShowError(ex); } }
    private async void ClearKey_Click(object sender, RoutedEventArgs e)
    { try { await MutateCredentialsAsync(c => { c.ApiKey = ""; c.UseApiKey = false; c.Model = ""; }); ApiKeyBox.Clear(); Models.ItemsSource = null; } catch (Exception ex) { ShowError(ex); } }
    private async void Model_Changed(object sender, SelectionChangedEventArgs e)
    { if (_updating || Models.SelectedItem is not AiModel model) return; try { using (await AiCredentials.LockAsync(CancellationToken.None)) { var c = AiCredentials.Load(); c.Model = model.Id; c.Save(); } } catch (Exception ex) { ShowError(ex); } }
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
    private void RefreshTranscript() => Transcript.Text = string.Join("\n\n────────\n\n", _book.Turns.Select(t => t.Question + "\n\n" + t.Answer));
    private async void Submit_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    internal async Task SubmitAsync()
    {
        if (_project is null || _operation is not null) return;
        var selected = _book.Entries.Where(e => e.Selected).ToArray();
        if (selected.Length == 0 || selected.Any(e => string.IsNullOrWhiteSpace(e.Question))) { StatusText.Text = "请勾选条目，并为每个条目填写问题。"; return; }
        if (selected.Length > 20 || selected.Sum(e => e.Sources.Sum(s => s.Text.Length) + e.Question.Length) > 80000 || selected.Sum(e => e.PdfBlocks.Sum(p => p.ImageBase64.Length)) > 24_000_000)
        { StatusText.Text = "每批最多 20 个问题、8 万字符和约 18 MB 截图，请分批提交。"; return; }
        await RunOperationAsync(async token =>
        {
            if (Models.SelectedItem is not AiModel) await LoadModelsAsync(token);
            if (Models.SelectedItem is not AiModel model) throw new InvalidOperationException("请选择可用模型。");
            var batch = selected.Select(x => new AiEntry { Id = x.Id, Title = x.Title, Question = x.Question, Sources = [.. x.Sources], PdfBlocks = [.. x.PdfBlocks], PdfRevision = x.PdfRevision }).ToArray();
            var files = ProjectFiles?.Invoke().ToArray() ?? []; var generation = _generation; var book = _book;
            foreach (var entry in selected) entry.State = "提交中"; Rebind(); await SaveAsync();
            var question = string.Join("\n", batch.Select(x => $"[{x.Id[..8]}] {x.Title}：{x.Question}")); var answer = new System.Text.StringBuilder(); var prior = Transcript.Text;
            StatusText.Text = "正在提交并等待回答…";
            try
            {
                await new AiResponses(_connection).RunAsync(model.Id, batch, book.Turns, files,
                    text => { answer.Append(text); if (_generation == generation) Transcript.Text = prior + "\n\n" + question + "\n\n" + answer; },
                    status => { if (_generation == generation) StatusText.Text = status; }, token);
                foreach (var entry in selected) { entry.State = "已完成"; entry.Selected = false; }
                if (_generation == generation) { book.Turns.Add(new(question, answer.ToString())); StatusText.Text = "本批次已完成。"; }
            }
            catch { foreach (var entry in selected) entry.State = "未完成，可重试"; if (_generation == generation && answer.Length > 0) book.Turns.Add(new(question, "[未完成的回复]\n" + answer)); throw; }
            finally { if (_generation == generation) { Rebind(); await SaveAsync(); } }
        });
    }
    public async Task StopAndSaveAsync() { _operation?.Cancel(); if (_operationDone is { } pending) await pending.Task; await SaveAsync(); }
    public void Dispose() { _disposed = true; _operation?.Cancel(); _saveTimer.Stop(); _connection.Dispose(); }
}
