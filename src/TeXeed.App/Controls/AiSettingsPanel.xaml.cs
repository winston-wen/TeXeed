using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiSettingsPanel : UserControl, IDisposable
{
    private readonly AiConnection _connection;
    private bool _updating, _disposed, _loaded;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationDone;
    private Task? _modelLoad;
    private CancellationTokenSource? _catalogCancellation;
    public event EventHandler? ConnectionChanged;
    internal bool IsBusy => _operation is not null;
    internal AiModel? SelectedModel => Models.SelectedItem as AiModel;
    internal string SelectedEffort => (Reasoning.SelectedItem as AiEffort)?.Value ?? "";
    internal IReadOnlyList<AiModel> AvailableModels => (Models.ItemsSource as IEnumerable<AiModel>)?.ToArray() ?? [];

    internal AiSettingsPanel(AiConnection connection)
    {
        _connection = connection;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            try { RefreshAccounts(); if (HasConnection()) await RunOperationAsync(LoadModelsAsync); }
            catch (Exception ex) { ShowError(ex); }
        };
    }
    internal static bool HasConnection()
    {
        var saved = AiCredentials.Load();
        return saved.UseApiKey ? saved.ApiKey.Length > 0 : saved.Active?.AccessToken.Length > 0;
    }
    internal void RefreshAccounts()
    {
        _updating = true;
        try
        {
            var saved = AiCredentials.Load();
            Accounts.ItemsSource = saved.Accounts;
            Accounts.SelectedItem = saved.Active;
            UseKey.IsChecked = saved.UseApiKey;
            AccountStatus.Text = saved.UseApiKey ? (saved.ApiKey.Length > 0 ? "使用已保存的 API Key · 单独计费" : "尚未设置 API Key")
                : saved.Active is { } a && a.AccessToken.Length > 0 ? "ChatGPT · " + a : "尚未登录 ChatGPT";
        }
        finally { _updating = false; }
    }
    private async Task MutateCredentialsAsync(Action<AiCredentials> action)
    {
        using (await AiCredentials.LockAsync(CancellationToken.None))
        {
            var credentials = AiCredentials.Load();
            action(credentials);
            credentials.ReasoningEffort = "";
            credentials.Save();
        }
        _updating = true;
        Models.ItemsSource = null;
        Reasoning.ItemsSource = null;
        _updating = false;
        RefreshAccounts();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var cancellation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationDone = done;
        _operation = cancellation;
        CancelButton.IsEnabled = true;
        ConnectionOptions.IsEnabled = false;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已取消连接操作。"; }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            cancellation.Dispose();
            _operation = null;
            if (!_disposed) { CancelButton.IsEnabled = false; ConnectionOptions.IsEnabled = true; }
            _operationDone = null;
            done.TrySetResult();
        }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。"
        : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;

    internal Task LoadModelsAsync(CancellationToken token)
    {
        if (_modelLoad is { IsCompleted: false }) return _modelLoad;
        _catalogCancellation?.Dispose();
        _catalogCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        return _modelLoad = LoadModelCatalogAsync(_catalogCancellation.Token);
    }
    private async Task LoadModelCatalogAsync(CancellationToken token)
    {
        var models = await _connection.ModelsAsync(token);
        token.ThrowIfCancellationRequested();
        if (_disposed) return;
        var saved = AiCredentials.Load();
        _updating = true;
        try
        {
            Models.ItemsSource = models;
            Models.SelectedItem = models.FirstOrDefault(m => m.Id == saved.Model) ?? models.FirstOrDefault();
            ConfigureEfforts(saved.ReasoningEffort);
        }
        finally { _updating = false; }
        await SaveSelectionAsync(token);
        StatusText.Text = models.Count > 0 ? "模型与推理强度已就绪。" : "该账号尚无可用模型。";
    }
    private void ConfigureEfforts(string preferred)
    {
        var choices = AiEffort.Choices(SelectedModel);
        Reasoning.ItemsSource = choices;
        Reasoning.SelectedItem = choices.FirstOrDefault(e => e.Value == preferred) ?? choices[0];
    }
    internal async Task SelectModelAsync(AiModel model)
    {
        _updating = true;
        try { Models.SelectedItem = AvailableModels.FirstOrDefault(m => m.Id == model.Id); ConfigureEfforts(SelectedEffort); }
        finally { _updating = false; }
        await SaveSelectionAsync(CancellationToken.None);
    }
    internal async Task SelectEffortAsync(string effort)
    {
        _updating = true;
        try { ConfigureEfforts(effort); }
        finally { _updating = false; }
        await SaveSelectionAsync(CancellationToken.None);
    }
    internal async Task SaveSelectionAsync(CancellationToken token)
    {
        if (SelectedModel is not { } model) { ConnectionChanged?.Invoke(this, EventArgs.Empty); return; }
        var effort = SelectedEffort;
        using (await AiCredentials.LockAsync(token))
        {
            var saved = AiCredentials.Load();
            saved.Model = model.Id;
            saved.ReasoningEffort = effort;
            saved.Save();
        }
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private async void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SelectedModel is null) return;
        try
        {
            _updating = true;
            ConfigureEfforts(AiCredentials.Load().ReasoningEffort);
            _updating = false;
            await SaveSelectionAsync(CancellationToken.None);
        }
        catch (Exception ex) { _updating = false; ShowError(ex); }
    }
    private async void Reasoning_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        try { await SaveSelectionAsync(CancellationToken.None); } catch (Exception ex) { ShowError(ex); }
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        StatusText.Text = "请在系统浏览器完成登录；可点取消。";
        await _connection.LoginAsync((Accounts.SelectedItem as AiAccount)?.ClientId, token);
        RefreshAccounts();
        await LoadModelsAsync(token);
    });
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        StatusText.Text = "请在系统浏览器授权新的账号或工作区。";
        await _connection.LoginAsync(null, token);
        RefreshAccounts();
        await LoadModelsAsync(token);
    });
    private async void Logout_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        StatusText.Text = await _connection.LogoutAsync(token);
        _updating = true; Models.ItemsSource = null; Reasoning.ItemsSource = null; _updating = false;
        RefreshAccounts();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    });
    private async void Models_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(LoadModelsAsync);
    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Accounts.SelectedItem is not AiAccount account) return;
        await RunOperationAsync(async token =>
        {
            await MutateCredentialsAsync(c => { c.ActiveClient = account.ClientId; c.UseApiKey = false; c.Model = ""; });
            if (account.AccessToken.Length > 0) await LoadModelsAsync(token);
        });
    }
    private async void UseKey_Click(object sender, RoutedEventArgs e)
    {
        try { var value = UseKey.IsChecked == true; await MutateCredentialsAsync(c => { c.UseApiKey = value; c.Model = ""; }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = ApiKeyBox.Password.Trim();
            if (key.Length == 0) { StatusText.Text = "请先输入 API Key。"; return; }
            await MutateCredentialsAsync(c => { c.ApiKey = key; c.UseApiKey = true; c.Model = ""; });
            ApiKeyBox.Clear();
            await RunOperationAsync(LoadModelsAsync);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        try { await MutateCredentialsAsync(c => { c.ApiKey = ""; c.UseApiKey = false; c.Model = ""; }); ApiKeyBox.Clear(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    internal async Task StopAsync()
    {
        _operation?.Cancel();
        _catalogCancellation?.Cancel();
        if (_operationDone is { } pending) await pending.Task;
        if (_modelLoad is { } modelWork) { try { await modelWork; } catch (Exception) { } }
    }
    public void Dispose() { _disposed = true; _operation?.Cancel(); _catalogCancellation?.Cancel(); _catalogCancellation?.Dispose(); }
}
