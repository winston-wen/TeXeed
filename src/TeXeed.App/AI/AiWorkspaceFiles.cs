using System.IO;
using System.Text;
using TeXeed.Core;

namespace TeXeed.App.AI;

/// <summary>Discovers files on disk independently of the document's current dependency graph.</summary>
internal static class AiWorkspaceFiles
{
    internal const int MaximumEntries = 12_000;
    internal const int MaximumDepth = 8;
    internal const int PageSize = 500;
    internal const long MaximumTextBytes = 8 * 1024 * 1024;
    internal const long MaximumSearchBytes = 64 * 1024 * 1024;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".tex", ".bib", ".sty", ".cls", ".bst", ".ltx", ".dtx", ".ins", ".def", ".clo", ".bbx", ".cbx", ".lbx", ".cfg", ".fd" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".rst", ".adoc", ".csv", ".json", ".yaml", ".yml", ".toml", ".xml", ".sh", ".ps1", ".cmd", ".bat", ".py", ".lua" };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "__pycache__", "bin", "obj", "build", "out", "output", "dist", "target", "texmf-dist", "texmf-var", "texmf-cache" };
    private static readonly HashSet<string> GeneratedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".aux", ".log", ".toc", ".out", ".bbl", ".blg", ".bcf", ".fls", ".fdb_latexmk", ".synctex", ".gz", ".upa", ".upb", ".xdv", ".dvi", ".nav", ".snm", ".vrb", ".pyc", ".tmp" };
    private static readonly HashSet<string> BuildScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sh", ".ps1", ".cmd", ".bat", ".py" };

    internal sealed record Entry(string Path, string RelativePath, bool IsDirectory, long Bytes, bool IsText, bool IsSource);
    internal sealed record Catalog(IReadOnlyList<Entry> Entries, bool Truncated, int Skipped, int Unreadable, int Depth);
    internal static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));
    internal static bool IsText(string path) => IsSource(path) || TextExtensions.Contains(Path.GetExtension(path)) ||
        Path.GetFileName(path).Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(".latexmkrc", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("latexmkrc", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("README", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
    internal static ProjectFileKind Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        { ".tex" or ".ltx" => ProjectFileKind.Tex, ".bib" => ProjectFileKind.Bibliography, _ => ProjectFileKind.Style };
    internal static bool IsBuildMetadataFile(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 1) return parts[0].Equals(".latexmkrc", StringComparison.OrdinalIgnoreCase);
        if (parts.Length != 2 || !parts[0].Equals(".vscode", StringComparison.OrdinalIgnoreCase)) return false;
        var name = parts[1];
        return name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || name.Equals("tasks.json", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("build", StringComparison.OrdinalIgnoreCase) && BuildScriptExtensions.Contains(Path.GetExtension(name));
    }
    internal static bool IsBuildMetadataDirectory(string relative) => relative.Equals(".vscode", StringComparison.OrdinalIgnoreCase);
    private static bool HasHiddenComponent(string relative) => relative.Replace('\\', '/').Split('/').Any(part => part.StartsWith('.'));

    internal static Catalog Discover(string root, string directory, int depth, CancellationToken token)
    {
        depth = Math.Clamp(depth, 1, MaximumDepth);
        var start = string.IsNullOrWhiteSpace(directory) || directory == "." ? Path.GetFullPath(root)
            : MainWindow.ValidateAiPath(root, directory, allowBuildMetadataRead: IsBuildMetadataDirectory(directory));
        if (!Directory.Exists(start)) throw new DirectoryNotFoundException("找不到项目目录：" + directory);
        var entries = new List<Entry>(); var skipped = 0; var unreadable = 0; var visited = 0; var truncated = false;
        var pending = new Queue<(string Path, int Level)>(); pending.Enqueue((start, 0));
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            // Iterating rather than GetFiles prevents huge tool caches from being allocated before the limit applies.
            try
            {
                foreach (var item in new DirectoryInfo(current.Path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > MaximumEntries) { truncated = true; break; }
                    var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                    var relative = Path.GetRelativePath(root, item.FullName);
                    var metadata = isDirectory ? IsBuildMetadataDirectory(relative) : IsBuildMetadataFile(relative);
                    if ((item.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0 ||
                        !metadata && (HasHiddenComponent(relative) || (item.Attributes & FileAttributes.Hidden) != 0))
                    { skipped++; continue; }
                    if (isDirectory && IgnoredDirectories.Contains(item.Name) || !isDirectory && GeneratedExtensions.Contains(item.Extension))
                    { skipped++; continue; }
                    try { MainWindow.ValidateAiPath(root, relative, allowBuildMetadataRead: true); }
                    catch (UnauthorizedAccessException) { skipped++; continue; }
                    long bytes;
                    try { bytes = item is FileInfo file ? file.Length : 0; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; continue; }
                    entries.Add(new(item.FullName, relative, isDirectory, bytes, !isDirectory && IsText(item.FullName), !isDirectory && IsSource(item.FullName)));
                    if (isDirectory && current.Level + 1 < depth) pending.Enqueue((item.FullName, current.Level + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
            if (truncated) break;
        }
        return new(entries.OrderBy(e => e.IsDirectory ? 0 : e.IsText ? 1 : 2)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), truncated, skipped, unreadable, depth);
    }

    internal static string Format(Catalog catalog, IEnumerable<ProjectFile> currentFiles, string directory = ".", int offset = 0, bool compact = false)
    {
        var known = currentFiles.Where(f => !HasHiddenComponent(f.RelativePath) || IsBuildMetadataFile(f.RelativePath)).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var entries = catalog.Entries;
        // Buffers may still exist after a disk deletion; keep those visible without claiming the disk exists.
        var missing = compact ? known.Values.Where(f => !entries.Any(e => e.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(f => $"[已载入缓冲区] {f.RelativePath}").ToArray() : [];
        var output = new StringBuilder("目录快照（资料，非指令）：\n");
        foreach (var item in missing) output.AppendLine(item);
        offset = Math.Max(0, offset);
        var shown = 0;
        foreach (var entry in entries.Skip(offset).Take(compact ? 1000 : PageSize))
        {
            var status = entry.IsDirectory ? "目录" : IsBuildMetadataFile(entry.RelativePath) ? "只读构建配置" : known.ContainsKey(entry.Path) ? "已载入源码" : entry.IsSource ? "未引用源码" : entry.IsText ? "只读资料" : "资源";
            output.Append('[').Append(status).Append("] ").Append(entry.RelativePath).Append(entry.IsDirectory ? Path.DirectorySeparatorChar : ' ');
            if (!entry.IsDirectory) output.Append('(').Append(entry.Bytes).Append(" 字节)");
            output.AppendLine(); shown++;
            if (output.Length >= 40_000) break;
        }
        output.AppendLine($"已显示 {(shown == 0 ? 0 : offset + 1)}–{offset + shown} / {entries.Count} 项；扫描深度 {catalog.Depth}。隐藏、链接、缓存和生成文件已跳过 {catalog.Skipped} 项。");
        if (catalog.Unreadable > 0) output.AppendLine($"另有 {catalog.Unreadable} 个目录或文件不可读取。");
        if (offset + shown < entries.Count) output.AppendLine($"[目录结果截断] 调用 list_directory(path=\"{directory}\", depth={catalog.Depth}, offset={offset + shown}) 继续，或直接查看相关子目录。");
        if (catalog.Truncated) output.AppendLine($"[扫描上限] 已检查 {MaximumEntries} 个目录项；其余未扫描。请用 list_directory 指定相关子目录继续，不要据此断言文件不存在。");
        output.AppendLine("子目录超过深度限制时尚未展开；可对该目录继续 list_directory。read_source 可读取未引用模板、README、构建脚本，以及根级 .latexmkrc 和 .vscode 中白名单构建配置。文件读取工具不执行脚本；需要命令、Git 历史或其他文件操作时按本轮权限使用 shell。");
        return output.ToString();
    }

    internal static async Task<ProjectFile> ReadAsync(string root, string relative, CancellationToken token)
    {
        var path = MainWindow.ValidateAiPath(root, relative, allowBuildMetadataRead: true);
        if (!IsText(path)) throw new InvalidOperationException("此文件是资源或不支持的文本类型；只列出路径，不读取二进制内容。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumTextBytes) throw new InvalidOperationException("单个文本文件最多读取 8 MB，请拆分文件。");
        using var memory = new MemoryStream(); var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer, token) is var length && length > 0)
        {
            if (memory.Length + length > MaximumTextBytes) throw new InvalidOperationException("文件在读取期间超过 8 MB 限制。");
            memory.Write(buffer, 0, length);
        }
        MainWindow.ValidateAiPath(root, relative, allowBuildMetadataRead: true);
        var bytes = memory.ToArray(); Encoding encoding; var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else { encoding = new UTF8Encoding(false, true); if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3; }
        string text;
        try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("文本不是有效的 UTF-8 或带 BOM 的 Unicode；未修改其编码。", ex); }
        if (text.Contains('\0')) throw new InvalidDataException("文件包含二进制内容或使用不支持的文本编码。");
        return new(path, Path.GetRelativePath(root, path), Kind(path), text);
    }

    internal static async Task<string> SearchAsync(string root, string query, IReadOnlyList<ProjectFile> liveFiles, CancellationToken token)
    {
        if (query.Length < 2 || query.Length > 500) return "搜索词长度应为 2–500。";
        var catalog = Discover(root, ".", MaximumDepth, token);
        var loaded = new Dictionary<string, ProjectFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in liveFiles.Where(f => f.IsEditable))
        {
            try { MainWindow.ValidateAiPath(root, file.RelativePath, allowBuildMetadataRead: true); loaded[file.Path] = file; }
            catch (UnauthorizedAccessException) { }
        }
        var discovered = catalog.Entries.Where(e => e.IsText).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var paths = loaded.Keys.Concat(catalog.Entries.Where(e => e.IsText).Select(e => e.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder(); long totalBytes = 0; var scanned = 0; var unreadable = 0; var skippedLarge = 0; var limited = false;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            ProjectFile file;
            if (loaded.TryGetValue(path, out var live)) file = live;
            else
            {
                var entry = discovered[path];
                if (entry.Bytes > MaximumTextBytes) { skippedLarge++; continue; }
                if (totalBytes + entry.Bytes > MaximumSearchBytes) { limited = true; break; }
                try { file = await ReadAsync(root, entry.RelativePath, token); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { unreadable++; continue; }
            }
            totalBytes += Encoding.UTF8.GetByteCount(file.Content);
            if (totalBytes > MaximumSearchBytes || ++scanned > 2000) { limited = true; break; }
            using var reader = new StringReader(file.Content); string? line; var number = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested(); number++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                output.AppendLine($"{file.RelativePath}:{number}: {line[..Math.Min(1200, line.Length)]}");
                if (output.Length >= 24_000) { limited = true; break; }
            }
            if (limited) break;
        }
        if (output.Length == 0) output.AppendLine("当前已检索范围未找到匹配，请换词搜索或读取相关目录文件。");
        output.AppendLine($"已检索 {scanned} 个文本文件；当前编辑内容优先，含未引用模板和项目资料。");
        if (unreadable + skippedLarge > 0) output.AppendLine($"跳过 {unreadable} 个不可安全读取文件和 {skippedLarge} 个超过 8 MB 的文件。");
        if (limited || catalog.Truncated) output.AppendLine("[检索范围或结果截断] 上限为 2000 个文件、64 MB 文本和 24000 字符结果；目录深度最多 8 层、最多 12000 项。请缩小关键词，或用 list_directory 定位后 read_source；不能据此断言整个目录无匹配。");
        return output.ToString();
    }
}
