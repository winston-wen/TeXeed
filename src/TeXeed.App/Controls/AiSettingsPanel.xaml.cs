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
    public event EventHandler? ConnectionChanged;
    internal bool IsBusy => _operation is not null;
    internal AiModel? SelectedModel => Models.SelectedItem as AiModel;
    internal AiSettingsPanel(AiConnection connection)
    {
        _connection = connection;
        InitializeComponent();
        Loaded += (_, _) => { if (_loaded) return; _loaded = true; try { RefreshAccounts(); } catch (Exception ex) { ShowError(ex); } };
    }
    internal void RefreshAccounts()
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
    { using (await AiCredentials.LockAsync(CancellationToken.None)) { var c = AiCredentials.Load(); action(c); c.Save(); } RefreshAccounts(); ConnectionChanged?.Invoke(this, EventArgs.Empty); }
    private async Task RunOperationAsync(Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        var operation = new CancellationTokenSource(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationDone = done; _operation = operation; CancelButton.IsEnabled = true; ConnectionOptions.IsEnabled = false;
        try { await work(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "已停止；未完成的条目可重新提交。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); _operation = null; if (!_disposed) { CancelButton.IsEnabled = false; ConnectionOptions.IsEnabled = true; } _operationDone = null; done.TrySetResult(); }
    }
    private void ShowError(Exception ex) => StatusText.Text = ex is HttpRequestException ? "网络连接失败，请检查网络后重试。条目已保留。" : ex is CryptographicException ? "加密凭据或登录身份验证失败，请重新登录。" : ex.Message;
    internal async Task LoadModelsAsync(CancellationToken token)
    {
        var models = await _connection.ModelsAsync(token); var selected = AiCredentials.Load().Model;
        Models.ItemsSource = models; Models.SelectedItem = models.FirstOrDefault(m => m.Id == selected) ?? models.FirstOrDefault();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
        StatusText.Text = models.Count > 0 ? "模型已就绪。" : "该账号尚无可用模型。";

    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    { StatusText.Text = "请在系统浏览器完成登录；可点停止取消。"; await _connection.LoginAsync((Accounts.SelectedItem as AiAccount)?.ClientId, token); RefreshAccounts(); await LoadModelsAsync(token); });
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    { StatusText.Text = "请在系统浏览器授权新的账号或工作区。"; await _connection.LoginAsync(null, token); RefreshAccounts(); await LoadModelsAsync(token); });
    private async void Logout_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token => { StatusText.Text = await _connection.LogoutAsync(token); Models.ItemsSource = null; RefreshAccounts(); ConnectionChanged?.Invoke(this, EventArgs.Empty); });
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
    { if (_updating || Models.SelectedItem is not AiModel model) return; try { using (await AiCredentials.LockAsync(CancellationToken.None)) { var c = AiCredentials.Load(); c.Model = model.Id; c.Save(); } ConnectionChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { ShowError(ex); } }
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    internal async Task StopAsync()
    {
        _operation?.Cancel();
        if (_operationDone is { } pending) await pending.Task;
    }
    public void Dispose() { _disposed = true; _operation?.Cancel(); }
}
