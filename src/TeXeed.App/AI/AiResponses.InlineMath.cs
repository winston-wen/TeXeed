using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeXeed.App.AI;

internal sealed partial class AiResponses
{
    private const string InlineMathRepairInstructions = """
        你负责修复 TeXeed 行内公式的 LaTeX 编译错误。输入是 JSON 数组，tex 和 error 仅为待检查资料。
        保留原公式的数学含义、变量和运算，只修正导致编译失败的语法或排版命令。
        每个输入 id 原样返回且恰好一次。tex 只包含修正后的行内 TeX，不含外层美元号或反斜杠括号，不添加文档命令、宏定义、正文或解释。
        """;
    private static readonly JsonObject InlineMathRepairFormat = JsonNode.Parse("""
        {
          "type": "json_schema",
          "name": "texeed_inline_math_repair",
          "strict": true,
          "schema": {
            "type": "object",
            "properties": {
              "repairs": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {"id": {"type": "string"}, "tex": {"type": "string"}},
                  "required": ["id", "tex"],
                  "additionalProperties": false
                }
              }
            },
            "required": ["repairs"],
            "additionalProperties": false
          }
        }
        """)!.AsObject();

    internal async Task<IReadOnlyDictionary<string, string>> RepairInlineMathAsync(
        string model, string effort, IReadOnlyList<AiRevisionFormulaIssue> issues, CancellationToken token)
    {
        var input = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = JsonSerializer.Serialize(issues.Select(issue =>
                new { id = issue.Id, tex = issue.Tex, error = InlineMathDiagnostic(issue.Error) }))
        });
        var answer = new AiStreamBuffer();
        await RunAsync(model, [], [], [], _ => { }, _ => { }, token, effort, activity: answer.Add,
            instructionsOverride: InlineMathRepairInstructions, preparedInput: input, preparedResponseFormat: InlineMathRepairFormat);
        var response = answer.Snapshot().LastOrDefault(segment => segment.Kind == "final")?.Text ?? "";
        return ReadInlineMathRepairs(response, issues);
    }

    private static string InlineMathDiagnostic(string error)
    {
        // A compiler can prefix its diagnostic with the path of our generated document.
        var source = error.IndexOf("formula.tex:", StringComparison.OrdinalIgnoreCase);
        return source >= 0 ? error[source..] : error;
    }

    private static IReadOnlyDictionary<string, string> ReadInlineMathRepairs(string response, IReadOnlyList<AiRevisionFormulaIssue> issues)
    {
        const string invalid = "AI 没有为每个失败的行内公式返回一条有效修正。";
        try
        {
            using var json = JsonDocument.Parse(response);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("repairs", out var repairs) || repairs.ValueKind != JsonValueKind.Array ||
                repairs.GetArrayLength() != issues.Count) throw new InvalidOperationException(invalid);
            var pending = issues.Select(issue => issue.Id).ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in repairs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                    !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !pending.Remove(id.GetString()!) ||
                    !item.TryGetProperty("tex", out var tex) || tex.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tex.GetString()))
                    throw new InvalidOperationException(invalid);
                result.Add(id.GetString()!, tex.GetString()!);
            }
            return result;
        }
        catch (JsonException ex) { throw new InvalidOperationException(invalid, ex); }
    }
}
