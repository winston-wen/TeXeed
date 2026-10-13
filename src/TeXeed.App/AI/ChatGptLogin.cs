using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeXeed.App.AI;

internal sealed class ChatGptLogin(HttpClient http)
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
    internal static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));
    internal async Task<AiAccount> LoginAsync(string host, AiAccount? previous, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5)); token = timeout.Token;
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var listener = new HttpListener(); var redirect = $"http://127.0.0.1:{port}/auth/callback";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var state = Random(); var nonce = Random(); var verifier = Random();
        var values = new Dictionary<string, string>
        {
            ["client_id"] = previous?.ClientId ?? "dynamic_agent_client",
            ["ext_agent_host_id"] = host,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = Scopes,
            ["resource"] = Resource,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (previous is null) values["agent_name_hint"] = "TeXeed";
        else { if (previous.IdToken.Length > 0) values["id_token_hint"] = previous.IdToken; if (previous.Email.Length > 0) values["login_hint"] = previous.Email; }
        var url = Issuer + "/api/accounts/authorize?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法打开默认浏览器，请检查 Windows 默认浏览器设置后重试。"); }
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(token);
            if (context.Request.Url?.AbsolutePath != "/auth/callback" || context.Request.HttpMethod != "GET") { context.Response.StatusCode = 404; context.Response.Close(); continue; }
            var query = context.Request.QueryString;
            var valid = query["state"] == state;
            var body = Encoding.UTF8.GetBytes(valid ? "<!doctype html><meta charset=utf-8><p>授权结果已收到。请返回 TeXeed 查看登录结果。</p>" : "Invalid authorization state.");
            context.Response.StatusCode = valid ? 200 : 400; context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store"; context.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
            await context.Response.OutputStream.WriteAsync(body, token); context.Response.Close();
            if (!valid) continue;
            if (query["error"] is not null) throw new InvalidOperationException("ChatGPT 授权未完成。请重新登录并允许使用 ChatGPT 额度。");
            var client = query["client_id"] ?? previous?.ClientId;
            if (string.IsNullOrEmpty(client) || client == "dynamic_agent_client" || previous is not null && client != previous.ClientId || string.IsNullOrEmpty(query["code"])) throw new InvalidOperationException("登录回调缺少或不匹配客户端信息。");
            var result = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = client, ["code"] = query["code"]!, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource }, token);
            var id = result.GetProperty("id_token").GetString()!;
            using var jwks = JsonDocument.Parse(await http.GetStringAsync(Issuer + "/.well-known/jwks.json", token));
            var identity = ValidateIdentity(id, client, nonce, jwks.RootElement);
            if (previous is not null && previous.Subject != identity.Subject) throw new InvalidOperationException("返回的账号与所选账号不一致。");
            var account = new AiAccount { ClientId = client, Subject = identity.Subject, Email = identity.Email };
            ApplyTokens(account, result); return account;
        }
    }
    internal static (string Subject, string Email) ValidateIdentity(string jwt, string client, string? nonce, JsonElement jwks)
    {
        var parts = jwt.Split('.'); if (parts.Length != 3) throw new CryptographicException("无效的身份令牌。");
        using var header = JsonDocument.Parse(Decode(parts[0])); using var claims = JsonDocument.Parse(Decode(parts[1]));
        var h = header.RootElement; var c = claims.RootElement; var algorithm = h.GetProperty("alg").GetString();
        var key = jwks.GetProperty("keys").EnumerateArray().FirstOrDefault(k => k.GetProperty("kid").GetString() == h.GetProperty("kid").GetString());
        var payload = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]); var signature = Decode(parts[2]); bool verified = false;
        if (key.ValueKind != JsonValueKind.Undefined && algorithm == "RS256" && key.GetProperty("kty").GetString() == "RSA")
        {
            using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
            verified = rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else if (key.ValueKind != JsonValueKind.Undefined && algorithm == "ES256" && key.GetProperty("kty").GetString() == "EC" && key.GetProperty("crv").GetString() == "P-256")
        {
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Decode(key.GetProperty("x").GetString()!), Y = Decode(key.GetProperty("y").GetString()!) } });
            verified = ec.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        var aud = c.GetProperty("aud"); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!verified || c.GetProperty("iss").GetString() != Issuer || !(aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Any(a => a.GetString() == client) : aud.GetString() == client)
            || c.GetProperty("exp").GetInt64() <= now || c.GetProperty("iat").GetInt64() > now + 5 || c.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 5
            || nonce is not null && (!c.TryGetProperty("nonce", out var n) || n.GetString() != nonce)
            || !c.TryGetProperty("sub", out var sub) || string.IsNullOrEmpty(sub.GetString())) throw new CryptographicException("ChatGPT 身份验证失败。");
        return (sub.GetString()!, c.TryGetProperty("email", out var email) ? email.GetString() ?? sub.GetString()! : sub.GetString()!);
    }
    internal async Task<JsonElement> TokenAsync(Dictionary<string, string> fields, CancellationToken token)
    {
        using var response = await http.PostAsync(Issuer + "/api/accounts/oauth/token", new FormUrlEncodedContent(fields), token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ChatGPT 授权失效或暂不可用（HTTP {(int)response.StatusCode}），请重新登录。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return json.RootElement.Clone();
    }
    internal static void ApplyTokens(AiAccount account, JsonElement json)
    {
        var scope = json.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : account.Scope;
        if (!scope.Split(' ').Contains("chatgpt.tokens.use.direct")) throw new InvalidOperationException("该账号尚未授权使用 ChatGPT 额度，请重新授权。");
        if (json.TryGetProperty("token_type", out var kind) && !string.Equals(kind.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不支持的登录令牌类型。");
        account.AccessToken = json.GetProperty("access_token").GetString()!;
        if (json.TryGetProperty("refresh_token", out var refresh)) account.RefreshToken = refresh.GetString()!;
        if (json.TryGetProperty("id_token", out var id)) account.IdToken = id.GetString()!;
        account.Scope = scope; account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetDouble());
    }
}
