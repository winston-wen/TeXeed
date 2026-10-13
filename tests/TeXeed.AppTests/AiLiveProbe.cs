using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static class AiLiveProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var token = timeout.Token;
            if (Environment.GetEnvironmentVariable("TEXEED_HOME") is not null) throw new InvalidOperationException("Live probe requires the real TeXeed profile, without TEXEED_HOME.");
            var saved = AiCredentials.Load();
            if (saved.UseApiKey || saved.Active is null) throw new InvalidOperationException("No active ChatGPT account; live probe will not use API billing.");
            using var connection = new AiConnection(new CountRequests());
            var models = await connection.ModelsAsync(token);
            var model = models.FirstOrDefault(m => m.Id == "gpt-6-luna") ?? models.FirstOrDefault(m => m.Id.EndsWith("-luna"));
            if (model is null) throw new InvalidOperationException("No Luna model available; refusing to substitute another model.");
            Console.WriteLine("Model: " + model.Id);
            Console.WriteLine("Saved model: " + saved.Model);
            if (args.Contains("--full"))
            {
                var useTools = args.Contains("--tools");
                var question = useTools ? "请调用 project.search_source 查找 hello，回复所在文件名和行号。" : "请只回复：你好。";
                TeXeed.Core.ProjectFile[] files = useTools ? [new("C:/fixture/main.tex", "main.tex", TeXeed.Core.ProjectFileKind.Tex, "first\nhello world\nlast")] : [];
                await new AiResponses(connection).RunAsync(model.Id, [new AiEntry { Question = question }], [new("一加一是多少？", "二。")], files, Console.Write, Console.WriteLine, token);
                Console.WriteLine("\nCompleted application request.");
                return 0;
            }
            var body = new JsonObject { ["model"] = model.Id, ["store"] = false, ["stream"] = true,
                ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "请只回复：你好。" }) };
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatGptLogin.Resource + "/responses") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await connection.BearerAsync(token));
            using var response = await connection.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw await AiFailure.FromResponseAsync(response, token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            var completed = false;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim(); if (data == "[DONE]") break;
                var evt = JsonNode.Parse(data)!;
                var type = evt["type"]?.ToString();
                if (type == "response.output_text.delta") Console.Write(evt["delta"]?.ToString());
                if (type == "response.completed") { completed = true; break; }
                if (type is "response.failed" or "response.incomplete" or "error") throw AiFailure.FromEvent(evt);
            }
            Console.WriteLine("\nCompleted minimal request: " + completed);
            return completed ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(ex is OperationCanceledException ? "Probe timed out/cancelled." : ex.Message); return 1; }
    }
    private sealed class CountRequests : DelegatingHandler
    {
        public CountRequests() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/responses")
            {
                var path = Path.Combine(AiStorage.Home, "live-test-0.3.1.count");
                using (await AiCredentials.LockAsync(token))
                {
                    var count = File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0;
                    if (count >= 20) throw new InvalidOperationException("Version 0.3.1 live-message budget exhausted (20).");
                    File.WriteAllText(path, (count + 1).ToString()); Console.WriteLine($"Live Luna request {count + 1}/20");
                }
            }
            var response = await base.SendAsync(request, token);
            Console.WriteLine($"{request.Method} {request.RequestUri!.AbsolutePath}: HTTP {(int)response.StatusCode}");
            if (request.RequestUri.AbsolutePath == "/v1/responses" && response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                var eventCounts = new Dictionary<string, int>();
                foreach (var line in body.Split('\n').Where(l => l.StartsWith("data:")))
                {
                    var data = line[5..].Trim();
                    if (data == "[DONE]") continue;
                    var json = JsonNode.Parse(data);
                    var eventType = json?["type"]?.ToString() ?? "unknown";
                    eventCounts[eventType] = eventCounts.GetValueOrDefault(eventType) + 1;
                    if (eventType == "response.output_item.done")
                        Console.WriteLine("Final item type: " + json?["item"]?["type"] + " name=" + json?["item"]?["name"]);
                    if (json?["type"]?.ToString() != "response.completed") continue;
                    var r = json["response"]!;
                    Console.WriteLine("Completed fields: " + string.Join(",", r.AsObject().Select(p => p.Key)));
                    if (r["output"] is JsonArray output)
                        foreach (var item in output)
                            Console.WriteLine("Output: " + item?["type"] + " name=" + item?["name"] + " namespace=" + item?["namespace"] + " fields=" + string.Join(",", item!.AsObject().Select(p => p.Key)));
                }
                Console.WriteLine("Event counts: " + string.Join(",", eventCounts.Select(p => p.Key + "=" + p.Value)));
                response.Content.Dispose();
                response.Content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            }
            return response;
        }
    }
}
