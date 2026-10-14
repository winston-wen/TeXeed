using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed record AiContextUsage(string Model, long InputTokens, long OutputTokens, long? ContextWindow)
{
    [JsonIgnore] public string Display => ContextWindow is > 0
        ? ((InputTokens + OutputTokens) * 100d / ContextWindow.Value).ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
    [JsonIgnore] public string Description => $"最近一次模型请求的上下文占用（输入与输出，非累计用量）：{InputTokens + OutputTokens:N0} tokens。" +
        (ContextWindow is > 0 ? $"模型窗口：{ContextWindow:N0} tokens。" : "服务端未提供上下文窗口大小，无法计算百分比。");
    internal static AiContextUsage? FromResponse(string model, JsonNode? response, long? contextWindow)
    {
        var usage = response?["usage"];
        if (usage?["input_tokens"] is not JsonValue input || !input.TryGetValue<long>(out var inputTokens) || inputTokens < 0) return null;
        var outputTokens = usage["output_tokens"] is JsonValue output && output.TryGetValue<long>(out var value) ? Math.Max(0, value) : 0;
        return new(model, inputTokens, outputTokens, contextWindow);
    }
}
