using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

internal static class AiFailure
{
    internal static async Task<InvalidOperationException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        var body = await response.Content.ReadAsStringAsync(cancellation);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { }
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        return FromEvent(json, (int)response.StatusCode, requestId);
    }

    internal static InvalidOperationException FromEvent(JsonNode? json, int? httpStatus = null, string? requestId = null)
    {
        var error = json?["error"] ?? json?["response"]?["error"];
        var code = error?["code"]?.ToString();
        var parameter = error?["param"]?.ToString();
        var detail = error?["message"]?.ToString() ?? json?["detail"]?.ToString()
            ?? json?["response"]?["incomplete_details"]?["reason"]?.ToString() ?? "未收到完成确认";
        var recovery = code switch
        {
            "subscription_sharing_usage_limit_exceeded" => "请检查 ChatGPT 设置中的 TeXeed 用量额度。",
            "subscription_sharing_user_not_eligible" => "当前账号或工作区不允许此项 ChatGPT 额度使用。",
            "subscription_sharing_unsupported_capability" => "请求包含当前登录通道不支持的能力。",
            _ => httpStatus == 401 ? "请重新登录。" : "本次问题已保留，可在排除原因后重试。"
        };
        var message = $"AI 请求未完成{(httpStatus is null ? "" : $" · HTTP {httpStatus}")}\n{code} {parameter}\n{detail}\n{recovery}";
        if (!string.IsNullOrEmpty(requestId)) message += "\n请求编号：" + requestId;
        // Diagnostics never display bearer strings or token-shaped values.
        message = Regex.Replace(message, @"(?i)Bearer\s+\S+|sk-[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[已隐藏凭据]");
        return new InvalidOperationException(message[..Math.Min(message.Length, 1800)]);
    }
}
