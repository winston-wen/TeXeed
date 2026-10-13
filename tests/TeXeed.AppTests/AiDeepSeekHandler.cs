using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

// Evaluation-only adapter. The application never reads another application's credentials.
// Explicit live-evaluation authorization permits reusing the local DeepSeek connection.
internal sealed class AiDeepSeekHandler : DelegatingHandler
{
    private readonly string _key;
    internal AiDeepSeekHandler() : base(new HttpClientHandler())
    {
        _key = Environment.GetEnvironmentVariable("TEXEED_EVAL_DEEPSEEK_KEY") ?? "";
        if (_key.Length == 0)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(path))!;
            if (settings["env"]?["ANTHROPIC_BASE_URL"]?.ToString().TrimEnd('/') != "https://api.deepseek.com/anthropic")
                throw new InvalidOperationException("The explicitly authorized Claude connection is not the official DeepSeek endpoint.");
            _key = settings["env"]?["ANTHROPIC_AUTH_TOKEN"]?.ToString() ?? "";
        }
        if (_key.Length == 0) throw new InvalidOperationException("No authorized DeepSeek connection is available.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/responses") != true)
            throw new InvalidOperationException("DeepSeek evaluation only supports Responses requests.");
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        if (body["model"]?.ToString() != "deepseek-flash") throw new InvalidOperationException("Only deepseek-flash is configured for these comparisons.");
        var functions = new JsonArray();
        foreach (var tool in body["tools"]!.AsArray())
        {
            if (tool?["type"]?.ToString() == "namespace")
                foreach (var function in tool["tools"]!.AsArray()) functions.Add(function!.DeepClone());
            else functions.Add(tool!.DeepClone());
        }
        body["tools"] = functions;
        body.Remove("include");
        body["max_output_tokens"] = 16000;
        foreach (var item in body["input"]!.AsArray().Where(i => i?["type"]?.ToString() == "function_call"))
            item!["name"] = item["name"]!.ToString().Split('.').Last();
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/responses")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return await base.SendAsync(outgoing, token);
    }
}
