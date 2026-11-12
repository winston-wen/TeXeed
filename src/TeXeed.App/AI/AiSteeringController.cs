namespace TeXeed.App.AI;

internal sealed record AiSteeringMessage(string Id, string Prompt, IReadOnlyList<AiEntry> Entries);

// The host must persist a message before offering it to the running turn.
internal sealed class AiSteeringController(
    Func<IReadOnlyList<string>, CancellationToken, Task> beforeSendAsync,
    Func<IReadOnlyList<string>, CancellationToken, Task> afterAcceptedAsync)
{
    private readonly object gate = new();
    private readonly Queue<AiSteeringMessage> pending = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private CancellationTokenSource? request;
    private bool closed;

    internal bool TryEnqueue(AiSteeringMessage message)
    {
        lock (gate)
        {
            if (closed || string.IsNullOrWhiteSpace(message.Id) || !seen.Add(message.Id)) return false;
            pending.Enqueue(message);
            request?.Cancel();
            return true;
        }
    }
    internal bool HasPending { get { lock (gate) return pending.Count != 0; } }
    internal IReadOnlyList<AiSteeringMessage> Drain()
    {
        lock (gate)
        {
            var result = pending.ToArray();
            pending.Clear();
            return result;
        }
    }
    internal CancellationTokenSource BeginRequest(CancellationToken token)
    {
        lock (gate)
        {
            var result = CancellationTokenSource.CreateLinkedTokenSource(token);
            request = result;
            if (pending.Count != 0) result.Cancel();
            return result;
        }
    }
    internal void EndRequest(CancellationTokenSource source)
    {
        lock (gate) { if (ReferenceEquals(request, source)) request = null; }
        source.Dispose();
    }
    internal bool TryFinish()
    {
        lock (gate)
        {
            if (pending.Count != 0) return false;
            closed = true;
            return true;
        }
    }
    internal void Close() { lock (gate) closed = true; }
    internal Task BeforeSendAsync(IReadOnlyList<string> ids, CancellationToken token) => beforeSendAsync(ids, token);
    internal Task AfterAcceptedAsync(IReadOnlyList<string> ids, CancellationToken token) => afterAcceptedAsync(ids, token);
}
