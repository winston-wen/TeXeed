using System.Text;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiFormContextAsync()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static string Complete(string text) => "data: " + new JsonObject
        {
            ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(new JsonObject
            { ["type"] = "message", ["role"] = "assistant", ["phase"] = "final_answer", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }) }
        }.ToJsonString() + "\n\n";
        static bool IsSummary(JsonNode request) => request["instructions"]!.ToString().Contains("会话状态整理器");
        static AiTurn Submission(AiTurn turn, string value) => AiForms.WithSubmission(turn, new AiForms.Submission("form-source", new() { ["answer"] = value }));
        new AiCredentials { ApiKey = "fixture-key-never-send", UseApiKey = true }.Save();

        var plain = Submission(new AiTurn("plain form", "plain response"), "PLAIN_VALUE_12345");
        var attachment = new AiEntry { Files = [new("form-input.txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes("ATTACHMENT_BODY_123")))] };
        var withAttachment = Submission(new AiTurn("form with attachment", "response") { SentAnnotations = [attachment] }, "ATTACHED_VALUE_69876");
        using var normalHandler = new AiSequenceHandler((_, _) => Complete("done"));
        using var normalConnection = new AiConnection(normalHandler);
        await new AiResponses(normalConnection).RunAsync("fixture", [new AiEntry { Question = "next message" }], [plain, withAttachment], [], _ => { }, _ => { }, CancellationToken.None);
        var input = normalHandler.Inputs.Single()["input"]!.AsArray();
        var users = input.Where(node => node?["role"]?.ToString() == "user").Select(node => node!.ToJsonString()).ToArray();
        Check(users.Any(s => s.Contains("plain form") && s.Contains("PLAIN_VALUE_12345")), "Subsequent request lost the plain form submission");
        Check(users.Any(s => s.Contains("form with attachment") && s.Contains("ATTACHED_VALUE_69876") && s.Contains("ATTACHMENT_BODY_123")), "Subsequent multimodal history lost the form or attachment");

        var longTurn = Submission(new AiTurn("long form", new string('x', 9000)), "BEFORE_FORM_123");
        using var compactHandler = new AiSequenceHandler((_, body) => Complete(IsSummary(body) ? "summary" : "done"));
        using var compactConnection = new AiConnection(compactHandler);
        AiContextMemory? memory = null;
        await new AiResponses(compactConnection).RunAsync("fixture", [new AiEntry { Question = "continue" }], [longTurn], [], _ => { }, _ => { }, CancellationToken.None,
            compactThreshold: 4000, compacted: m => memory = m);
        Check(memory is not null && compactHandler.Inputs.Where(IsSummary).Any(body => body["input"]!.ToJsonString().Contains("BEFORE_FORM_123")), "Compaction source omitted the form submission");

        var changed = Submission(longTurn, "AFTER_FORM_456");
        using var changedHandler = new AiSequenceHandler((_, body) => Complete(IsSummary(body) ? "updated summary" : "done"));
        using var changedConnection = new AiConnection(changedHandler);
        await new AiResponses(changedConnection).RunAsync("fixture", [new AiEntry { Question = "continue" }], [changed], [], _ => { }, _ => { }, CancellationToken.None,
            compactThreshold: 4000, memory: memory);
        Check(changedHandler.Inputs.Where(IsSummary).Any(body => body["input"]!.ToJsonString().Contains("AFTER_FORM_456")), "Changed form values reused an incompatible context fingerprint");
        Console.WriteLine("PASS: form values in real history, attachments, compaction and fingerprint.");
    }
}
