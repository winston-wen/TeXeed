using System.Text.Json;

namespace TeXeed.Core;

public sealed record CachedPreview(string PdfPath, bool IsCurrent, bool SynctexAvailable);

public sealed partial class CompileService
{
    // Bump when changes to compilation/driver rules require regenerating output.
    private const int PreviewCacheVersion = 1;
    private sealed record PreviewCache(int Version, string Root, string Main, string Settings,
        string Engine, Dictionary<string, string> Inputs, Dictionary<string, string> Outputs);

    private static string CachePath(TexProject project) => Path.Combine(project.Root, ".texeed", "preview",
        Path.GetFileNameWithoutExtension(project.MainPath) + ".texeed-cache.json");
    private static string CacheSettings(AppSettings settings) => JsonSerializer.Serialize(new
    {
        settings.Engine, settings.EnginePath,
        TexInputs = Environment.GetEnvironmentVariable("TEXINPUTS"),
        BibInputs = Environment.GetEnvironmentVariable("BIBINPUTS"),
        BstInputs = Environment.GetEnvironmentVariable("BSTINPUTS"),
        TexMfHome = Environment.GetEnvironmentVariable("TEXMFHOME")
    });

    public async Task<CachedPreview?> GetCachedPreviewAsync(TexProject project, AppSettings settings, CancellationToken ct = default)
    {
        var preview = Path.Combine(project.Root, ".texeed", "preview");
        var job = Path.GetFileNameWithoutExtension(project.MainPath);
        var pdf = Path.Combine(preview, job + ".pdf");
        try
        {
            if (!File.Exists(pdf) || !IsOrdinaryInput(project.Root, pdf))
            {
                // Also show output produced by another editor/build script;
                // without a manifest it remains a last-known preview, not current.
                foreach (var candidate in new[] { Path.Combine(project.Root, job + ".pdf"), Path.Combine(project.Root, "build", job + ".pdf") })
                {
                    if (!File.Exists(candidate) || !IsOrdinaryInput(project.Root, candidate)) continue;
                    await using var legacy = File.OpenRead(candidate);
                    var magic = new byte[5];
                    if (await legacy.ReadAsync(magic, ct) == 5 && magic.AsSpan().SequenceEqual("%PDF-"u8))
                        return new(candidate, false, File.Exists(Path.ChangeExtension(candidate, ".synctex.gz")) || File.Exists(Path.ChangeExtension(candidate, ".synctex")));
                }
                return null;
            }
            await using (var stream = File.OpenRead(pdf))
            {
                var header = new byte[5];
                if (await stream.ReadAsync(header, ct) != 5 || !header.AsSpan().SequenceEqual("%PDF-"u8)) return null;
            }
            var sync = File.Exists(Path.ChangeExtension(pdf, ".synctex")) || File.Exists(Path.ChangeExtension(pdf, ".synctex.gz"));
            var stale = new CachedPreview(pdf, false, sync);
            var path = CachePath(project);
            if (!File.Exists(path) || !IsOrdinaryInput(project.Root, path) || new FileInfo(path).Length > 4 * 1024 * 1024) return stale;
            var cache = JsonSerializer.Deserialize<PreviewCache>(await File.ReadAllTextAsync(path, ct));
            if (cache is null || cache.Version != PreviewCacheVersion || cache.Root != project.Root || cache.Main != project.MainPath
                || cache.Settings != CacheSettings(settings) || cache.Inputs is null || cache.Outputs is null
                || cache.Inputs.Count > 10000 || cache.Outputs.Count > 8) return stale;
            var engine = ResolveEngine(settings, await DetectToolsAsync());
            if (cache.Engine != engine.Name + "\n" + ToolIdentity(engine.Path)) return stale;
            if (engine.Name == "tectonic")
            {
                var current = await CaptureCacheInputsAsync(project, "", ct, scanProject: true);
                if (current.Count != cache.Inputs.Count || current.Keys.Any(key => !cache.Inputs.ContainsKey(key))) return stale;
            }
            // Newly discovered inputs invalidate old manifests even if other files match.
            foreach (var file in project.Files)
                if (!cache.Inputs.ContainsKey(LocalCacheKey(project, file.Path))) return stale;
            foreach (var (key, signature) in cache.Inputs)
            {
                var input = CacheInputPath(project, key);
                if (input is null || await CacheInputSignatureAsync(project, input, ct) != signature) return stale;
            }
            foreach (var (name, signature) in cache.Outputs)
            {
                if (name != Path.GetFileName(name)) return stale;
                if (await FileSignatureAsync(Path.Combine(preview, name), ct) != signature) return stale;
            }
            if (!cache.Outputs.ContainsKey(job + ".pdf")) return stale;
            return new(pdf, true, sync);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A missing compiler/manifest does not prevent viewing a good PDF.
            return File.Exists(pdf) && IsOrdinaryInput(project.Root, pdf) ? new(pdf, false, false) : null;
        }
    }

