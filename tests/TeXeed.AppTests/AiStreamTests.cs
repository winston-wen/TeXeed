using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TeXeed.App.AI;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiStreamRecoveryAsync(Action<bool, string> check)
    {
        var files = new[] { new ProjectFile("C:/project/main.tex", "main.tex", ProjectFileKind.Tex, "A compact proof.") };
        static JsonObject Message(string id, string text, string phase = "final_answer") => new()
        {
            ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["phase"] = phase,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
        };
        static JsonObject Call(string id, string name, string arguments = "{}") => new()
        { ["id"] = id, ["type"] = "function_call", ["name"] = name, ["call_id"] = "call-" + id, ["arguments"] = arguments };
        static string Event(string type, JsonNode? item = null, int? index = null) =>
            "data: " + new JsonObject { ["type"] = type, ["item"] = item?.DeepClone(), ["output_index"] = index }.ToJsonString() + "\n\n";
        static string Complete(params JsonNode[] items) =>
            "data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(items.Select(n => n.DeepClone()).ToArray()) } }.ToJsonString() + "\n\n";
        static string Delta(string? id, int index, string value, int part = 0) =>
            "data: " + new JsonObject { ["type"] = "response.output_text.delta", ["item_id"] = id, ["output_index"] = index, ["content_index"] = part, ["delta"] = value }.ToJsonString() + "\n\n";

        var first = Message("progress", "先检查项目。", "commentary");
        var final = Message("answer", "已修复并通过编译。");
        using (var handler = new AiSequenceHandler((_, _) => Delta("progress", 0, "先检查") + Event("response.output_item.done", first, 0)
            + Event("response.output_item.done", final, 1) + Complete(final)))
        using (var connection = new AiConnection(handler))
        {
            var buffer = new AiStreamBuffer(); var trace = new StringBuilder();
            await new AiResponses(connection).RunAsync("test-model", [], [], files, value => trace.Append(value), _ => { }, CancellationToken.None, activity: buffer.Add);
            var segments = buffer.Snapshot().Where(s => s.Kind is "text" or "final").ToArray();
            check(segments.Select(s => s.Text).SequenceEqual(new[] { "先检查项目。", "已修复并通过编译。" }), "Mixed streamed and terminal-only messages lost or duplicated text");
            check(segments[0].Kind == "text" && segments[1].Kind == "final", "Progress was marked final instead of the terminal answer");
            check(trace.ToString() == "先检查项目。已修复并通过编译。", "Terminal suffix reconciliation duplicated streamed text");
        }

        var multipart = Message("late-id", "开始");
        multipart["content"]!.AsArray().Add(new JsonObject { ["type"] = "output_text", ["text"] = "补充内容" });
        using (var connection = new AiConnection(new AiSequenceHandler((_, _) => Delta(null, 0, "开") + Event("response.output_item.done", multipart, 0) + Complete(multipart))))
        {
            var buffer = new AiStreamBuffer();
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, activity: buffer.Add);
            check(buffer.Snapshot().Single(s => s.Kind == "final").Text == "开始补充内容", "Output-index aliasing or terminal-only content part duplicated/lost a message");
        }

        var search = Call("search", "search_source", "{\"query\":\"compact proof\"}");
        using (var handler = new AiSequenceHandler((round, _) => round == 0
            ? Event("response.output_item.done", first, 0) + Event("response.output_item.done", search, 1) + Complete(first)
            : Complete(final)))
        using (var connection = new AiConnection(handler))
        {
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None);
            var next = handler.Inputs[1]["input"]!.AsArray();
            check(handler.Inputs.Count == 2 && next.Count(n => n?["type"]?.ToString() == "function_call") == 1 && next.Count(n => n?["type"]?.ToString() == "function_call_output") == 1,
                "A nonempty but partial terminal envelope dropped or duplicated its completed tool call");
            check(next.Single(n => n?["type"]?.ToString() == "function_call_output")!["output"]!.ToString().Contains("main.tex:1"), "Recovered tool call did not execute");
        }

        static Task<string> Runner(string name, string arguments, CancellationToken token) => Task.FromResult(name switch
        { "list_files" => "主文件：main.tex", "replace_source" => "已修改 main.tex", "compile_project" => "编译成功", _ => "操作失败：未知工具" });
        var premature = Message("unverified", "源码已修改。");
        using (var handler = new AiSequenceHandler((round, _) => round switch
        { 0 => Complete(Call("edit", "replace_source")), 1 => Complete(premature), 2 => Complete(Call("verify", "compile_project")), _ => Complete(final) }))
        using (var connection = new AiConnection(handler))
        {
            var buffer = new AiStreamBuffer();
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, toolRunner: Runner, activity: buffer.Add);
            var segments = buffer.Snapshot();
            check(handler.Inputs.Count == 4 && handler.Inputs[2]["input"]!.ToJsonString().Contains("compile_project"), "Successful edits ended without a bounded verification reminder");
            check(segments.Single(s => s.Kind == "final").Text == final["content"]![0]!["text"]!.ToString(), "Premature answer remained final after verification continued");
        }
        using (var handler = new AiSequenceHandler((round, _) => round == 0 ? Complete(Call("edit", "replace_source")) : Complete(Message("blocked-" + round, "用户要求暂不编译，当前修改尚未验证。"))))
        using (var connection = new AiConnection(handler))
        {
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, toolRunner: Runner);
            check(handler.Inputs.Count == 3, "Verification reminder repeated instead of permitting an explicit limitation");
        }
        using (var handler = new AiSequenceHandler((_, _) => Complete(Message("explanation", "这是一段只读解释。"))))
        using (var connection = new AiConnection(handler))
        {
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, toolRunner: Runner);
            check(handler.Inputs.Count == 1, "Read-only explanation was forced into editing or compilation");
        }
        using (var handler = new AiSequenceHandler((round, _) => Complete(Message("progress-" + round, "正在检查。", "commentary"))))
        using (var connection = new AiConnection(handler))
        {
            var failed = false; var buffer = new AiStreamBuffer();
            try { await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, toolRunner: Runner, activity: buffer.Add); }
            catch (InvalidOperationException ex) { failed = ex.Message.Contains("行动说明"); }
            check(failed && handler.Inputs.Count == 3 && buffer.Snapshot().All(s => s.Kind != "final"), "Commentary-only output was marked final or continued without a bound");
        }
        using (var handler = new AiSequenceHandler((round, _) => Complete(Message("recover-" + round, round == 0 ? "正在检查。" : "解释已完成。", round == 0 ? "commentary" : "final_answer"))))
        using (var connection = new AiConnection(handler))
        {
            var buffer = new AiStreamBuffer();
            await new AiResponses(connection).RunAsync("test-model", [], [], files, _ => { }, _ => { }, CancellationToken.None, activity: buffer.Add);
            check(handler.Inputs.Count == 2 && buffer.Snapshot().Single(s => s.Kind == "final").Text == "解释已完成。", "Commentary continuation lost its eventual final answer");
        }
    }

    private sealed class AiSequenceHandler(Func<int, JsonNode, string> response) : HttpMessageHandler
    {
        internal List<JsonNode> Inputs { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Inputs.Add(input);
            if (Inputs.Count > 8) throw new InvalidOperationException("Unexpected unbounded AI continuation in fixture");
            return new(HttpStatusCode.OK) { Content = new StringContent(response(Inputs.Count - 1, input), Encoding.UTF8, "text/event-stream") };
        }
    }
}
