using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiHeadingPromptAsync()
    {
        new AiCredentials { UseApiKey = true, ApiKey = "isolated-heading-test-not-a-real-key" }.Save();
        const string previous = "# 1. 已有标题\n\n已有正文。\n\n## 1.1 已编号小节\n\n原有内容。";
        var history = new[] { new AiTurn("先前的问题", previous) };
        foreach (var mode in new[] { "read", "workspace", "resume", "repair" })
        {
            using var handler = new HeadingPromptHandler(previous);
            using var connection = new AiConnection(handler);
            var output = new AiStreamBuffer();
            var checks = 0;
            AiToolRunner? tools = mode == "workspace" ? (_, _, _) => Task.FromResult("测试工作区") : null;
            Func<string, CancellationToken, Task<AiMathCheck>>? proofread = mode == "repair"
                ? (_, _) => Task.FromResult(++checks == 1 ? new AiMathCheck(1, "测试公式诊断", true) : new AiMathCheck(1)) : null;
            await new AiResponses(connection).RunAsync("test-model", [], history, [], _ => { }, _ => { }, CancellationToken.None,
                toolRunner: tools, activity: output.Add, proofread: proofread, resuming: mode == "resume");
            if (handler.Payloads.Count != (mode == "repair" ? 2 : 1)) throw new Exception($"Unexpected request count for {mode}");
            foreach (var payload in handler.Payloads)
            {
                var instruction = payload["instructions"]!.GetValue<string>();
                if (!instruction.Contains("不要添加一个包裹全文的统一一级标题") || !instruction.Contains("保留原有标题结构"))
                    throw new Exception($"Heading policy missing from {mode} request");
                if (!instruction.Contains("新生成的分节标题只写标题文字") ||
                    !instruction.Contains("不自行添加数字、中文数字或罗马数字等标题序号") ||
                    !instruction.Contains("列表项编号和公式编号不受影响") ||
                    !instruction.Contains("已经写入的标题编号") || !instruction.Contains("用户明确要求时按用户要求处理"))
                    throw new Exception($"Automatic heading numbering or preservation policy missing from {mode} request");
                var input = payload["input"]!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                if (!input.Contains("1. 已有标题") || !input.Contains("1.1 已编号小节") || !input.Contains("已有正文"))
                    throw new Exception($"Existing numbered answer changed in {mode} request");
            }
            if (output.Snapshot().Where(s => s.Kind == "final").Single().Text != previous)
                throw new Exception($"Existing heading was rewritten in {mode} response");
            if (history[0].Answer != previous) throw new Exception($"Stored answer changed in {mode} request");
            if (mode == "repair" && !handler.Payloads[1]["instructions"]!.GetValue<string>().Contains("保持原意、推理与非公式内容"))
                throw new Exception("Math-only repair no longer preserves the existing answer");
        }
        using (var handler = new HeadingPromptHandler("压缩结果"))
        using (var connection = new AiConnection(handler))
        {
            await new AiResponses(connection).RunAsync("test-model", [], [], [], _ => { }, _ => { }, CancellationToken.None,
                instructionsOverride: "内部会话压缩", preparedInput: new JsonArray());
            var instruction = handler.Payloads.Single()["instructions"]!.GetValue<string>();
            if (instruction != "内部会话压缩") throw new Exception("User-facing heading policy leaked into internal session compaction");
        }
        Console.WriteLine("AI heading prompt: read, workspace, resume, math repair and internal compaction passed; existing answer preserved.");
    }

    private sealed class HeadingPromptHandler(string answer) : HttpMessageHandler
    {
        internal List<JsonNode> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payloads.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!);
            var stream = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = answer }) +
                "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
