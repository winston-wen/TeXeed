using System.IO;
using System.Text.Json;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App.AI;

/// <summary>Separate comments and drafts keyed by an editable project file's path.</summary>
internal sealed class ProjectCommentStore
{
    private sealed class Data
    {
        public Data() { }
        public Dictionary<string, AiConversation> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly string _root, _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, AiConversation> _files;
    private string? _diskHash;

    private ProjectCommentStore(string root, string path, Data data, string? diskHash)
    {
        _root = Path.GetFullPath(root); _path = path; _diskHash = diskHash;
        _files = new Dictionary<string, AiConversation>(data.Files, StringComparer.OrdinalIgnoreCase);
    }
    internal static ProjectCommentStore Load(string mainPath, string root)
    {
        var path = Path.Combine(AiStorage.Home, "project-comments", AiStorage.Hash(Path.GetFullPath(mainPath).ToUpperInvariant()) + ".json");
        if (!File.Exists(path)) return new(root, path, new(), null);
        var json = File.ReadAllText(path);
        return new(root, path, JsonSerializer.Deserialize<Data>(json) ?? throw new InvalidDataException("项目评论文件为空。"), AiStorage.Hash(json));
    }
    private string Key(string filePath)
    {
        var full = Path.GetFullPath(filePath); var relative = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("评论源码不在当前项目内。");
        return relative.Replace('\\', '/');
    }
    internal AiConversation GetOrCreate(string filePath, string source)
    {
        var key = Key(filePath);
        if (!_files.TryGetValue(key, out var record))
        {
            record = new AiConversation();
            if (source.Length > 0 && !record.SharedDocument.TryInitialize(source, "用户", "user")) throw new InvalidOperationException("源码无法保存为评论锚点。");
            _files.Add(key, record);
        }
        else if (record.SharedDocument.Source != source && !record.SharedDocument.TryApply(record.SharedDocument.Revision, source, "用户"))
            throw new InvalidOperationException("源码已变化，评论锚点未更新；原评论仍保留。");
        return record;
    }
    internal bool UpdateIfTracked(string filePath, string source)
    {
        if (!_files.TryGetValue(Key(filePath), out var item) || item.SharedDocument.Source == source) return false;
        if (!item.SharedDocument.TryApply(item.SharedDocument.Revision, source, "用户")) throw new InvalidOperationException("源码已变化，评论锚点未更新；原评论仍保留。");
        return true;
    }
    internal IEnumerable<(string File, AiConversation Data)> Files => _files.Select(pair => (Path.Combine(_root, pair.Key.Replace('/', Path.DirectorySeparatorChar)), pair.Value));
    private static bool HasData(AiConversation item) => item.SharedDocument.Comments.Count > 0 || item.CommentDrafts.Selection is not null || item.CommentDrafts.NewComment.Length > 0 || item.CommentDrafts.Edits.Values.Any(d => d.Text != d.Baseline) || item.CommentDrafts.Replies.Values.Any(text => text.Length > 0);
    internal bool HasAnyData => _files.Values.Any(HasData);
    internal bool HasDataFor(string filePath) => _files.TryGetValue(Key(filePath), out var item) && HasData(item);
    internal async Task SaveAsync(CancellationToken token = default)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            var snapshot = new Data { Files = _files.ToDictionary(x => x.Key, x => x.Value.Snapshot(), StringComparer.OrdinalIgnoreCase) };
            var json = JsonSerializer.Serialize(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var locked = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var existingHash = File.Exists(_path) ? AiStorage.Hash(await File.ReadAllTextAsync(_path, token)) : null;
            if (!string.Equals(existingHash, _diskHash, StringComparison.Ordinal))
                throw new IOException("项目评论已被其他程序修改；当前输入保留，请先处理冲突。");
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes, token);
                    stream.Flush(flushToDisk: true);
                }
                AtomicFile.Replace(temporary, _path); _diskHash = AiStorage.Hash(json);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _saveGate.Release(); }
    }

    internal static async Task<AiCommentLocation> LocateAsync(AiConversation file, AiDocumentComment comment,
        string filePath, TexProject compiledProject, string pdfPath, AppSettings settings, SyncTexService sync,
        Func<CancellationToken, Task<bool>> previewIsCurrent, CancellationToken token = default)
    {
        if (!await previewIsCurrent(token)) return new(AiCommentLocationStatus.PreviewOutdated, null);
        var snapshot = file.SharedDocument.Snapshot();
        var source = compiledProject.Files.FirstOrDefault(f => f.IsEditable && string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.Content != snapshot.Source) return new(AiCommentLocationStatus.Unavailable, null);
        var saved = snapshot.Comments.FirstOrDefault(c => c.Id == comment.Id);
        if (saved is null || AiCommentLocator.SourceAnchor(snapshot, saved, source.Path) is not { } anchor) return new(AiCommentLocationStatus.Unavailable, null);
        var location = await sync.ForwardViewportAsync(compiledProject, pdfPath, anchor, settings, token);
        if (!await previewIsCurrent(token) || file.SharedDocument.Revision != snapshot.Revision) return new(AiCommentLocationStatus.PreviewOutdated, null);
        var current = file.SharedDocument.Comments.FirstOrDefault(c => c.Id == saved.Id);
        if (current is null || current.Start != saved.Start || current.Length != saved.Length || current.Quote != saved.Quote || current.Orphaned != saved.Orphaned)
            return new(AiCommentLocationStatus.Unavailable, null);
        return location is null ? new(AiCommentLocationStatus.Unavailable, null) : new(AiCommentLocationStatus.Approximate, location);
    }
}
