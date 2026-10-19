using System.IO;
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
    private AiTurnCheckpoint(string project, string conversationId, string title, AiTurn turn, AiStreamBuffer buffer)
    { _path = PathFor(project); _pending = new(conversationId, title, turn); _buffer = buffer; }
    internal static string PathFor(string project) => AiStorage.NotebookPath(project) + ".pending";
    internal static async Task<AiTurnCheckpoint> StartAsync(string project, string conversationId, string title, AiTurn turn, AiStreamBuffer buffer)
    {
        var checkpoint = new AiTurnCheckpoint(project, conversationId, title, turn, buffer);
        // Acceptance is durable before clearing the composer or starting network/render work.
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
    internal void Commit() { if (File.Exists(_path)) File.Delete(_path); }
    internal static void RetireSaved(string project, AiNotebook book)
    {
        var path = PathFor(project);
        if (!File.Exists(path)) return;
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(path));
        if (pending is not null && book.Conversations.Any(c => c.Id == pending.ConversationId && c.Turns.Any(t => t.Id == pending.Turn.Id))) File.Delete(path);
    }
    internal static void Recover(string project, AiNotebook book)
    {
        var path = PathFor(project);
        if (!File.Exists(path)) return;
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(path)) ?? throw new InvalidDataException("未完成对话恢复文件为空。");
        var conversation = book.Conversations.FirstOrDefault(c => c.Id == pending.ConversationId);
        if (conversation is null) { conversation = new() { Id = pending.ConversationId, Title = pending.Title }; book.Conversations.Add(conversation); }
        if (!conversation.Turns.Any(t => t.Id == pending.Turn.Id)) conversation.Turns.Add(pending.Turn);
        book.ActiveConversationId = conversation.Id;
        // Do not delete here: only a successful normal notebook save retires this journal.
    }
}