    private static string LocalCacheKey(TexProject project, string path) => "local:" + Path.GetRelativePath(project.Root, path);
    private static string? CacheInputPath(TexProject project, string key)
    {
        if (key.StartsWith("local:", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(key[6..], project.Root);
            return IsWithin(project.Root, path) && !Path.GetRelativePath(project.Root, path).StartsWith(".texeed" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        return key.StartsWith("external:", StringComparison.Ordinal) && Path.IsPathRooted(key[9..]) ? key[9..] : null;
    }
    private static async Task<string> CacheInputSignatureAsync(TexProject project, string path, CancellationToken ct)
    {
        if (IsWithin(project.Root, path))
            return IsOrdinaryInput(project.Root, path) ? await FileSignatureAsync(path, ct) : "missing";
        var info = new FileInfo(path);
        return info.Exists ? $"stamp:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }
    private static async Task<Dictionary<string, string>> CaptureCacheInputsAsync(TexProject project, string recorder, CancellationToken ct, bool scanProject = false)
    {
        var paths = new HashSet<string>(project.Files.Select(f => f.Path), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (scanProject)
        {
            // Tectonic does not provide a TeX recorder. Conservatively include
            // local files rather than miss images opened through custom macros.
            var pending = new Queue<string>(); pending.Enqueue(project.Root);
            while (pending.TryDequeue(out var directory))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Directory.Exists(path))
                    {
                        if (Path.GetFileName(path) is not (".texeed" or ".git" or ".venv" or ".svn")) pending.Enqueue(path);
                    }
                    else paths.Add(path);
                    if (paths.Count > 10000) throw new IOException("编译缓存依赖过多。");
                }
            }
        }
        if (File.Exists(recorder))
        {
            if (!IsOrdinaryInput(project.Root, recorder) || new FileInfo(recorder).Length > 4 * 1024 * 1024)
                throw new IOException("无法完整核对编译依赖记录。");
            foreach (var line in (await ReadLimitedAsync(recorder, ct)).Split('\n'))
            {
                if (!line.StartsWith("INPUT ", StringComparison.Ordinal)) continue;
                try
                {
                    var path = Path.GetFullPath(line[6..].Trim().Trim('"'), project.Root);
                    if (IsWithin(project.Root, path) && Path.GetRelativePath(project.Root, path).StartsWith(".texeed" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    paths.Add(path);
                }
                catch (ArgumentException) { }
            }
        }
        if (paths.Count > 10000) throw new IOException("编译缓存依赖过多。");
        var signatures = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var key = IsWithin(project.Root, path) ? LocalCacheKey(project, path) : "external:" + path;
            signatures[key] = await CacheInputSignatureAsync(project, path, ct);
        }
        return signatures;
    }
    private static async Task WritePreviewCacheAsync(TexProject project, AppSettings settings, ToolInfo engine,
        string recorder, Dictionary<string, string> before, DateTime started, CancellationToken ct)
    {
        try
        {
            var after = await CaptureCacheInputsAsync(project, recorder, ct, scanProject: engine.Name == "tectonic");
            // Removed dependencies are no longer part of this build. Only a
            // dependency that remained active and changed mid-build is unsafe.
            if (before.Any(item => after.TryGetValue(item.Key, out var value) && value != item.Value)) return;
            foreach (var key in after.Keys.Except(before.Keys))
            {
                var input = CacheInputPath(project, key);
                if (input is null || File.GetLastWriteTimeUtc(input) > started) return;
            }
            var directory = Path.GetDirectoryName(CachePath(project))!;
            var job = Path.GetFileNameWithoutExtension(project.MainPath);
            var outputs = new Dictionary<string, string>();
            foreach (var extension in new[] { ".pdf", ".synctex", ".synctex.gz", TexPageLayout.Extension })
                outputs[job + extension] = await FileSignatureAsync(Path.Combine(directory, job + extension), ct);
            var cache = new PreviewCache(PreviewCacheVersion, project.Root, project.MainPath, CacheSettings(settings),
                engine.Name + "\n" + ToolIdentity(engine.Path), after, outputs);
            var path = CachePath(project);
            if (File.Exists(path + ".tmp") && (File.GetAttributes(path + ".tmp") & FileAttributes.ReparsePoint) != 0) return;
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(cache), ct);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
