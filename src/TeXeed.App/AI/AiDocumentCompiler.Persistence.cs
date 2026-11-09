using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App.AI;

internal sealed partial class AiDocumentCompiler
{
    private sealed record PreviewManifest(string BuildId, string SourceRevision, Dictionary<string, string> ResourceHashes, Dictionary<string, string> OutputHashes);
    private readonly HashSet<string> _publishedBuilds = new(StringComparer.OrdinalIgnoreCase);
    internal static string ManifestPath(string project, string conversationId) => Path.Combine(AiStorage.Home, "document-previews",
        AiStorage.Hash(Path.GetFullPath(project).ToUpperInvariant())[..32], AiStorage.Hash(conversationId)[..32] + ".json");
    internal static string BuildParent(string project, string conversationId) => Path.Combine(AiStorage.Home, "document-builds",
        AiStorage.Hash(Path.GetFullPath(project).ToUpperInvariant())[..32], AiStorage.Hash(conversationId)[..32]);

    private static void CreateBuildDirectory(string root)
    {
        var home = AiStorage.Home; Directory.CreateDirectory(home);
        if ((File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0) throw new IOException("预览目录是链接，无法编译。");
        var relative = Path.GetRelativePath(home, root);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or ".."))
            throw new IOException("预览目录无效，无法编译。");
        var current = home;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part); Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("预览目录是链接，无法编译。");
        }
    }

    private static readonly string[] PreviewOutputs = ["document.pdf", "document.synctex", "document.synctex.gz", "document.texeed-layout"];
    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static bool SafeChild(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) return false;
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return false;
        var path = root;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var part in parts)
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private async Task<AiDocumentPreview?> ReadManifestAsync(string project, string conversationId, CancellationToken token)
    {
        var manifestPath = ManifestPath(project, conversationId);
        try
        {
            var home = AiStorage.Home; var relativeManifest = Path.GetRelativePath(home, manifestPath);
            if (!SafeChild(home, relativeManifest) || new FileInfo(manifestPath).Length > 4_000_000) return null;
            var manifest = JsonSerializer.Deserialize<PreviewManifest>(await File.ReadAllTextAsync(manifestPath, token));
            if (manifest is null || !Guid.TryParseExact(manifest.BuildId, "N", out _) || !ValidHash(manifest.SourceRevision) ||
                manifest.ResourceHashes is null || manifest.OutputHashes is null || !manifest.OutputHashes.ContainsKey("document.pdf") ||
                manifest.OutputHashes.Keys.Any(k => !PreviewOutputs.Contains(k, StringComparer.Ordinal)) ||
                manifest.OutputHashes.Values.Any(v => !ValidHash(v)) || manifest.ResourceHashes.Values.Any(v => !ValidHash(v))) return null;
            var relativeRoot = Path.GetRelativePath(home, Path.Combine(BuildParent(project, conversationId), manifest.BuildId));
            if (!SafeChild(home, relativeRoot)) return null;
            var root = Path.Combine(home, relativeRoot); var source = Path.Combine(root, "document.tex");
            if (!SafeChild(root, "document.tex") || AiStorage.Hash(await File.ReadAllTextAsync(source, token)) != manifest.SourceRevision) return null;
            foreach (var (relative, hash) in manifest.ResourceHashes)
                if (!SafeChild(root, relative) || await FileHashAsync(Path.Combine(root, relative), token) != hash) return null;
            foreach (var output in PreviewOutputs)
            {
                var relative = Path.Combine(".texeed", "preview", output); var path = Path.Combine(root, relative);
                if (manifest.OutputHashes.TryGetValue(output, out var hash))
                { if (!SafeChild(root, relative) || await FileHashAsync(path, token) != hash) return null; }
                else if (File.Exists(path)) return null;
            }
            var loaded = await _projects.LoadAsync(source, token, root);
            var main = loaded.Files.FirstOrDefault(f => f.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (main is null || AiStorage.Hash(main.Content) != manifest.SourceRevision ||
                loaded.Files.Any(f => f.Kind == ProjectFileKind.Tex && !f.Path.Equals(source, StringComparison.OrdinalIgnoreCase))) return null;
            return new AiDocumentPreview(Path.Combine(root, ".texeed", "preview", "document.pdf"), manifest.SourceRevision, loaded,
                manifest.OutputHashes.ContainsKey("document.synctex") || manifest.OutputHashes.ContainsKey("document.synctex.gz"),
                AiDocumentRepository.GetDirectory(project, conversationId), manifest.ResourceHashes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException) { return null; }
    }

    internal async Task<AiDocumentPreview?> RestoreAsync(string project, string conversationId, CancellationToken token = default)
    {
        var preview = await ReadManifestAsync(project, conversationId, token);
        if (preview is not null) lock (_ownedBuilds) { _ownedBuilds.Add(preview.Project.Root); _publishedBuilds.Add(preview.Project.Root); LastSuccessful = preview; }
        return preview;
    }

    internal async Task PublishAsync(string project, string conversationId, AiDocumentPreview preview, CancellationToken token = default)
    {
        var home = AiStorage.Home; var root = preview.Project.Root; var buildId = Path.GetFileName(root);
        if (!Guid.TryParseExact(buildId, "N", out _) || !Path.GetFullPath(root).Equals(Path.GetFullPath(Path.Combine(BuildParent(project, conversationId), buildId)), StringComparison.OrdinalIgnoreCase) ||
            !SafeChild(home, Path.GetRelativePath(home, root))) throw new IOException("预览目录无效，未保存预览记录。");
        if (!Path.GetFullPath(preview.ResourceRoot).Equals(Path.GetFullPath(AiDocumentRepository.GetDirectory(project, conversationId)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("预览所属会话不匹配，未保存预览记录。");
        lock (_ownedBuilds) { if (!_ownedBuilds.Contains(root)) throw new IOException("预览不属于当前会话编译器。"); }
        if (!SafeChild(root, "document.tex") || AiStorage.Hash(await File.ReadAllTextAsync(Path.Combine(root, "document.tex"), token)) != preview.SourceRevision)
            throw new IOException("预览源码已变化，未保存预览记录。");
        foreach (var (relative, hash) in preview.ResourceHashes)
            if (!SafeChild(root, relative) || await FileHashAsync(Path.Combine(root, relative), token) != hash)
                throw new IOException("预览资源已变化，未保存预览记录。");
        var outputHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PreviewOutputs)
        {
            var relative = Path.Combine(".texeed", "preview", name); var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            if (!SafeChild(root, relative)) throw new IOException("预览文件是链接，未保存预览记录。");
            outputHashes[name] = await FileHashAsync(path, token);
        }
        if (!outputHashes.ContainsKey("document.pdf")) throw new IOException("找不到 PDF，未保存预览记录。");
        var manifest = new PreviewManifest(buildId, preview.SourceRevision, new Dictionary<string, string>(preview.ResourceHashes), outputHashes);
        var pathToManifest = ManifestPath(project, conversationId); var parent = Path.GetDirectoryName(pathToManifest)!;
        var top = Path.Combine(home, "document-previews");
        if ((File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0) throw new IOException("会话目录是链接，未保存预览记录。");
        Directory.CreateDirectory(top);
        if (!SafeChild(home, "document-previews")) throw new IOException("预览记录目录是链接，未保存。");
        Directory.CreateDirectory(parent);
        if (!SafeChild(home, Path.GetRelativePath(home, parent))) throw new IOException("预览记录目录是链接，未保存。");
        if (File.Exists(pathToManifest) && !SafeChild(home, Path.GetRelativePath(home, pathToManifest))) throw new IOException("预览记录是链接，未覆盖。");
        var temporary = pathToManifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest), token);
            AtomicFile.Replace(temporary, pathToManifest);
            lock (_ownedBuilds) _publishedBuilds.Add(root);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
