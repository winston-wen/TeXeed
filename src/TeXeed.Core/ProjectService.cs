using System.Text;

namespace TeXeed.Core;

public sealed class ProjectService
{
    private const int MaximumFiles = 512;
    private const long MaximumTextFileBytes = 8 * 1024 * 1024;
    private const long MaximumProjectTextBytes = 32 * 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string[] ImageExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".eps", ".svg", ".tif", ".tiff", ".bmp"];
    private static readonly HashSet<string> DependencyCommands = new(StringComparer.Ordinal)
    {
        "input", "include", "subfile", "import", "subimport", "inputfrom", "subinputfrom", "includefrom", "subincludefrom",
        "bibliography", "addbibresource", "addglobalbib", "addsectionbib", "usepackage", "RequirePackage",
        "documentclass", "LoadClass", "bibliographystyle", "includegraphics", "graphicspath"
    };

    public async Task<TexProject> LoadAsync(string mainPath, CancellationToken ct = default)
    {
        mainPath = Path.GetFullPath(mainPath);
        var root = Path.GetDirectoryName(mainPath) ?? throw new ArgumentException("主文件必须位于文件夹中。", nameof(mainPath));
        EnsureSafePath(root, mainPath);
        if (!File.Exists(mainPath)) throw new FileNotFoundException("找不到 LaTeX 主文件。", mainPath);
        var files = new List<ProjectFile>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        var queue = new Queue<(string Path, ProjectFileKind Kind)>();
        queue.Enqueue((mainPath, ProjectFileKind.Tex));
        long totalBytes = 0;
        while (queue.TryDequeue(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(entry.Path)) continue;
            if (files.Count >= MaximumFiles) { warnings.Add($"项目文件超过 {MaximumFiles} 个，已停止扫描。 "); break; }
            EnsureSafePath(root, entry.Path);
            var relativePath = Path.GetRelativePath(root, entry.Path);
            if (entry.Kind == ProjectFileKind.Asset)
            {
                files.Add(new(entry.Path, relativePath, entry.Kind, ""));
                continue;
            }
            try
            {
                var size = new FileInfo(entry.Path).Length;
                if (size > MaximumTextFileBytes || totalBytes + size > MaximumProjectTextBytes)
                {
                    if (PathComparer.Equals(entry.Path, mainPath)) throw new InvalidDataException("主文件过大；支持最大 8 MB 的文本文件。");
                    warnings.Add($"文件过大，已作为只读资源载入：{relativePath}");
                    files.Add(new(entry.Path, relativePath, ProjectFileKind.Asset, ""));
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(entry.Path, ct);
                totalBytes += bytes.Length;
                var document = Decode(bytes);
                files.Add(new(entry.Path, relativePath, entry.Kind, document.Text));
                if (entry.Kind == ProjectFileKind.Bibliography) continue;
                var code = LatexSyntax.Analyze(document.Text).Code;
                var commands = LatexSyntax.Commands(code, DependencyCommands).ToList();
                var graphicPaths = commands.Where(c => c.Name == "graphicspath" && c.Arguments.Count > 0)
                    .SelectMany(c => ReadGraphicPaths(c.Arguments[0])).ToArray();
                foreach (var command in commands)
                {
                    foreach (var dependency in Dependencies(command))
                    {
                        var value = dependency.Name.Trim().Trim('"');
                        if (value.Length == 0) continue;
                        if (value.IndexOfAny(['\\', '$', '#', '{', '}', '\0']) >= 0)
                        {
                            warnings.Add($"无法静态解析动态路径：{value}（{relativePath}:{LatexSyntax.LineAt(document.Text, command.Index)}）");
                            continue;
                        }
                        var directories = new List<string> { Path.GetDirectoryName(entry.Path)!, root };
                        if (dependency.Kind == ProjectFileKind.Asset)
                            foreach (var graphicPath in graphicPaths)
                            {
                                if (graphicPath.IndexOfAny(['\\', '$', '#', '{', '}']) >= 0) continue;
                                directories.Add(Path.Combine(Path.GetDirectoryName(entry.Path)!, graphicPath));
                                directories.Add(Path.Combine(root, graphicPath));
                            }
                        string? found = null;
                        var blocked = false;
                        foreach (var directory in directories.Distinct(PathComparer))
                        {
                            foreach (var extension in Path.HasExtension(value) ? new[] { "" } : dependency.Extensions)
                            {
                                string candidate;
                                try
                                {
                                    candidate = Path.GetFullPath(Path.Combine(directory, value + extension));
                                    EnsureSafePath(root, candidate);
                                }
                                catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
                                {
                                    blocked = true;
                                    continue;
                                }
                                if (File.Exists(candidate)) { found = candidate; break; }
                            }
                            if (found is not null) break;
                        }
                        if (found is not null) queue.Enqueue((found, dependency.Kind));
                        else if (blocked) warnings.Add($"依赖路径越过项目根目录或包含符号链接，已跳过：{value}");
                        else if (!dependency.Optional) warnings.Add($"找不到引用文件：{value}（来自 {relativePath}）");
                    }
                }
            }
            catch (Exception ex) when (ex is DecoderFallbackException || ex is InvalidDataException && !PathComparer.Equals(entry.Path, mainPath))
            {
                if (PathComparer.Equals(entry.Path, mainPath)) throw new InvalidDataException("主文件不是有效的 UTF-8 或带 BOM 的 Unicode 文本，请先转换文件编码。", ex);
                warnings.Add($"无法安全识别文本编码，已作为只读资源载入：{relativePath}");
                files.Add(new(entry.Path, relativePath, ProjectFileKind.Asset, ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !PathComparer.Equals(entry.Path, mainPath))
            {
                warnings.Add($"无法读取文件 {relativePath}：{ex.Message}");
            }
        }
        string? name = null;
        var metadataPath = Path.Combine(root, ".texeed", "project.json");
        try
        {
            EnsureSafePath(root, metadataPath);
            if (File.Exists(metadataPath) && new FileInfo(metadataPath).Length <= 16384)
                name = System.Text.Json.JsonSerializer.Deserialize<ProjectMetadata>(await File.ReadAllTextAsync(metadataPath, ct))?.Name;
            name = name?.Trim();
            if (name is not null && (name.Length > 200 || name.Any(char.IsControl)))
            { warnings.Add("项目名称配置无效，已使用目录名。"); name = null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { warnings.Add("无法读取项目名称配置：" + ex.Message); }
        return new TexProject(root, mainPath, files, warnings.Distinct().ToArray()) { ConfiguredName = name };
    }

    public async Task<TexProject> SetNameAsync(TexProject project, string name)
    {
        name = name.Trim();
        if (name.Length > 200 || name.Any(char.IsControl)) throw new ArgumentException("项目名称最多 200 字，且不能包含换行或控制字符。");
        var path = Path.Combine(project.Root, ".texeed", "project.json");
        EnsureSafePath(project.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(new ProjectMetadata(name)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return project with { ConfiguredName = name };
    }
    private sealed record ProjectMetadata(string? Name);

    public async Task<TexProject> SaveAsync(TexProject project, IReadOnlyDictionary<string, string> changes, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(project.Root);
        EnsureSafePath(root, project.MainPath);
        var knownFiles = project.Files.ToDictionary(f => Path.GetFullPath(f.Path), PathComparer);
        var prepared = new List<PendingSave>();
        var seen = new HashSet<string>(PathComparer);
        try
        {
            // Validate the complete batch before changing any source file.
            foreach (var (path, content) in changes)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                EnsureSafePath(root, fullPath);
                if (!seen.Add(fullPath)) throw new ArgumentException($"重复的文件路径：{path}", nameof(changes));
                if (!knownFiles.TryGetValue(fullPath, out var original) || !original.IsEditable)
                    throw new UnauthorizedAccessException($"不允许写入尚未载入的文件或资源：{path}");
                if (!File.Exists(fullPath)) throw new IOException($"文件已被删除，请重新载入项目：{path}");
                var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                var disk = Decode(bytes);
                if (!string.Equals(disk.Text, original.Content, StringComparison.Ordinal))
                    throw new IOException($"文件已被其他程序修改。请先重新载入，避免覆盖更改：{original.RelativePath}");
                if (string.Equals(content, original.Content, StringComparison.Ordinal)) continue;
                var encoded = disk.Encoding.GetBytes(content);
                if (encoded.LongLength + disk.Preamble.Length > MaximumTextFileBytes)
                    throw new InvalidDataException($"文本文件不能超过 8 MB：{original.RelativePath}");
                var output = new byte[disk.Preamble.Length + encoded.Length];
                disk.Preamble.CopyTo(output, 0);
                encoded.CopyTo(output, disk.Preamble.Length);
                var tempPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".texeed-{Guid.NewGuid():N}.tmp");
                prepared.Add(new(fullPath, tempPath, bytes));
                await using var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(output, ct);
                await stream.FlushAsync(ct);
            }
            foreach (var save in prepared)
            {
                ct.ThrowIfCancellationRequested();
                EnsureSafePath(root, save.Path);
                if (!(await File.ReadAllBytesAsync(save.Path, ct)).AsSpan().SequenceEqual(save.OriginalBytes))
                    throw new IOException($"保存时文件被其他程序修改：{save.Path}");
            }
            foreach (var save in prepared)
            {
                // Same-directory rename replaces each individual file atomically.
                EnsureSafePath(root, save.Path);
                File.Move(save.TempPath, save.Path, true);
            }
        }
        finally
        {
            foreach (var save in prepared)
                try { if (File.Exists(save.TempPath)) File.Delete(save.TempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
        return await LoadAsync(project.MainPath, ct);
    }

    private static IEnumerable<Dependency> Dependencies(LatexCommand command)
    {
        if (command.Arguments.Count == 0) yield break;
        var first = command.Arguments[0];
        switch (command.Name)
        {
            case "input": case "include": case "subfile":
                yield return new(first, ["", ".tex"], ProjectFileKind.Tex); break;
            case "import": case "subimport": case "inputfrom": case "subinputfrom": case "includefrom": case "subincludefrom":
                if (command.Arguments.Count > 1)
                    yield return new(first.TrimEnd('/') + "/" + command.Arguments[1], ["", ".tex"], ProjectFileKind.Tex);
                break;
            case "bibliography":
                foreach (var name in first.Split(',')) yield return new(name, [".bib"], ProjectFileKind.Bibliography);
                break;
            case "addbibresource": case "addglobalbib": case "addsectionbib":
                yield return new(first, [".bib"], ProjectFileKind.Bibliography); break;
            case "usepackage": case "RequirePackage":
                foreach (var name in first.Split(',')) yield return new(name, [".sty"], ProjectFileKind.Style, true);
                break;
            case "documentclass": case "LoadClass":
                yield return new(first, [".cls"], ProjectFileKind.Style, true); break;
            case "bibliographystyle":
                yield return new(first, [".bst"], ProjectFileKind.Style, true); break;
            case "includegraphics":
                yield return new(first, ImageExtensions, ProjectFileKind.Asset); break;
        }
    }

    private static IEnumerable<string> ReadGraphicPaths(string argument)
    {
        var pos = 0;
        while (pos < argument.Length)
        {
            var group = LatexSyntax.ReadGroup(argument, ref pos, '{', '}');
            if (group is null) yield break;
            yield return group;
        }
    }

    private static void EnsureSafePath(string root, string candidate)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        candidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"文件不在项目目录内：{candidate}");
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException($"项目内的符号链接不允许自动读取或写入：{current}");
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }

    private static DecodedText Decode(byte[] bytes)
    {
        Encoding encoding;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else
        {
            encoding = new UTF8Encoding(false, true);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        }
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidDataException("文件包含二进制内容或使用不支持的文本编码。");
        return new(text, encoding, bytes[..skip]);
    }

    private sealed record Dependency(string Name, string[] Extensions, ProjectFileKind Kind, bool Optional = false);
    private sealed record DecodedText(string Text, Encoding Encoding, byte[] Preamble);
    private sealed record PendingSave(string Path, string TempPath, byte[] OriginalBytes);
}
