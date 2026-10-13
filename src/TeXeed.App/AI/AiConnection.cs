using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TeXeed.App.AI;

internal sealed class AiConnection : IDisposable
{
    internal readonly HttpClient Http;
    private readonly string? _credentialsHome;
    internal string ConnectionIdentity => AiCredentials.Load(_credentialsHome).ConnectionIdentity;
    internal AiConnection(HttpMessageHandler? handler = null, string? credentialsHome = null)
    { _credentialsHome = credentialsHome; Http = handler is null ? new HttpClient() : new HttpClient(handler); Http.Timeout = TimeSpan.FromMinutes(10); }
    internal async Task<string> BearerAsync(CancellationToken token, string? expectedIdentity = null)
    {
        using var gate = await AiCredentials.LockAsync(token, _credentialsHome); var settings = AiCredentials.Load(_credentialsHome);
        if (expectedIdentity is not null && settings.ConnectionIdentity != expectedIdentity) throw new InvalidOperationException("连接账号已在其他窗口改变，本次提交已停止，请确认账号后重试。");
        if (settings.UseApiKey)
        { if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("请先设置 API Key。"); return settings.ApiKey; }
        var account = settings.Active;
        if (account is null || account.AccessToken.Length == 0) throw new InvalidOperationException("请先点击 Continue with ChatGPT 登录。");
        if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            if (account.RefreshToken.Length == 0) throw new InvalidOperationException("登录已过期，请重新登录。");
            var login = new ChatGptLogin(Http);
            var json = await login.TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId, ["refresh_token"] = account.RefreshToken, ["resource"] = ChatGptLogin.Resource }, token);
            if (json.TryGetProperty("id_token", out var id))
            {
                using var keys = JsonDocument.Parse(await Http.GetStringAsync(ChatGptLogin.Issuer + "/.well-known/jwks.json", token));
                if (ChatGptLogin.ValidateIdentity(id.GetString()!, account.ClientId, null, keys.RootElement).Subject != account.Subject) throw new InvalidOperationException("刷新令牌的账号不匹配，请重新登录。");
            }
            ChatGptLogin.ApplyTokens(account, json); settings.Save(_credentialsHome);
        }
        return account.AccessToken;
    }
    internal async Task LoginAsync(string? client, CancellationToken token)
    {
        AiCredentials saved;
        using (await AiCredentials.LockAsync(token, _credentialsHome)) { saved = AiCredentials.Load(_credentialsHome); saved.Save(_credentialsHome); }
        var account = await new ChatGptLogin(Http).LoginAsync(saved.HostId, saved.Accounts.FirstOrDefault(a => a.ClientId == client), token);
        using (await AiCredentials.LockAsync(token, _credentialsHome))
        {
            saved = AiCredentials.Load(_credentialsHome); saved.Accounts.RemoveAll(a => a.ClientId == account.ClientId && a.Subject == account.Subject);
            saved.Accounts.Add(account); saved.ActiveClient = account.ClientId; saved.UseApiKey = false; saved.Model = ""; saved.Save(_credentialsHome);
        }
    }
    internal async Task<string> LogoutAsync(CancellationToken token)
    {
        using var gate = await AiCredentials.LockAsync(token, _credentialsHome); var saved = AiCredentials.Load(_credentialsHome); var account = saved.Active;
        var revoked = false;
        if (account is null) return "尚未登录";
        try
        {
            using var discovery = JsonDocument.Parse(await Http.GetStringAsync(ChatGptLogin.Issuer + "/.well-known/openid-configuration", token));
            var endpoint = new Uri(discovery.RootElement.GetProperty("revocation_endpoint").GetString()!);
            if (endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com") throw new InvalidOperationException("无效的撤销端点。");
            for (var attempt = 0; attempt < 2 && !revoked; attempt++)
            {
                using var response = await Http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }), token);
                revoked = response.IsSuccessStatusCode;
                if (!revoked && attempt == 0) await Task.Delay(500, token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
        finally { account.AccessToken = account.RefreshToken = account.IdToken = ""; saved.Model = ""; saved.Save(_credentialsHome); }
        return revoked ? "已退出 ChatGPT" : "已清除本机登录；远端撤销未确认，可在 ChatGPT 设置中断开 TeXeed。";
    }
    internal async Task<List<AiModel>> ModelsAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ChatGptLogin.Resource + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await BearerAsync(token));
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"读取模型失败（HTTP {(int)response.StatusCode}）。请检查登录、网络或 API Key。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = json.RootElement;
        if (root.TryGetProperty("models", out var models)) return models.EnumerateArray().Where(m => m.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(AiModel.FromCatalog).ToList();
        return root.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Where(id => id.StartsWith("gpt-") && !id.Contains("audio") && !id.Contains("realtime") && !id.Contains("image"))
            .OrderDescending().Select(id => new AiModel(id, id) { ReasoningEfforts = AiModel.DocumentedEfforts(id) }).ToList();
    }
    public void Dispose() => Http.Dispose();
}
