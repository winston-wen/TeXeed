using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeXeed.App.Services;

namespace TeXeed.App.AI;

internal sealed record AiDocumentVersion(string Commit, string Author, DateTimeOffset CreatedAt, string Description);

/// <summary>Stores explicit, per-conversation versions outside the user's TeX project.</summary>
internal static class AiDocumentRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string GetDirectory(string project, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return Path.Combine(AiStorage.Home, "documents", AiStorage.Hash(Path.GetFullPath(project).ToUpperInvariant())[..32], AiStorage.Hash(conversationId)[..32]);
    }

    internal static async Task<string> SaveVersionAsync(string project, string conversationId, AiSharedDocument document,
        string author, string description, CancellationToken cancellationToken = default, string? resourceRestoreToken = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        var directory = GetDirectory(project, conversationId);
        var snapshot = document.Snapshot();
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话目录是链接，未写入版本。");
            var repository = Path.Combine(directory, ".git");
            if (!Directory.Exists(repository))
            {
                if (File.Exists(Path.Combine(directory, "document.tex")) || File.Exists(Path.Combine(directory, "review.json")))
                    throw new IOException("会话目录已有未识别的文稿或评论文件，未覆盖任何内容。");
                EnsureSafeResources(directory);
                await Git(directory, cancellationToken, "init", "--quiet");
            }
            else if ((File.GetAttributes(repository) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("会话版本目录的 Git 仓库是链接，未写入版本。");
            var root = (await Git(directory, cancellationToken, "rev-parse", "--show-toplevel")).Trim();
            if (!string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("会话版本仓库的位置不匹配，未写入版本。" );
            CheckResourceRestore(project, conversationId, resourceRestoreToken);
            await RecoverInterruptedVersionAsync(directory, cancellationToken);
            var index = Path.Combine(repository, "index"); var lockPath = index + ".lock";
            var token = Guid.NewGuid().ToString("N"); var tempIndex = Path.Combine(repository, "texeed-index-" + token);
            var journalPath = Path.Combine(repository, "texeed-save-journal.json");
            FileStream guard;
            try { guard = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new IOException("会话版本仓库正在被其他 Git 操作使用；未覆盖任何内容。", ex); }
            var journal = new SaveJournal { Token = token, TemporaryIndex = Path.GetFileName(tempIndex) };
            var keepRecovery = false; var ownsLock = true;
            try
            {
                var marker = Encoding.UTF8.GetBytes("TeXeed-index-lock:" + token); guard.Write(marker); guard.Flush(true);
                var pending = await Git(directory, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no");
                foreach (var entry in pending.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (entry.Length < 4) throw new IOException("无法检查会话版本目录的状态。");
                    var statusPath = entry[3..].Replace('\\', '/');
                    if ((entry[0] != ' ' && entry[0] != '?') || statusPath.Equals("document.tex", StringComparison.OrdinalIgnoreCase) || statusPath.Equals("review.json", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("会话版本包含手动暂存或外部修改的文稿、评论，未覆盖任何内容。");
                }
                var resources = EnsureSafeResources(directory);
                var handles = new List<FileStream>();
                try
                {
                    foreach (var path in resources) handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    var sourcePath = Path.Combine(directory, "document.tex"); var reviewPath = Path.Combine(directory, "review.json");
                    var review = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    journal.BeforeSource = File.Exists(sourcePath) ? await File.ReadAllBytesAsync(sourcePath, cancellationToken) : null;
                    journal.BeforeReview = File.Exists(reviewPath) ? await File.ReadAllBytesAsync(reviewPath, cancellationToken) : null;
                    journal.AfterSource = Utf8.GetBytes(snapshot.Source); journal.AfterReview = Utf8.GetBytes(review);
                    journal.BaseIndexHash = HashFile(index); journal.BaseHead = await Head(directory, cancellationToken);
                    SaveJournalFile(journalPath, journal);
                    if (File.Exists(index)) File.Copy(index, tempIndex);
                    else await GitCore(directory, cancellationToken, tempIndex, "read-tree", "--empty");
                    WriteOwnedFile(directory, "document.tex", journal.BeforeSource, journal.AfterSource, token);
                    WriteOwnedFile(directory, "review.json", journal.BeforeReview, journal.AfterReview, token);
                    await GitCore(directory, cancellationToken, tempIndex, "add", "-A", "-f", "--", ".");
                    if (await GitCore(directory, cancellationToken, tempIndex, "show", ":document.tex") != snapshot.Source ||
                        await GitCore(directory, cancellationToken, tempIndex, "show", ":review.json") != review)
                        throw new IOException("文稿在保存期间发生外部变化，未创建版本；工作区已保留。");
                    var later = EnsureSafeResources(directory);
                    if (!resources.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(later) ||
                        (await GitCore(directory, cancellationToken, tempIndex, "diff", "--name-only", "-z")).Length != 0)
                        throw new IOException("会话资源在保存期间发生变化，未创建版本；文件已保留。");
                    var staged = await GitCore(directory, cancellationToken, tempIndex, "diff", "--cached", "--name-only", "-z");
                    if (staged.Length == 0)
                    { return journal.BaseHead; }
                    journal.TargetTree = (await GitCore(directory, cancellationToken, tempIndex, "write-tree")).Trim();
                    SaveJournalFile(journalPath, journal);
                    var name = new string(author.Where(c => !char.IsControl(c) && c is not ('<' or '>')).Take(200).ToArray()).Trim();
                    if (name.Length == 0) name = "用户";
                    const string email = "author@texeed.local";
                    var message = string.IsNullOrWhiteSpace(description) ? "保存文稿" : description.Trim();
                    await GitCore(directory, cancellationToken, tempIndex, "-c", "user.name=TeXeed", "-c", "user.email=local@texeed.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(directory, ".texeed-no-hooks"), "commit", "--no-verify", "--quiet", "--author=" + name + " <" + email + ">", "-m", message);
                    keepRecovery = true;
                    await CopyIndexToGuardAsync(tempIndex, guard);
                    guard.Dispose(); AtomicFile.Replace(lockPath, index); ownsLock = false;
                    keepRecovery = false;
                    return (await Git(directory, CancellationToken.None, "rev-parse", "HEAD")).Trim();
                }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }
            catch (Exception ex)
            {
                if (File.Exists(journalPath) && !keepRecovery)
                {
                    var head = await Head(directory, CancellationToken.None);
                    if (head != journal.BaseHead) keepRecovery = true;
                    else if (!RestoreOwnedFile(directory, "document.tex", journal.BeforeSource, journal.AfterSource, journal.Token) || !RestoreOwnedFile(directory, "review.json", journal.BeforeReview, journal.AfterReview, journal.Token)) keepRecovery = true;
                }
                if (keepRecovery) throw new IOException("会话版本保存中断，恢复日志和文件已保留；请重新打开会话以恢复。", ex);
                throw;
            }
            finally
            {
                guard.Dispose();
                if (!keepRecovery)
                {
                    CleanupOwnedBackups(directory, journal);
                    if (File.Exists(tempIndex)) File.Delete(tempIndex);
                    if (File.Exists(journalPath)) File.Delete(journalPath);
                    if (ownsLock && File.Exists(lockPath)) File.Delete(lockPath);
                }
            }
        }
        finally { gate.Release(); }
    }

    private sealed class SaveJournal
    {
        public string Token { get; set; } = "";
        public string TemporaryIndex { get; set; } = "";
        public string BaseIndexHash { get; set; } = "";
        public string BaseHead { get; set; } = "";
        public string TargetTree { get; set; } = "";
        public byte[]? BeforeSource { get; set; }
        public byte[]? BeforeReview { get; set; }
        public byte[]? AfterSource { get; set; }
        public byte[]? AfterReview { get; set; }
    }

    private static string HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    private static void SaveJournalFile(string path, SaveJournal journal)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("版本恢复日志是链接，未写入。");
        WriteAtomic(path, JsonSerializer.Serialize(journal));
    }
    private static async Task<string> Head(string directory, CancellationToken token)
    {
        try { return (await Git(directory, token, "rev-parse", "--verify", "HEAD")).Trim(); }
        catch (IOException) { return ""; }
    }
    private static async Task CopyIndexToGuardAsync(string source, FileStream guard)
    {
        guard.Position = 0; guard.SetLength(0);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await input.CopyToAsync(guard); guard.Flush(true);
    }
    private static string OwnedBackup(string root, string name, string token) => Path.Combine(root, ".git", "texeed-before-" + token + "-" + name);
    private static void WriteOwnedFile(string root, string name, byte[]? before, byte[]? after, string token)
    {
        var path = Path.Combine(root, name); var backup = OwnedBackup(root, name, token);
        if (after is null) throw new ArgumentNullException(nameof(after));
        if (before is null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("文稿文件在保存期间被外部创建，未覆盖。");
        }
        else
        {
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var copy = new MemoryStream(); original.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(before)) throw new IOException("文稿文件在保存期间被外部修改，未覆盖。");
            File.Move(path, backup, false);
            if (!MatchesFile(backup, before))
            {
                if (!File.Exists(path) && !Directory.Exists(path)) File.Move(backup, path, false);
                throw new IOException("文稿文件在保存期间被外部替换，未覆盖；备份保留在会话仓库中。");
            }
        }
        var temporary = Path.Combine(root, ".git", "texeed-write-" + token + "-" + name);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(after); file.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool RestoreOwnedFile(string root, string name, byte[]? before, byte[]? after, string token)
    {
        var path = Path.Combine(root, name); var backup = OwnedBackup(root, name, token);
        if (MatchesFile(path, before)) return true;
        if (!File.Exists(path) && !Directory.Exists(path) && before is not null && MatchesFile(backup, before))
        { File.Move(backup, path, false); return true; }
        if (after is null || !MatchesFile(path, after)) return false;
        var displaced = Path.Combine(root, ".git", "texeed-rollback-" + token + "-" + name);
        using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var read = new MemoryStream(); current.CopyTo(read);
        if (!read.ToArray().AsSpan().SequenceEqual(after)) return false;
        File.Move(path, displaced, false);
        if (!MatchesFile(displaced, after))
        { if (!File.Exists(path) && !Directory.Exists(path)) File.Move(displaced, path, false); return false; }
        try
        {
            if (before is not null)
            {
                if (MatchesFile(backup, before)) File.Move(backup, path, false);
                else
                {
                    var temp = Path.Combine(root, ".git", "texeed-restore-" + token + "-" + name);
                    try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(before); file.Flush(true); } File.Move(temp, path, false); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            if (File.Exists(displaced)) File.Delete(displaced); return true;
        }
        catch (IOException) { if (!File.Exists(path) && !Directory.Exists(path) && File.Exists(displaced)) File.Move(displaced, path, false); return false; }
    }

    private static void CleanupOwnedBackups(string directory, SaveJournal journal)
    {
        foreach (var (name, expected) in new[] { ("document.tex", journal.BeforeSource), ("review.json", journal.BeforeReview) })
        {
            var backup = OwnedBackup(directory, name, journal.Token);
            if (expected is not null && MatchesFile(backup, expected)) File.Delete(backup);
        }
    }

    private static async Task RecoverInterruptedVersionAsync(string directory, CancellationToken token)
    {
        var repository = Path.Combine(directory, ".git"); var journalPath = Path.Combine(repository, "texeed-save-journal.json");
        if (!File.Exists(journalPath))
        {
            var orphan = Path.Combine(repository, "index.lock");
            if (!File.Exists(orphan)) return;
            try
            {
                using var abandoned = new FileStream(orphan, FileMode.Open, FileAccess.Read, FileShare.None);
                const string prefix = "TeXeed-index-lock:";
                if (abandoned.Length != prefix.Length + 32) return;
                using var reader = new StreamReader(abandoned, Encoding.UTF8, leaveOpen: true); var value = await reader.ReadToEndAsync(token);
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 32 || !value[prefix.Length..].All(Uri.IsHexDigit)) return;
                abandoned.Dispose(); File.Delete(orphan);
            }
            catch (IOException) { }
            return;
        }
        if ((File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话版本恢复日志是链接，未改动文件。");
        SaveJournal journal;
        try { journal = JsonSerializer.Deserialize<SaveJournal>(await File.ReadAllTextAsync(journalPath, token)) ?? throw new InvalidDataException(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new IOException("会话版本恢复日志无法读取；原文件已保留。", ex); }
        if (journal.Token.Length != 32 || !journal.Token.All(Uri.IsHexDigit) || journal.TemporaryIndex != "texeed-index-" + journal.Token)
            throw new IOException("会话版本恢复日志无效；原文件已保留。");
        var index = Path.Combine(repository, "index"); var lockPath = index + ".lock"; var temp = Path.Combine(repository, journal.TemporaryIndex);
        var hadLock = File.Exists(lockPath);
        FileStream guard;
        try { guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("会话版本仓库正在被其他 Git 操作使用。", ex); }
        var cleanup = false; var ownsLock = true;
        try
        {
            var marker = Encoding.UTF8.GetBytes("TeXeed-index-lock:" + journal.Token);
            if (hadLock)
            {
                using var existing = new MemoryStream(); await guard.CopyToAsync(existing, token);
                var lockBytes = existing.ToArray();
                var sameMarker = lockBytes.AsSpan().SequenceEqual(marker);
                var temporaryBytes = File.Exists(temp) ? await File.ReadAllBytesAsync(temp, token) : null;
                var sameIndex = temporaryBytes is not null && lockBytes.AsSpan().SequenceEqual(temporaryBytes);
                if (!sameMarker && !sameIndex) throw new IOException("Git 索引锁不属于此恢复记录；保留锁和所有文件。");
            }
            else { await guard.WriteAsync(marker, token); guard.Flush(true); }
            var indexHash = HashFile(index); var head = await Head(directory, token);
            if (head == journal.BaseHead && indexHash == journal.BaseIndexHash)
            {
                if (!RestoreOwnedFile(directory, "document.tex", journal.BeforeSource, journal.AfterSource, journal.Token) || !RestoreOwnedFile(directory, "review.json", journal.BeforeReview, journal.AfterReview, journal.Token))
                    throw new IOException("恢复期间发现文稿或评论已被外部修改；已保留恢复记录和所有文件。");
                cleanup = true;
            }
            else if (journal.TargetTree.Length > 0 && File.Exists(temp))
            {
                var tree = (await Git(directory, token, "rev-parse", "HEAD^{tree}")).Trim();
                var pendingTree = (await GitCore(directory, token, temp, "write-tree")).Trim();
                if (tree != journal.TargetTree || pendingTree != journal.TargetTree) throw new IOException("历史版本已变化；已保留恢复记录和所有文件。");
                if (indexHash == HashFile(temp)) cleanup = true;
                else if (indexHash == journal.BaseIndexHash)
                {
                    await CopyIndexToGuardAsync(temp, guard); guard.Dispose(); AtomicFile.Replace(lockPath, index); ownsLock = false; cleanup = true;
                }
                else throw new IOException("Git 索引已由其他操作修改；已保留恢复记录和所有文件。");
            }
            else throw new IOException("无法安全恢复中断的会话版本；已保留恢复记录和所有文件。");
        }
        finally
        {
            guard.Dispose();
            if (cleanup) { CleanupOwnedBackups(directory, journal); if (File.Exists(temp)) File.Delete(temp); if (File.Exists(journalPath)) File.Delete(journalPath); if (ownsLock && File.Exists(lockPath)) File.Delete(lockPath); }
        }
    }

    internal static async Task<IReadOnlyList<AiDocumentVersion>> ListVersionsAsync(string project, string conversationId, CancellationToken cancellationToken = default)
    {
        var directory = GetDirectory(project, conversationId);
        if (!Directory.Exists(Path.Combine(directory, ".git"))) return [];
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var log = await Git(directory, cancellationToken, "log", "--format=%H%x00%an%x00%aI%x00%s%x00");
            var versions = new List<AiDocumentVersion>();
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\0');
                if (fields.Length >= 4 && DateTimeOffset.TryParse(fields[2], out var date)) versions.Add(new(fields[0], fields[1], date, fields[3]));
            }
            return versions;
        }
        finally { gate.Release(); }
    }

    internal static async Task<AiSharedDocument> ReadVersionAsync(string project, string conversationId, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid version identifier.", nameof(commit));
        var directory = GetDirectory(project, conversationId);
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var json = await Git(directory, cancellationToken, "show", commit + ":review.json");
            return JsonSerializer.Deserialize<AiSharedDocument>(json) ?? throw new InvalidDataException("无法读取会话版本。" );
        }
        finally { gate.Release(); }
    }

    internal sealed record AssetChange(string Root, string Path, byte[]? Before, byte[]? After);

    private sealed class ResourceRestoreJournal
    {
        public string Token { get; set; } = "";
        public string BaseHead { get; set; } = "";
        public string TargetHead { get; set; } = "";
    }

    private static string ResourceJournalPath(string directory) => Path.Combine(directory, ".git", "texeed-resource-restore.json");
    private static ResourceRestoreJournal ReadResourceJournal(string directory)
    {
        var path = ResourceJournalPath(directory);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("资源恢复记录是链接，未改动任何文件。");
        try
        {
            var journal = JsonSerializer.Deserialize<ResourceRestoreJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            if (journal.Token.Length != 32 || !journal.Token.All(Uri.IsHexDigit) || !ValidCommit(journal.BaseHead) || !ValidCommit(journal.TargetHead)) throw new InvalidDataException();
            return journal;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new IOException("资源恢复记录无法读取；文件和历史版本已保留。", ex); }
    }
    private static bool ValidCommit(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    internal static bool HasInterruptedResourceRestore(string project, string conversationId) => File.Exists(ResourceJournalPath(GetDirectory(project, conversationId)));
    internal static void CheckResourceRestore(string project, string conversationId, string? allowedToken)
    {
        var directory = GetDirectory(project, conversationId); if (!File.Exists(ResourceJournalPath(directory))) return;
        var journal = ReadResourceJournal(directory);
        if (journal.Token != allowedToken) throw new IOException("上次资源版本恢复未完成，已暂停 Git 版本保存；文稿和草稿仍会保存。请打开版本窗口，选择‘恢复中断的资源’。");
    }

    internal sealed class AssetRestore
    {
        private readonly IReadOnlyList<AssetChange> _changes;
        private readonly string _directory;
        private readonly bool _removeMarkerOnRollback;
        internal string Token { get; }
        internal AssetRestore(IReadOnlyList<AssetChange> changes, string directory, string token, bool removeMarkerOnRollback = true)
        { _changes = changes; _directory = directory; Token = token; _removeMarkerOnRollback = removeMarkerOnRollback; }
        internal bool Rollback()
        {
            var complete = true;
            foreach (var change in _changes.Reverse())
            {
                try
                {
                    if (!MatchesFile(change.Path, change.After)) { complete = false; continue; }
                    PutFile(change.Root, change.Path, change.Before, change.After, Token);
                }
                catch (IOException) { complete = false; }
                catch (UnauthorizedAccessException) { complete = false; }
            }
            if (complete && _removeMarkerOnRollback) Complete();
            return complete;
        }
        internal void Complete()
        {
            var journal = ReadResourceJournal(_directory);
            if (journal.Token != Token) throw new IOException("资源恢复记录已变化，未清除任何记录。");
            if (Directory.EnumerateFiles(Path.Combine(_directory, ".git"), "texeed-asset-backup-" + Token + "-*").Any()) throw new IOException("存在未处理的资源恢复备份，已保留恢复记录。");
            File.Delete(ResourceJournalPath(_directory));
        }
    }

    /// <summary>Restores versioned resources only when the saved worktree is still clean.</summary>
    internal static async Task<AssetRestore> RestoreAssetsAsync(string project, string conversationId, string commit, string expectedHead, CancellationToken token = default)
    {
        if (!ValidCommit(commit) || !ValidCommit(expectedHead)) throw new ArgumentException("无效的文稿版本标识。");
        var directory = Path.GetFullPath(GetDirectory(project, conversationId)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(token);
        try
        {
            if ((File.GetAttributes(Path.Combine(directory, ".git")) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话版本仓库是链接，未恢复资源。");
            var files = EnsureSafeResources(directory);
            var head = (await Git(directory, token, "rev-parse", "HEAD")).Trim();
            var status = await Git(directory, token, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching");
            if (!string.Equals(head, expectedHead, StringComparison.OrdinalIgnoreCase) || status.Length != 0) throw new IOException("会话资源在准备恢复后发生变化，未覆盖任何文件。");
            var target = await ReadResourceTreeAsync(directory, commit, token);
            var current = files.ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), path => path, StringComparer.OrdinalIgnoreCase);
            var keys = current.Keys.Union(target.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var changes = new List<AssetChange>(); long total = 0;
            foreach (var key in keys)
            {
                var path = target.TryGetValue(key, out var asset) ? asset.FullPath : current[key];
                byte[]? before = null; byte[]? after = null;
                if (current.TryGetValue(key, out var existing))
                {
                    if (new FileInfo(existing).Length > 64 * 1024 * 1024) throw new IOException("资源过大，未自动恢复；历史文件仍在 Git 版本中。");
                    before = await File.ReadAllBytesAsync(existing, token);
                }
                if (target.ContainsKey(key))
                {
                    var sizeText = (await Git(directory, token, "cat-file", "-s", asset.Object)).Trim();
                    if (!long.TryParse(sizeText, out var size) || size < 0 || size > 64 * 1024 * 1024) throw new IOException("资源过大，未自动恢复；历史文件仍在 Git 版本中。");
                    after = await GitBytes(directory, token, "cat-file", "blob", asset.Object);
                }
                total += (before?.LongLength ?? 0) + (after?.LongLength ?? 0);
                if ((before?.LongLength ?? 0) > 64 * 1024 * 1024 || (after?.LongLength ?? 0) > 64 * 1024 * 1024 || total > 256 * 1024 * 1024)
                    throw new IOException("资源过大，未自动恢复；历史文件仍在 Git 版本中。");
                if (before is not null && after is not null && before.AsSpan().SequenceEqual(after)) continue;
                var parent = Path.GetDirectoryName(path)!;
                while (!string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                { if (File.Exists(parent)) throw new IOException("历史资源路径与现有文件冲突，未恢复资源。"); parent = Path.GetDirectoryName(parent) ?? throw new IOException("无效的资源路径。"); }
                if (Directory.Exists(path)) throw new IOException("历史资源路径与现有目录冲突，未恢复资源。");
                changes.Add(new(directory, path, before, after));
            }
            if (File.Exists(ResourceJournalPath(directory))) throw new IOException("有尚未完成的资源恢复，请先处理恢复记录。");
            var journal = new ResourceRestoreJournal { Token = Guid.NewGuid().ToString("N"), BaseHead = expectedHead, TargetHead = commit };
            WriteAtomic(ResourceJournalPath(directory), JsonSerializer.Serialize(journal));
            var applied = new List<AssetChange>();
            try
            {
                foreach (var change in changes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!MatchesFile(change.Path, change.Before)) throw new IOException("资源在恢复期间发生外部修改，已停止恢复。");
                    PutFile(change.Root, change.Path, change.After, change.Before, journal.Token); applied.Add(change);
                }
            }
            catch (Exception ex)
            {
                var rollback = new AssetRestore(applied, directory, journal.Token, false);
                if (!rollback.Rollback()) throw new IOException("资源在恢复期间发生并发修改，已保留当前文件；恢复前版本仍保存在 Git 中。", ex);
                throw new IOException("资源恢复未完成；恢复记录和文件已保留，请打开版本窗口处理。", ex);
            }
            return new AssetRestore(applied, directory, journal.Token);
        }
        finally { gate.Release(); }
    }

    internal sealed record AssetRecovery(AiSharedDocument ExpectedDocument, AiSharedDocument PreviousDocument, AssetRestore Resources);

    internal static async Task<AssetRecovery> RecoverInterruptedAssetsAsync(string project, string conversationId, AiSharedDocument document, CancellationToken token = default)
    {
        var directory = Path.GetFullPath(GetDirectory(project, conversationId)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(token);
        try
        {
            await RecoverInterruptedVersionAsync(directory, token);
            var journal = ReadResourceJournal(directory); EnsureSafeResources(directory);
            var before = JsonSerializer.Deserialize<AiSharedDocument>(await Git(directory, token, "show", journal.BaseHead + ":review.json")) ?? throw new InvalidDataException("无法读取恢复前的文稿。");
            var target = JsonSerializer.Deserialize<AiSharedDocument>(await Git(directory, token, "show", journal.TargetHead + ":review.json")) ?? throw new InvalidDataException("无法读取待恢复的文稿。");
            var expected = document.Snapshot(); var currentJson = JsonSerializer.Serialize(expected);
            if (currentJson != JsonSerializer.Serialize(before) && currentJson != JsonSerializer.Serialize(target))
                throw new IOException("文稿或评论在恢复中断后已变化，未自动覆盖；恢复记录及 Git 版本仍保留。");
            var oldTree = await ReadResourceTreeAsync(directory, journal.BaseHead, token);
            var newTree = await ReadResourceTreeAsync(directory, journal.TargetHead, token);
            var changes = new List<AssetChange>(); long total = 0;
            foreach (var key in oldTree.Keys.Union(newTree.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var hasBefore = oldTree.TryGetValue(key, out var oldFile); var hasAfter = newTree.TryGetValue(key, out var newFile);
                if (hasBefore && hasAfter && oldFile.Object == newFile.Object) continue;
                var path = hasBefore ? oldFile.FullPath : newFile.FullPath;
                byte[]? oldBytes = hasBefore ? await ReadAssetBlobAsync(directory, oldFile.Object, token) : null;
                byte[]? newBytes = hasAfter ? await ReadAssetBlobAsync(directory, newFile.Object, token) : null;
                total += (oldBytes?.LongLength ?? 0) + (newBytes?.LongLength ?? 0);
                if (total > 256 * 1024 * 1024) throw new IOException("资源过大，未自动恢复；Git 历史与恢复记录仍保留。");
                var backup = ResourceBackup(directory, path, journal.Token);
                if (File.Exists(backup))
                {
                    if (!MatchesFile(backup, oldBytes) && !MatchesFile(backup, newBytes)) throw new IOException("资源恢复备份已被更改，未覆盖任何内容。");
                    if (!File.Exists(path) && !Directory.Exists(path))
                    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Move(backup, path, false); }
                    else if (MatchesFile(path, oldBytes) || MatchesFile(path, newBytes)) File.Delete(backup);
                    else throw new IOException("资源在恢复中断后被外部修改，未覆盖任何内容。");
                }
                if (MatchesFile(path, oldBytes)) continue;
                if (!MatchesFile(path, newBytes)) throw new IOException("资源在恢复中断后被外部修改，未覆盖任何内容。");
                changes.Add(new(directory, path, newBytes, oldBytes));
            }
            var applied = new List<AssetChange>();
            try
            {
                foreach (var change in changes) { PutFile(change.Root, change.Path, change.After, change.Before, journal.Token); applied.Add(change); }
            }
            catch
            {
                var rollback = new AssetRestore(applied, directory, journal.Token, false);
                if (!rollback.Rollback()) throw new IOException("资源恢复期间发生并发修改；当前文件和恢复记录已保留。");
                throw;
            }
            return new AssetRecovery(expected, before, new AssetRestore(applied, directory, journal.Token, false));
        }
        finally { gate.Release(); }
    }

    private static async Task<byte[]> ReadAssetBlobAsync(string directory, string objectId, CancellationToken token)
    {
        var sizeText = (await Git(directory, token, "cat-file", "-s", objectId)).Trim();
        if (!long.TryParse(sizeText, out var size) || size < 0 || size > 64 * 1024 * 1024) throw new IOException("资源过大，未自动恢复；Git 历史与恢复记录仍保留。");
        return await GitBytes(directory, token, "cat-file", "blob", objectId);
    }

    private static async Task<Dictionary<string, (string FullPath, string Object)>> ReadResourceTreeAsync(string directory, string commit, CancellationToken token)
    {
        var tree = await Git(directory, token, "ls-tree", "-r", "-z", "--full-tree", commit);
        var target = new Dictionary<string, (string FullPath, string Object)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = row.IndexOf('\t'); if (tab < 0) throw new IOException("无法读取历史资源目录。");
            var metadata = row[..tab].Split(' '); var name = row[(tab + 1)..];
            if (metadata.Length != 3 || metadata[1] != "blob" || metadata[0] is not ("100644" or "100755")) throw new IOException("历史版本含符号链接或嵌套仓库，未恢复资源。");
            if (name.Equals("document.tex", StringComparison.OrdinalIgnoreCase) || name.Equals("review.json", StringComparison.OrdinalIgnoreCase)) continue;
            var segments = name.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('\\') || segment.Contains(':') || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || segment.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("历史版本含不安全的资源路径，未恢复资源。");
            var full = Path.GetFullPath(Path.Combine(directory, Path.Combine(segments)));
            if (!full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !target.TryAdd(name, (full, metadata[2])))
                throw new IOException("历史版本的资源路径不唯一，未恢复资源。");
        }
        return target;
    }

    private static bool MatchesFile(string path, byte[]? expected)
    {
        if (expected is null) return !File.Exists(path) && !Directory.Exists(path);
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
    }

    private static string ResourceBackup(string root, string path, string token)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/').ToUpperInvariant();
        return Path.Combine(root, ".git", "texeed-asset-backup-" + token + "-" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(relative))));
    }

    private static void PutFile(string root, string path, byte[]? value, byte[]? expected, string token)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, Path.GetDirectoryName(path)!).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("资源目录发生链接变化，已停止恢复。");
        }
        if (!MatchesFile(path, expected)) throw new IOException("资源在恢复期间发生外部修改，未覆盖。");
        var backup = ResourceBackup(root, path, token);
        if (File.Exists(backup)) throw new IOException("资源有未完成的恢复备份，请先处理恢复记录。");
        if (expected is not null)
        {
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var copy = new MemoryStream(); original.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(expected)) throw new IOException("资源在恢复期间发生外部修改，未覆盖。");
            File.Move(path, backup, false);
            if (!MatchesFile(backup, expected))
            {
                if (!File.Exists(path) && !Directory.Exists(path)) File.Move(backup, path, false);
                throw new IOException("资源在恢复期间被外部替换；原文件保留在会话仓库中。");
            }
        }
        var temporary = Path.Combine(root, ".git", "texeed-asset-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (value is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(value); file.Flush(true); }
                File.Move(temporary, path, false);
            }
            if (expected is not null && MatchesFile(backup, expected)) File.Delete(backup);
        }
        catch
        {
            if (expected is not null && !File.Exists(path) && !Directory.Exists(path) && MatchesFile(backup, expected)) File.Move(backup, path, false);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<byte[]> GitBytes(string directory, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) || k is "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY" or "GIT_ALTERNATE_OBJECT_DIRECTORIES" or "GIT_COMMON_DIR").ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".texeed-no-config"); start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Git。");
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, token); var errors = process.StandardError.ReadToEndAsync(token);
        try { await Task.WhenAll(copy, process.WaitForExitAsync(token)); } catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var error = await errors; if (process.ExitCode != 0) throw new IOException("读取历史资源失败：" + error.Trim());
        return buffer.ToArray();
    }

    private static IReadOnlyList<string> EnsureSafeResources(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话目录是链接，未访问资源。");
        var files = new List<string>();
        void Visit(string current, bool root)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(path);
                if (root && name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("会话资源中含 Git 控制文件或嵌套仓库，未写入版本。 ");
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("会话资源含符号链接或目录联接，未写入版本。 ");
                if ((attr & FileAttributes.Directory) != 0) Visit(path, false);
                else if (!(root && (name.Equals("document.tex", StringComparison.OrdinalIgnoreCase) || name.Equals("review.json", StringComparison.OrdinalIgnoreCase)))) files.Add(path);
            }
        }
        Visit(directory, true); return files;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, Utf8, leaveOpen: true))
            { writer.Write(content); writer.Flush(); file.Flush(flushToDisk: true); }
            AtomicFile.Replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Task<string> Git(string directory, CancellationToken cancellationToken, params string[] arguments) => GitCore(directory, cancellationToken, null, arguments);

    private static async Task<string> GitCore(string directory, CancellationToken cancellationToken, string? indexFile, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) || k is "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE" or "GIT_OBJECT_DIRECTORY" or "GIT_ALTERNATE_OBJECT_DIRECTORIES" or "GIT_COMMON_DIR").ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".texeed-no-config");
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (indexFile is not null) start.Environment["GIT_INDEX_FILE"] = indexFile;
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.autocrlf=false");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.longpaths=true");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.attributesFile=" + Path.Combine(directory, ".texeed-no-attributes"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Git。" );
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"Git 操作失败（{process.ExitCode}）：{error.Trim()}" );
        return output;
    }
}
