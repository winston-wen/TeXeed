using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Encodings.Web;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiSteeringAsync()
    {
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = new List<string>(); var accepted = new List<string>();
        var controller = new AiSteeringController((ids, _) => { before.AddRange(ids); return Task.CompletedTask; },
            (ids, _) => { accepted.AddRange(ids); return Task.CompletedTask; });
        using (var handler = new SteeringHandler(entered))
        using (var connection = new AiConnection(handler))
        {
            var segments = new AiStreamBuffer();
            var run = new AiResponses(connection).RunAsync("fixture", [new AiEntry { Question = "初始问题" }], [], [], _ => { }, _ => { }, CancellationToken.None,
                activity: segments.Add, steering: controller);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!controller.TryEnqueue(new("steer-1", "改用第二种方法", [new AiEntry { Question = "引用备注", Sources = [new("a.tex", 1, 1, 0, "引用快照", "rev")] }]))) throw new Exception("Active turn rejected steering");
            if (controller.TryEnqueue(new("steer-1", "重复引导", []))) throw new Exception("Duplicate steering ID was accepted");
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            if (!before.SequenceEqual(["steer-1"]) || !accepted.SequenceEqual(before)) throw new Exception("Steering callbacks did not persist and confirm once");
            var secondBody = UnescapeJson(handler.Bodies.Last());
            if (handler.Bodies.Count != 2 || !secondBody.Contains("改用第二种方法") || !secondBody.Contains("引用快照") || !secondBody.Contains("先前片段")) throw new Exception("Steering request lost partial text or frozen reference");
            if (!string.Concat(segments.Snapshot().Select(x => x.Text)).Contains("先前片段")) throw new Exception("Steering erased streamed partial output");
            if (controller.TryEnqueue(new("late", "过晚", []))) throw new Exception("Completed turn accepted steering");
        }

        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new List<string>();
        var toolController = new AiSteeringController((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        using var toolHandler = new SteeringToolHandler();
        using var toolConnection = new AiConnection(toolHandler);
        var toolRun = new AiResponses(toolConnection).RunAsync("fixture", [], [], [], _ => { }, _ => { }, CancellationToken.None,
            toolRunner: async (name, _, token) =>
            {
                if (name == "list_files") return "fixture workspace";
                invoked.Add(name); toolStarted.TrySetResult(); await toolRelease.Task.WaitAsync(token); return "fixture result";
            }, steering: toolController);
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!toolController.TryEnqueue(new("steer-2", "停止接下来的工具", []))) throw new Exception("Tool turn rejected steering");
        if (!toolController.TryEnqueue(new("steer-3", "并保留已完成的结果", []))) throw new Exception("Second steering was not queued");
        toolRelease.SetResult(); await toolRun.WaitAsync(TimeSpan.FromSeconds(5));
        var toolBody = UnescapeJson(toolHandler.Bodies[1]);
        if (invoked.Count != 1 || !toolBody.Contains("fixture result") || !toolBody.Contains("用户已发送新引导") || !toolBody.Contains("call-2") || !toolBody.Contains("停止接下来的工具") || !toolBody.Contains("并保留已完成的结果") || toolBody.IndexOf("停止接下来的工具", StringComparison.Ordinal) >= toolBody.IndexOf("并保留已完成的结果", StringComparison.Ordinal)) throw new Exception("Tool interruption lost results, reordered steering, repeated execution or broke call/output pairing");

        var unknownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>(); var confirmed = new List<string>();
        var unknownController = new AiSteeringController((ids, _) => { sent.AddRange(ids); return Task.CompletedTask; }, (ids, _) => { confirmed.AddRange(ids); return Task.CompletedTask; });
        using var unknownHandler = new SteeringHandler(unknownEntered, failAfterSteering: true);
        using var unknownConnection = new AiConnection(unknownHandler);
        var unknownRun = new AiResponses(unknownConnection, (_, _) => Task.CompletedTask).RunAsync("fixture", [], [], [], _ => { }, _ => { }, CancellationToken.None, steering: unknownController);
        await unknownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!unknownController.TryEnqueue(new("unknown", "新指令", []))) throw new Exception("Unknown-state test could not enqueue steering");
        try { await unknownRun.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Disconnected steering was unexpectedly confirmed"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("状态不明", StringComparison.Ordinal)) { }
        if (unknownHandler.Bodies.Count != 2 || !sent.SequenceEqual(["unknown"]) || confirmed.Count != 0) throw new Exception("Disconnected steering was retried or confirmed without completion");
    }
    private static string UnescapeJson(string value) => JsonNode.Parse(value)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private sealed class SteeringHandler(TaskCompletionSource entered, bool failAfterSteering = false) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (Bodies.Count == 1)
            {
                var data = Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"先前片段\"}\n\n");
                return new(HttpStatusCode.OK) { Content = new StreamContent(new SteeringStalledStream(data, entered)) };
            }
            return failAfterSteering ? new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n", Encoding.UTF8, "text/event-stream") } : SteeringSuccess();
        }
    }
    private sealed class SteeringToolHandler : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if (Bodies.Count > 1) return SteeringSuccess();
            var output = new JsonArray(
                new JsonObject { ["type"] = "function_call", ["name"] = "read_source", ["call_id"] = "call-1", ["arguments"] = "{}" },
                new JsonObject { ["type"] = "function_call", ["name"] = "read_source", ["call_id"] = "call-2", ["arguments"] = "{}" });
            var json = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = output } };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + json.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static HttpResponseMessage SteeringSuccess() => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"最终回复\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
    private sealed class SteeringStalledStream(byte[] data, TaskCompletionSource entered) : Stream
    {
        private int offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int index, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset < data.Length) { var count = Math.Min(buffer.Length, data.Length - offset); data.AsMemory(offset, count).CopyTo(buffer); offset += count; return count; }
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
