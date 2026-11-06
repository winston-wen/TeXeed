using System.IO;
using System.Text.Json;
using TeXeed.App.Services;

namespace TeXeed.App.AI;

internal sealed class AiGlobalCarryRound
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Question { get; set; } = "";
    public string Mode { get; set; } = "none";
    public string SelectionRevision { get; set; } = Guid.NewGuid().ToString("N");
}
internal sealed class AiGlobalPendingReply
{
    public string CommentId { get; set; } = "";
    public AiDocumentReply Reply { get; set; } = new();
    public AiDocumentComment ExpectedComment { get; set; } = new();
}
internal sealed class AiGlobalCarryConversation
{
    public string Project { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Deleted { get; set; }
    public List<AiGlobalCarryRound> Rounds { get; set; } = [];
    public List<AiDocumentComment> Comments { get; set; } = [];
    public List<AiGlobalPendingReply> PendingReplies { get; set; } = [];
}

/// <summary>Cross-project references and pending replies, stored separately per conversation.</summary>
internal sealed record AiGlobalReplyUpdate(string Project, string ConversationId, AiDocumentComment ExpectedComment, AiDocumentReply Reply);
internal sealed record AiGlobalLocalChange(string ConversationId, AiSharedDocument Before, AiSharedDocument After);
internal sealed class AiGlobalCarryTransaction
{
    public string Stage { get; set; } = "prepared";
    public string LocalProject { get; set; } = "";
    public List<AiGlobalLocalChange> LocalChanges { get; set; } = [];
    public List<AiGlobalReplyUpdate> Updates { get; set; } = [];
    public List<AiGlobalCarryConversation> Records { get; set; } = [];
}

internal sealed class AiGlobalCarryBusyException : IOException
{
    internal AiGlobalCarryBusyException() : base("另一个会话正在保存跨会话修改。") { }
}

internal static class AiGlobalCarry
{
    private static string Root => Path.Combine(AiStorage.Home, "shared-carry");
    private static string Key(string project, string conversation) => AiStorage.Hash(Path.GetFullPath(project).ToUpperInvariant()) + "-" + AiStorage.Hash(conversation);
    private static string PathFor(string project, string conversation) => Path.Combine(Root, Key(project, conversation) + ".json");
    private static FileStream Lock(string file)
    {
        Directory.CreateDirectory(Root);
        for (var i = 0; ; i++)
        {
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 100) { Thread.Sleep(10); }
        }
    }
    private static AiGlobalCarryConversation Read(string file) => JsonSerializer.Deserialize<AiGlobalCarryConversation>(File.ReadAllText(file)) ?? throw new InvalidDataException("无法读取跨会话携带记录。");
    private static void Write<T>(string file, T value)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, value); output.Flush(flushToDisk: true); }
            AtomicFile.Replace(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream GlobalLock()
    {
        Directory.CreateDirectory(Root);
        try { return new FileStream(Path.Combine(Root, "_global.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AiGlobalCarryBusyException(); }
    }
    private static void Recover()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var transactionFile in Directory.EnumerateFiles(Root, "*.transaction"))
        {
            var transaction = JsonSerializer.Deserialize<AiGlobalCarryTransaction>(File.ReadAllText(transactionFile))
                ?? throw new InvalidDataException("无法恢复跨会话评论事务。");
            if (transaction.Stage != "committed") continue;
            foreach (var record in transaction.Records) Write(PathFor(record.Project, record.ConversationId), record);
            File.Delete(transactionFile);
        }
    }

    // The caller owns the live conversation; pending replies are applied by ID exactly once.
    internal static bool Synchronize(string project, AiConversation conversation)
    {
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversation.Id);
        using var gate = Lock(file);
        var record = File.Exists(file) ? Read(file) : new AiGlobalCarryConversation { Project = Path.GetFullPath(project), ConversationId = conversation.Id };
        if (record.Deleted) throw new InvalidOperationException("会话正在删除或已删除，请重新打开项目后重试。");
        var unresolved = new List<AiGlobalPendingReply>();
        foreach (var pending in record.PendingReplies)
        {
            var comment = conversation.SharedDocument.Comments.FirstOrDefault(c => c.Id == pending.CommentId);
            if (comment is null) { unresolved.Add(pending); continue; }
            if (comment.Replies.Any(reply => reply.Id == pending.Reply.Id)) continue;
            if (JsonSerializer.Serialize(comment) != JsonSerializer.Serialize(pending.ExpectedComment)) { unresolved.Add(pending); continue; }
            comment.Replies.Add(pending.Reply.Snapshot());
        }
        record.Title = conversation.Title;
        var old = record.Rounds.ToDictionary(r => r.Id);
        record.Rounds = conversation.Turns.Select((turn, index) =>
        {
            if (!old.TryGetValue(turn.Id, out var round)) round = new AiGlobalCarryRound { Id = turn.Id, Mode = conversation.CommentCarry.GetValueOrDefault(turn.Id, "none") };
            round.Number = index + 1; round.Question = turn.Question;
            conversation.CommentCarry[turn.Id] = round.Mode;
            return round;
        }).ToList();
        record.Comments = conversation.SharedDocument.Comments.Select(comment => comment.Snapshot()).ToList();
        Write(file, record);
        return unresolved.Count == 0;
    }

    internal static void AcknowledgePersisted(string project, string conversationId)
    {
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversationId); if (!File.Exists(file)) return;
        var record = Read(file); if (record.PendingReplies.Count == 0) return;
        var notebookPath = AiStorage.NotebookPath(project); if (!File.Exists(notebookPath)) return;
        var book = JsonSerializer.Deserialize<AiNotebook>(File.ReadAllText(notebookPath)) ?? throw new InvalidDataException("无法读取已保存的源会话。");
        var conversation = book.Conversations.FirstOrDefault(c => c.Id == conversationId); if (conversation is null) return;
        if (record.PendingReplies.RemoveAll(p => conversation.SharedDocument.Comments.Any(c => c.Id == p.CommentId && c.Replies.Any(r => r.Id == p.Reply.Id))) > 0) Write(file, record);
    }

    internal static IReadOnlyList<AiGlobalCarryConversation> List()
    {
        using var global = GlobalLock(); Recover();
        if (!Directory.Exists(Root)) return [];
        var result = new List<AiGlobalCarryConversation>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        { using var gate = Lock(file); if (File.Exists(file)) { var record = Read(file); if (!record.Deleted) result.Add(record); } }
        return result;
    }

    internal static void SetMode(string project, string conversationId, string roundId, string mode)
    {
        if (mode is not ("none" or "temporary" or "long")) throw new ArgumentException("无效的携带方式。", nameof(mode));
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversationId); using var gate = Lock(file);
        var record = Read(file); var round = record.Rounds.FirstOrDefault(r => r.Id == roundId) ?? throw new InvalidOperationException("轮次不存在。 ");
        round.Mode = mode; round.SelectionRevision = Guid.NewGuid().ToString("N"); Write(file, record);
    }

    internal static bool ConsumeTemporary(string project, string conversationId, string roundId, string selectionRevision)
    {
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversationId); using var gate = Lock(file);
        if (!File.Exists(file)) return false;
        var record = Read(file); var round = record.Rounds.FirstOrDefault(r => r.Id == roundId);
        if (round is null || round.Mode != "temporary" || round.SelectionRevision != selectionRevision) return false;
        round.Mode = "none"; round.SelectionRevision = Guid.NewGuid().ToString("N"); Write(file, record); return true;
    }

    internal static bool TryAppendReply(string project, string conversationId, AiDocumentComment expected, AiDocumentReply reply)
    {
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversationId); using var gate = Lock(file);
        var record = Read(file); var comment = record.Comments.FirstOrDefault(c => c.Id == expected.Id);
        if (comment is null || JsonSerializer.Serialize(comment) != JsonSerializer.Serialize(expected)) return false;
        if (record.PendingReplies.Any(p => p.Reply.Id == reply.Id) || comment.Replies.Any(r => r.Id == reply.Id)) return false;
        comment.Replies.Add(reply.Snapshot()); record.PendingReplies.Add(new AiGlobalPendingReply { CommentId = comment.Id, Reply = reply.Snapshot(), ExpectedComment = expected.Snapshot() });
        Write(file, record); return true;
    }

    private static List<AiGlobalCarryConversation>? StageUpdates(IReadOnlyList<AiGlobalReplyUpdate> updates)
    {
        var records = new Dictionary<string, AiGlobalCarryConversation>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i]; var file = PathFor(update.Project, update.ConversationId);
            if (!File.Exists(file)) return null;
            if (!records.TryGetValue(file, out var record)) { record = Read(file); records.Add(file, record); }
            var comment = record.Comments.FirstOrDefault(c => c.Id == update.ExpectedComment.Id);
            if (comment is null || record.PendingReplies.Any(p => p.Reply.Id == update.Reply.Id) || comment.Replies.Any(r => r.Id == update.Reply.Id)) return null;
            var comparable = comment.Snapshot();
            foreach (var earlier in updates.Take(i).Where(x => string.Equals(PathFor(x.Project, x.ConversationId), file, StringComparison.OrdinalIgnoreCase) && x.ExpectedComment.Id == update.ExpectedComment.Id))
                comparable.Replies.RemoveAll(r => r.Id == earlier.Reply.Id);
            if (JsonSerializer.Serialize(comparable) != JsonSerializer.Serialize(update.ExpectedComment)) return null;
            var expected = comment.Snapshot(); comment.Replies.Add(update.Reply.Snapshot());
            record.PendingReplies.Add(new AiGlobalPendingReply { CommentId = comment.Id, Reply = update.Reply.Snapshot(), ExpectedComment = expected });
        }
        return records.Values.ToList();
    }

    internal sealed class Prepared : IDisposable
    {
        private FileStream? _gate;
        private readonly string _file;
        private readonly AiGlobalCarryTransaction _transaction;
        internal Prepared(FileStream gate, string file, AiGlobalCarryTransaction transaction) { _gate = gate; _file = file; _transaction = transaction; }
        // Only commit external replies after every local document is actually present on disk.
        internal void CommitAfterSave()
        {
            if (_gate is null) throw new ObjectDisposedException(nameof(Prepared));
            var notebook = AiStorage.NotebookPath(_transaction.LocalProject);
            if (!File.Exists(notebook)) throw new IOException("当前会话尚未落盘，跨会话回复未提交。");
            var book = JsonSerializer.Deserialize<AiNotebook>(File.ReadAllText(notebook)) ?? throw new InvalidDataException("无法读取当前会话。");
            if (!MatchesAfter(book, _transaction.LocalChanges)) throw new IOException("当前文稿版本尚未落盘，跨会话回复未提交。");
            _transaction.Stage = "committed"; Write(_file, _transaction); Recover();
        }
        internal void AbortBeforeSave()
        {
            if (_gate is null) throw new ObjectDisposedException(nameof(Prepared));
            var notebook = AiStorage.NotebookPath(_transaction.LocalProject);
            if (File.Exists(notebook))
            {
                var book = JsonSerializer.Deserialize<AiNotebook>(File.ReadAllText(notebook));
                if (book is not null && !MatchesBefore(book, _transaction.LocalChanges)) throw new InvalidOperationException("本地文稿已变化，保留恢复日志。");
            }
            File.Delete(_file);
        }
        public void Dispose() { _gate?.Dispose(); _gate = null; }
    }

    internal static Prepared? PrepareReplies(string localProject, IReadOnlyList<AiGlobalReplyUpdate> updates, IReadOnlyList<AiGlobalLocalChange> localChanges)
    {
        if (updates.Count == 0 || localChanges.Count == 0) throw new ArgumentException("跨会话事务必须包含远端回复和本地文稿。");
        var gate = GlobalLock();
        try
        {
            Recover(); var records = StageUpdates(updates); if (records is null) { gate.Dispose(); return null; }
            var transaction = new AiGlobalCarryTransaction { LocalProject = Path.GetFullPath(localProject), Updates = updates.ToList(), LocalChanges = localChanges.ToList(), Records = records };
            var file = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".transaction"); Write(file, transaction);
            return new Prepared(gate, file, transaction);
        }
        catch { gate.Dispose(); throw; }
    }

    private static bool MatchesBefore(AiNotebook book, IEnumerable<AiGlobalLocalChange> changes) => changes.All(change =>
        book.Conversations.FirstOrDefault(c => c.Id == change.ConversationId) is { } c && JsonSerializer.Serialize(c.SharedDocument.Snapshot()) == JsonSerializer.Serialize(change.Before));
    private static bool MatchesAfter(AiNotebook book, IEnumerable<AiGlobalLocalChange> changes) => changes.All(change =>
        book.Conversations.FirstOrDefault(c => c.Id == change.ConversationId) is { } c && JsonSerializer.Serialize(c.SharedDocument.Snapshot()) == JsonSerializer.Serialize(change.After));

    // Called before exposing a loaded notebook. Conflicts stay journaled; caller must report them.
    internal static int RecoverNotebook(string project, AiNotebook book)
    {
        using var global = GlobalLock(); Recover(); var conflicts = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*.transaction"))
        {
            var transaction = JsonSerializer.Deserialize<AiGlobalCarryTransaction>(File.ReadAllText(file)) ?? throw new InvalidDataException("无法读取跨会话恢复日志。");
            if (transaction.Stage != "prepared" || !string.Equals(Path.GetFullPath(project), transaction.LocalProject, StringComparison.OrdinalIgnoreCase)) continue;
            var records = StageUpdates(transaction.Updates);
            if (records is null) { conflicts++; continue; }
            if (MatchesBefore(book, transaction.LocalChanges))
            {
                foreach (var change in transaction.LocalChanges)
                    if (!book.Conversations.First(c => c.Id == change.ConversationId).SharedDocument.TryRestore(change.Before, change.After)) throw new InvalidOperationException("恢复文稿失败。");
                AiStorage.Save(project, book);
            }
            else if (!MatchesAfter(book, transaction.LocalChanges)) { conflicts++; continue; }
            transaction.Records = records; transaction.Stage = "committed"; Write(file, transaction); Recover();
        }
        return conflicts;
    }

    internal static void Remove(string project, string conversationId)
    {
        using var global = GlobalLock(); Recover();
        var file = PathFor(project, conversationId); using var gate = Lock(file);
        if (!File.Exists(file)) return;
        var record = Read(file);
        if (record.PendingReplies.Count > 0) throw new InvalidOperationException("此会话还有未处理的跨会话回复，请先恢复或解决冲突。");
        foreach (var transactionFile in Directory.EnumerateFiles(Root, "*.transaction"))
        {
            var transaction = JsonSerializer.Deserialize<AiGlobalCarryTransaction>(File.ReadAllText(transactionFile)) ?? throw new InvalidDataException("无法读取跨会话恢复日志。");
            if (transaction.Stage != "prepared") continue;
            var localMatch = string.Equals(Path.GetFullPath(project), transaction.LocalProject, StringComparison.OrdinalIgnoreCase) && transaction.LocalChanges.Any(c => c.ConversationId == conversationId);
            var remoteMatch = transaction.Updates.Any(u => string.Equals(Path.GetFullPath(project), Path.GetFullPath(u.Project), StringComparison.OrdinalIgnoreCase) && u.ConversationId == conversationId);
            if (localMatch || remoteMatch) throw new InvalidOperationException("此会话有待恢复的跨会话修改，请先解决冲突。");
        }
        record.Deleted = true; Write(file, record);
    }
    // Reconcile tombstones only against the notebook just loaded from disk.
    internal static void ReconcileDeleted(string project, AiNotebook persisted)
    {
        using var global = GlobalLock(); Recover();
        foreach (var conversation in persisted.Conversations)
        {
            var file = PathFor(project, conversation.Id); if (!File.Exists(file)) continue;
            var record = Read(file); if (record.Deleted) { record.Deleted = false; Write(file, record); }
        }
    }

    // Merge queued replies before a loaded conversation is shown; acknowledge only after notebook and Git are durable.
    internal static int RestorePendingReplies(string project, AiNotebook book)
    {
        var records = List().Where(record => string.Equals(Path.GetFullPath(record.Project), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase) && record.PendingReplies.Count > 0).ToArray();
        var affected = new List<(AiConversation Conversation, AiGlobalCarryConversation Record)>();
        var conflicts = 0; var changed = false;
        foreach (var record in records)
        {
            var conversation = book.Conversations.FirstOrDefault(c => c.Id == record.ConversationId);
            if (conversation is null) { conflicts++; continue; }
            var before = JsonSerializer.Serialize(conversation.SharedDocument.Snapshot());
            if (!Synchronize(project, conversation)) conflicts++;
            if (before != JsonSerializer.Serialize(conversation.SharedDocument.Snapshot())) changed = true;
            affected.Add((conversation, record));
        }
        if (changed) AiStorage.Save(project, book);
        foreach (var (conversation, record) in affected)
        {
            var applied = record.PendingReplies.Where(pending => conversation.SharedDocument.Comments.Any(comment => comment.Id == pending.CommentId && comment.Replies.Any(reply => reply.Id == pending.Reply.Id))).ToArray();
            if (applied.Length == 0) continue;
            try
            {
                AiDocumentRepository.SaveVersionAsync(project, conversation.Id, conversation.SharedDocument, applied[^1].Reply.Author, "跨会话回复评论").GetAwaiter().GetResult();
                AcknowledgePersisted(project, conversation.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { conflicts++; }
        }
        return conflicts;
    }

}
