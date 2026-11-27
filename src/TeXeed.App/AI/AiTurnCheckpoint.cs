using System.IO;
using System.Diagnostics;
using System.Text.Json;
using TeXeed.App.Services;

namespace TeXeed.App.AI;

/// <summary>A crash journal written independently of WPF layout and the UI dispatcher.</summary>
internal sealed class AiTurnCheckpoint
{
    internal sealed record Pending(string ConversationId, string Title, AiTurn Turn);
    private readonly string _path;
    private readonly Pending _pending;
    private readonly AiStreamBuffer _buffer;
    private readonly CancellationTokenSource _stop = new();
    private Task _worker = Task.CompletedTask;
    private long _writtenVersion = -1;
    private AiTurnCheckpoint(string project, string conversationId, string title, AiTurn turn, AiStreamBuffer buffer, bool scoped)
    { _path = scoped ? PathFor(project, conversationId) : PathFor(project); _pending = new(conversationId, title, turn); _buffer = buffer; }
    internal static string PathFor(string project) => AiStorage.NotebookPath(project) + ".pending";
    internal static string PathFor(string project, string conversationId) => PathFor(project) + "." + AiStorage.Hash(conversationId);
    internal static IEnumerable<string> PathsFor(string project)
    {
        var path = PathFor(project);
        if (File.Exists(path)) yield return path;
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*"))
                if (!file.EndsWith(".tmp", StringComparison.Ordinal)) yield return file;
    }
    internal static async Task<AiTurnCheckpoint> StartAsync(string project, string conversationId, string title, AiTurn turn, AiStreamBuffer buffer, bool scoped = false)
    {
        var checkpoint = new AiTurnCheckpoint(project, conversationId, title, turn, buffer, scoped);
        // Persist the accepted question before network work and draft removal.
        await Task.Run(checkpoint.Write);
        checkpoint._worker = Task.Run(checkpoint.RunAsync);
        return checkpoint;
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                try { Write(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry, retaining the last atomic checkpoint. */ }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private void Write()
    {
        var version = _buffer.Version;
        if (version == _writtenVersion) return;
        var segments = _buffer.Snapshot();
        var partial = string.Join("\n\n", segments.Where(s => s.Kind is "text" or "final").Select(s => s.Text));
        var turn = _pending.Turn with { Answer = "[本轮未完成]\n" + (partial.Length > 0 ? partial : "尚未收到回答。"), Segments = segments };
        if (_pending.Turn.InputAttemptId is { } attempt)
        {
            foreach (var segment in segments) segment.Id = attempt + "-" + segment.Id;
            turn = _pending.Turn with
            {
                Answer = string.Join("\n\n", new[] { _pending.Turn.Answer, partial }.Where(s => s.Length > 0)),
                Segments = [.. _pending.Turn.Segments, .. segments]
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, _pending with { Turn = turn }); file.Flush(flushToDisk: true); }
            AtomicFile.Replace(temporary, _path);
            _writtenVersion = version;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal async Task StopAsync()
    {
        _stop.Cancel(); await _worker; _stop.Dispose();
        await Task.Run(Write);
    }
    private static Pending? ReadPending(string path)
    {
        var text = AtomicFile.ReadTextIfPresent(path);
        if (text is null) return null;
        var pending = JsonSerializer.Deserialize<Pending>(text);
        if (pending is not { ConversationId.Length: > 0, Turn.Id.Length: > 0 }) throw new InvalidDataException("未完成对话恢复文件缺少会话或轮次标识。");
        return pending;
    }

    // The notebook has already been published. Failure to retire the journal
    // must not roll back a committed in-memory revision. A later save retries it.
    private static bool DeferredCleanup(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or JsonException or InvalidDataException;

    private static void Retire(string path, Func<Pending, bool> saved)
    {
        try
        {
            if (ReadPending(path) is { } pending && saved(pending)) File.Delete(path);
        }
        catch (Exception ex) when (DeferredCleanup(ex))
        {
            Trace.TraceWarning("Deferred conversation journal cleanup: {0}", ex.Message);
        }
    }

    internal void Commit() => Retire(_path, pending => pending.ConversationId == _pending.ConversationId && pending.Turn.Id == _pending.Turn.Id && pending.Turn.InputAttemptId == _pending.Turn.InputAttemptId);
    internal static void RetireSaved(string project, AiNotebook book)
    {
        try
        {
            foreach (var path in PathsFor(project))
                Retire(path, pending => book.Conversations.Any(c => c.Id == pending.ConversationId && c.Turns.Any(t => t.Id == pending.Turn.Id &&
                    (pending.Turn.InputAttemptId is null || t.InputAttemptId != pending.Turn.InputAttemptId || t.EndReason != "input-resuming"))));
        }
        catch (Exception ex) when (DeferredCleanup(ex))
        {
            Trace.TraceWarning("Deferred conversation journal enumeration: {0}", ex.Message);
        }
    }
    internal static void Recover(string project, AiNotebook book)
    {
        foreach (var path in PathsFor(project))
        {
            // Retirement may remove a journal between directory enumeration and opening its handle.
            if (ReadPending(path) is not { } pending) continue;
            var conversation = book.Conversations.FirstOrDefault(c => c.Id == pending.ConversationId);
            if (conversation is null) { conversation = new() { Id = pending.ConversationId, Title = pending.Title }; book.Conversations.Add(conversation); }
            var index = conversation.Turns.FindIndex(t => t.Id == pending.Turn.Id);
            if (index < 0) conversation.Turns.Add(pending.Turn);
            else if (pending.Turn.InputAttemptId is { } attempt && conversation.Turns[index] is { EndReason: "input-resuming" } current &&
                current.InputAttemptId == attempt)
                conversation.Turns[index] = AiTurnInput.AppendToRevisions(conversation, current,
                    current with { Answer = pending.Turn.Answer, Segments = pending.Turn.Segments });
            conversation.LastSentAt = conversation.LastSend;
            if (!book.Conversations.Any(c => c.Id == book.ActiveConversationId)) book.ActiveConversationId = conversation.Id;
            // Only a successful notebook save retires this journal.
        }
    }
}
