using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static class AiLiveCompaction
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(outputIndex >= 0 ? args[outputIndex + 1] : "artifacts/luna-compaction");
        Directory.CreateDirectory(output);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var token = timeout.Token;
            if (Environment.GetEnvironmentVariable("TEXEED_HOME") is not null) throw new InvalidOperationException("Real account required; do not set TEXEED_HOME.");
            var credentials = AiCredentials.Load();
            if (credentials.UseApiKey || credentials.Active is null) throw new InvalidOperationException("This test requires the signed-in ChatGPT account.");
            using var counter = new Counter(output);
            using var connection = new AiConnection(counter);
            var catalog = await connection.ModelsAsync(token);
            var model = catalog.SingleOrDefault(m => m.Id == "gpt-5.6-luna") ?? throw new InvalidOperationException("GPT-5.6 Luna unavailable; no substitution made.");
            Console.WriteLine("Testing " + model.Id + " / low; history threshold deliberately lowered to 20000 chars.");
            var log = string.Concat(Enumerable.Repeat("TeX diagnostic observation: chapter input scanned; no modification performed.\n", 80));
            var history = Enumerable.Range(0, 5).Select(i => new AiTurn(i == 0 ? "记住验收口令 Amber-731，入口是 templates/book-root.tex；禁止删除任何正文或替换原字体。" : "继续检查记录 " + i,
                "已读取资料，尚未修复，尚未编译验证。\n" + log)).ToArray();
            AiContextMemory? memory = null;
            var events = new List<AiStreamEvent>(); var answer = new StringBuilder();
            var agent = new AiResponses(connection);
            await agent.RunAsync(model.Id, [new() { Question = "根据会话历史，简短给出验收口令、编译入口、禁止事项及是否已通过编译。不得调用工具。" }], history, [],
                text => answer.Append(text), _ => { }, token, "low", activity: e => events.Add(e),
                instructionsOverride: "用简短中文准确回答历史事实，不执行工具。", contextWindow: model.ContextWindow,
                compacted: m => memory = m, compactThreshold: 20000);
            if (memory is null || memory.TurnIds.Length != 3 || !events.Any(e => e.Text.StartsWith("已压缩"))) throw new Exception("Automatic compaction did not trigger.");
            var first = answer.ToString();
            if (!first.Contains("Amber-731") || !first.Contains("templates/book-root.tex") || !first.Contains("正文") || !first.Contains("字体") || !(first.Contains("未") || first.Contains("没有"))) throw new Exception("Compacted context lost a required fact: " + first);
            var roundtrip = JsonSerializer.Deserialize<AiContextMemory>(JsonSerializer.Serialize(memory))!;
            answer.Clear();
            await agent.RunAsync(model.Id, [new() { Question = "只回复历史中的验收口令和编译入口，不调用工具。" }], history, [], text => answer.Append(text), _ => { }, token, "low",
                instructionsOverride: "准确回答历史事实。", memory: roundtrip, compactThreshold: 20000);
            if (!answer.ToString().Contains("Amber-731") || !answer.ToString().Contains("templates/book-root.tex")) throw new Exception("Persisted summary was not retained.");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, model = model.Id, requests = counter.Requests, memory, first, followup = answer.ToString(), events }, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"PASS: automatic compaction, constraints, task state and persisted follow-up; {counter.Requests} Luna requests.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    private sealed class Counter(string output) : DelegatingHandler(new HttpClientHandler())
    {
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/responses"))
            {
                if (++Requests > 8) throw new InvalidOperationException("Luna test request cap exceeded.");
                var body = await request.Content!.ReadAsStringAsync(token);
                var json = JsonNode.Parse(body)!;
                if (json["model"]?.ToString() != "gpt-5.6-luna" || json["reasoning"]?["effort"]?.ToString() != "low") throw new InvalidOperationException("Unexpected model/effort.");
                await File.WriteAllTextAsync(Path.Combine(output, $"request-{Requests}.json"), body, token);
                Console.WriteLine("Luna request " + Requests);
            }
            return await base.SendAsync(request, token);
        }
    }
}
