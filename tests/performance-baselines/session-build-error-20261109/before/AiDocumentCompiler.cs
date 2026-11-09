using System.IO;
using System.Security.Cryptography;
using System.Text;
using TeXeed.Core;

namespace TeXeed.App.AI;

internal sealed record AiDocumentPreview(string PdfPath, string SourceRevision, TexProject Project, bool SynctexAvailable,
    string ResourceRoot, IReadOnlyDictionary<string, string> ResourceHashes);
internal sealed record AiDocumentBuildResult(AiDocumentPreview? Preview, string Message, bool Outdated);

/// <summary>Compiles immutable session snapshots in a separate per-build directory.</summary>
internal sealed partial class AiDocumentCompiler
{
    private readonly CompileService _compiler = new();
    private readonly ProjectService _projects = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _ownedBuilds = new(StringComparer.OrdinalIgnoreCase);
    internal AiDocumentPreview? LastSuccessful { get; private set; }
    internal void Cancel() => _compiler.Cancel();

    internal static async Task<bool> IsCurrentAsync(AiDocumentPreview preview, AiSharedDocument document, CancellationToken token = default) =>
        document.Revision == preview.SourceRevision && await ResourcesMatchAsync(preview.ResourceRoot, preview.ResourceHashes, token) &&
        document.Revision == preview.SourceRevision;

    // The caller releases an old preview only after all viewers have switched away.
    internal bool Release(AiDocumentPreview preview)
    {
        var root = preview.Project.Root;
        lock (_ownedBuilds)
        {
            if (ReferenceEquals(preview, LastSuccessful) || !_ownedBuilds.Contains(root) || _publishedBuilds.Contains(root)) return false;
            if (!RemoveOwnedBuild(root)) return false;
            _ownedBuilds.Remove(root);
            return true;
        }
    }

    internal bool DiscardUnshown(AiDocumentPreview preview, AiDocumentPreview? displayed)
    {
        lock (_ownedBuilds)
        {
            if (!ReferenceEquals(LastSuccessful, preview) || !_ownedBuilds.Contains(preview.Project.Root) || _publishedBuilds.Contains(preview.Project.Root) ||
                displayed is not null && !_ownedBuilds.Contains(displayed.Project.Root)) return false;
            LastSuccessful = displayed;
            if (!RemoveOwnedBuild(preview.Project.Root)) return false;
            _ownedBuilds.Remove(preview.Project.Root);
            return true;
        }
    }

    private static bool RemoveOwnedBuild(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return true;
            if (!SafeChild(AiStorage.Home, Path.GetRelativePath(AiStorage.Home, root))) return false;
            Directory.Delete(root, true); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal async Task<AiDocumentBuildResult> CompileAsync(string project, string conversationId,
        AiSharedDocument document, AppSettings settings, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        string? buildRoot = null;
        var published = false;
        try
        {
            var snapshot = document.Snapshot();
            var revision = snapshot.Revision;
            var sessionRoot = AiDocumentRepository.GetDirectory(project, conversationId);
            Directory.CreateDirectory(sessionRoot);
            buildRoot = Path.Combine(BuildParent(project, conversationId), Guid.NewGuid().ToString("N"));
            CreateBuildDirectory(buildRoot);
            var files = await CopyResourcesAsync(sessionRoot, buildRoot, token);
            var sourcePath = Path.Combine(buildRoot, "document.tex");
            await File.WriteAllTextAsync(sourcePath, snapshot.Source, new UTF8Encoding(false), token);
            var buildProject = await _projects.LoadAsync(sourcePath, token, buildRoot);
            if (buildProject.Files.Any(f => f.Kind == ProjectFileKind.Tex && !f.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
                return new(null, "共同文稿不能包含其他 TeX 文件。", LastSuccessful is not null);
            var result = await _compiler.CompileAsync(buildProject, settings, token);
            if (!result.Success || result.PdfPath is null)
                return new(null, "编译失败：" + result.Log, LastSuccessful is not null);
            var current = document.Revision == revision && result.InputsCurrent != false &&
                await _projects.MatchesSourcesAsync(buildProject, token) &&
                await ResourcesMatchAsync(sessionRoot, files, token) && document.Revision == revision;
            if (!current) return new(null, "编译期间文稿或资源发生变化；预览已过期，请重新编译。", true);
            var preview = new AiDocumentPreview(result.PdfPath, revision, buildProject, result.SynctexAvailable, sessionRoot, files);
            lock (_ownedBuilds) { _ownedBuilds.Add(buildRoot); LastSuccessful = preview; }
            published = true;
            return new(preview, "编译成功", false);
        }
        finally
        {
            if (!published && buildRoot is not null) RemoveOwnedBuild(buildRoot);
            _gate.Release();
        }
    }

    private static async Task<Dictionary<string, string>> CopyResourcesAsync(string root, string target, CancellationToken token)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in EnumerateResources(root))
        {
            token.ThrowIfCancellationRequested();
            var input = Path.Combine(root, relative); var output = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            { hash.AppendData(buffer, 0, count); await destination.WriteAsync(buffer.AsMemory(0, count), token); }
            hashes.Add(relative, Convert.ToHexString(hash.GetHashAndReset()));
        }
        return hashes;
    }

    private static IEnumerable<string> EnumerateResources(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话目录是链接，无法编译。");
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                var relative = Path.GetRelativePath(root, path);
                if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("会话资源含链接，无法安全编译。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (!relative.Equals("document.tex", StringComparison.OrdinalIgnoreCase) && !relative.Equals("review.json", StringComparison.OrdinalIgnoreCase)) yield return relative;
            }
    }

    private static async Task<bool> ResourcesMatchAsync(string root, IReadOnlyDictionary<string, string> expected, CancellationToken token)
    {
        try
        {
            var paths = EnumerateResources(root).ToArray();
            if (!expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths)) return false;
            foreach (var relative in paths)
            {
                token.ThrowIfCancellationRequested();
                await using var stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != expected[relative]) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
