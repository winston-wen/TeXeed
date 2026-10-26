using System.Text.Json;

namespace TeXeed.App.AI;

internal sealed record AiModel(string Id, string Name)
{
    private static readonly string[] SupportedEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    internal IReadOnlyList<string> ReasoningEfforts { get; init; } = [];
    internal string DefaultReasoningEffort { get; init; } = "";
    internal long? ContextWindow { get; init; }
    public override string ToString() => Name;

    internal static AiModel FromCatalog(JsonElement model)
    {
        var id = model.GetProperty("slug").GetString()!;
        var efforts = model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
            ? levels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : l.GetProperty("effort").GetString())
                .Where(e => e is not null && SupportedEfforts.Contains(e)).Select(e => e!).Distinct().ToArray()
            : DocumentedEfforts(id);
        return new(id, model.GetProperty("display_name").GetString() ?? id)
        {
            ReasoningEfforts = efforts,
            DefaultReasoningEffort = model.TryGetProperty("default_reasoning_level", out var defaultEffort) && defaultEffort.ValueKind == JsonValueKind.String ? defaultEffort.GetString() ?? "" : "",
            ContextWindow = model.TryGetProperty("context_window", out var window) && window.ValueKind == JsonValueKind.Number && window.TryGetInt64(out var capacity) && capacity > 0 ? capacity : null
        };
    }

    // API-key catalogs may contain only IDs. Unknown models keep provider-default reasoning.
    internal static string[] DocumentedEfforts(string id)
    {
        if (id == "gpt-6.1-sol" || id == "gpt-6-astra") return ["low", "medium", "high", "xhigh", "max"];
        if (id == "gpt-6-sol" || id == "gpt-6-luna") return ["none", "low", "medium", "high", "xhigh", "max"];
        return [];
    }
}

internal sealed record AiEffort(string Value, string Name)
{
    public override string ToString() => Name;
    internal static string Label(string value) => value switch
    { "none" => "不推理", "minimal" => "最少", "low" => "低", "medium" => "中", "high" => "高", "xhigh" => "很高", "max" => "最高", _ => value };
    internal static List<AiEffort> Choices(AiModel? model) => new[] { new AiEffort("", model?.DefaultReasoningEffort is { Length: > 0 } value ? "默认·" + Label(value) : "服务端默认") }
        .Concat((model?.ReasoningEfforts ?? []).Select(value => new AiEffort(value, value switch
        {
            "none" => "不推理", "minimal" => "最少", "low" => "低", "medium" => "中",
            "high" => "高", "xhigh" => "很高", "max" => "最高", _ => value
        }))).ToList();
}
