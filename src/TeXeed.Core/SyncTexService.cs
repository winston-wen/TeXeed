using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace TeXeed.Core;

/// <summary>SyncTeX positions are 1-based pages, with coordinates in PDF points
/// (72 per inch) measured from the page's top left. Returned boxes use their top edge.</summary>
public sealed class SyncTexService
{
    private string? cachedPath;
    private DateTime cachedTime;
    private long cachedSize;
    private SyncIndex cachedIndex = SyncIndex.Empty;
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private static readonly ConditionalWeakTable<string, EnvironmentMap> EnvironmentMaps = new();
    private static readonly ConditionalWeakTable<FileIndex, ConcurrentDictionary<(int, int), GraphicProjection>> GraphicMaps = new();
    private static readonly ConditionalWeakTable<FileIndex, ConcurrentDictionary<(int, int), BlockProjection>> BlockMaps = new();
    private static readonly HashSet<string> BlockEnvironments = new(StringComparer.Ordinal)
    { "algorithm", "algorithmic", "algorithmicx", "lstlisting", "listing", "minted", "verbatim", "Verbatim", "tabular", "tabularx", "longtable", "supertabular", "tikzpicture", "pspicture", "picture" };
    private sealed record EnvironmentRange(string Name, int Start, int End)
    {
        public bool IsGraphic => Name is "tikzpicture" or "pspicture" or "picture";
    }
    private sealed record EnvironmentMap(EnvironmentRange[] Ranges);
    private sealed record GraphicProjection(PdfLocation? Box);
    private sealed record BlockProjection(PdfLocation[] Rows);

    private static EnvironmentMap ReadEnvironments(string contents)
    {
        var ranges = new List<EnvironmentRange>();
        var stack = new Stack<(string Name, int Line)>();
        var lines = contents.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = Regex.Match(line, @"(?<!\\)(?:\\\\)*%");
            if (comment.Success) line = line[..comment.Index];
            foreach (Match match in Regex.Matches(line, @"\\(begin|end)\s*\{([^{}]+)\}"))
            {
                var name = match.Groups[2].Value;
                if (match.Groups[1].Value == "begin") stack.Push((name, i + 1));
                else if (stack.TryPeek(out var opening) && opening.Name == name)
                {
                    stack.Pop();
                    if (BlockEnvironments.Contains(name)) ranges.Add(new(name, opening.Line, i + 1));
                }
            }
        }
        return new(ranges.OrderBy(r => r.End - r.Start).ToArray());
    }

    private static EnvironmentMap? Environments(TexProject project, SourceLocation source)
    {
        var contents = project.Files.FirstOrDefault(f => PathsEqual(f.Path, source.Path))?.Content;
        return contents is null ? null : EnvironmentMaps.GetValue(contents, ReadEnvironments);
    }

    public async Task<PdfLocation?> ForwardAsync(TexProject project, string pdfPath, SourceLocation source, AppSettings settings, CancellationToken ct = default)
    {
        var valid = ResolveSource(project, source.Path, source.Line, source.Column);
        if (valid is null || !ValidOutput(project, pdfPath)) return null;
        var command = GetExecutable(settings);
        if (command is not null)
        {
            var position = FormattableString.Invariant($"{valid.Line}:{valid.Column}:{valid.Path}");
            var output = await QueryAsync(command, ["view", "-i", position, "-o", pdfPath], CompileService.EntryDirectory(project), ct);
            var result = ParseForwardOutput(output);
            if (result is not null) return result;
        }
        return await NativeForwardAsync(project, pdfPath, valid, ct, contentOnly: false);
    }

    /// <summary>Follow source text, excluding shipout boxes, rules and positioning kerns
    /// that can inherit a later source line while still belonging to the previous page.</summary>
    public async Task<PdfLocation?> ForwardViewportAsync(TexProject project, string pdfPath, SourceLocation source, AppSettings settings, CancellationToken ct = default)
    {
        var valid = ResolveSource(project, source.Path, source.Line, source.Column);
        if (valid is null || !ValidOutput(project, pdfPath)) return null;
        return await NativeForwardAsync(project, pdfPath, valid, ct, contentOnly: true)
            ?? await ForwardAsync(project, pdfPath, source, settings, ct);
    }

    private async Task<PdfLocation?> NativeForwardAsync(TexProject project, string pdfPath, SourceLocation valid, CancellationToken ct, bool contentOnly)
    {
        var index = await GetIndexAsync(pdfPath, ct).ConfigureAwait(false);
        SyncNode? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var (input, file) in index.Files)
        {
            ct.ThrowIfCancellationRequested();
            try { if (!PathsEqual(Path.GetFullPath(input.Trim('"'), CompileService.EntryDirectory(project)), valid.Path)) continue; }
            catch (Exception ex) when (ex is ArgumentException or IOException) { continue; }
            var boundary = Array.BinarySearch(file.Lines, valid.Line);
            if (boundary < 0) boundary = ~boundary;
            if (contentOnly && ApproximateGraphic(project, valid, file, boundary) is { } graphic)
                return graphic;
            if (contentOnly && ApproximateBlock(project, valid, file, boundary) is { } block)
                return block;
            if (contentOnly && boundary < file.Lines.Length && file.Lines[boundary] != valid.Line &&
                InterpolateEnvironment(project, valid, file, boundary) is { } interpolated)
                return interpolated;
            var left = boundary - 1; var right = boundary;
            while (left >= 0 || right < file.Lines.Length)
            {
                ct.ThrowIfCancellationRequested();
                var row = right >= file.Lines.Length || left >= 0 && valid.Line - file.Lines[left] <= file.Lines[right] - valid.Line ? left-- : right++;
                var lineDistance = Math.Abs(file.Lines[row] - valid.Line) * 1_000_000d;
                if (lineDistance > bestDistance) break;
                foreach (var node in file.Rows[row])
                {
                    if (contentOnly && !node.IsContent) continue;
                    var distance = lineDistance + (node.IsContent ? 0 : node.IsBox ? 200_000 : 100_000)
                        + (node.Column > 0 ? Math.Abs(node.Column - valid.Column) : 0);
                    if (distance < bestDistance || distance == bestDistance && (best is null || node.Order < best.Order))
                    { best = node; bestDistance = distance; }
                }
            }
        }
        return best?.Box;
    }

    // Some verbatim packages assign every output row to the closing source line.
    // The rows themselves remain in SyncTeX, even across page boundaries.
    private static PdfLocation? ApproximateBlock(TexProject project, SourceLocation source, FileIndex file, int boundary)
    {
        var range = Environments(project, source)?.Ranges.FirstOrDefault(r => !r.IsGraphic && r.Start < source.Line && source.Line <= r.End && r.End - r.Start >= 4);
        if (range is null) return null;
        if (source.Line != range.End && boundary < file.Lines.Length && file.Lines[boundary] == source.Line && file.Rows[boundary].Any(n => n.IsContent)) return null;
        var projection = BlockMaps.GetValue(file, _ => new()).GetOrAdd((range.Start, range.End), _ => InspectBlock(file, range));
        if (projection.Rows.Length == 0) return null;
        var fraction = (double)(source.Line - range.Start - 1) / (range.End - range.Start - 2);
        return projection.Rows[(int)Math.Round(Math.Clamp(fraction, 0, 1) * (projection.Rows.Length - 1))];
    }

    private static BlockProjection InspectBlock(FileIndex file, EnvironmentRange range)
    {
        var first = Array.BinarySearch(file.Lines, range.Start + 1);
        if (first < 0) first = ~first;
        for (var i = first; i < file.Lines.Length && file.Lines[i] < range.End; i++)
            if (file.Rows[i].Any(n => n.IsContent)) return new([]);
        var last = Array.BinarySearch(file.Lines, range.End);
        if (last < 0) return new([]);
        var nodes = file.Rows[last].Where(n => n.IsContent).Select(n => n.Box).OrderBy(n => n.Page).ThenBy(n => n.Y).ToArray();
        var rows = new List<PdfLocation>();
        foreach (var node in nodes)
            if (rows.Count == 0 || node.Page != rows[^1].Page || Math.Abs(node.Y - rows[^1].Y) > 2)
                rows.Add(node);
            else
            {
                var row = rows[^1]; var x = Math.Min(row.X, node.X);
                rows[^1] = new(row.Page, x, Math.Min(row.Y, node.Y), Math.Max(row.X + row.Width, node.X + node.Width) - x, Math.Max(row.Height, node.Height));
            }
        return rows.Count >= 3 && rows.Count <= (range.End - range.Start) * 4 ? new(rows.ToArray()) : new([]);
    }

    // Missing SyncTeX lines inside a typeset environment can be approximated only
    // when content anchors surround them in source and on the same PDF page.
    private static PdfLocation? InterpolateEnvironment(TexProject project, SourceLocation source, FileIndex file, int boundary)
    {
        var left = boundary - 1;
        while (left >= 0 && !file.Rows[left].Any(n => n.IsContent)) left--;
        var right = boundary;
        while (right < file.Lines.Length && !file.Rows[right].Any(n => n.IsContent)) right++;
        if (left < 0 || right >= file.Lines.Length || file.Lines[right] - file.Lines[left] < 3) return null;
        var a = file.Rows[left].First(n => n.IsContent).Box;
        var b = file.Rows[right].First(n => n.IsContent).Box;
        if (a.Page != b.Page || b.Y - a.Y < 20 || b.Y - a.Y > 600) return null;
        if (Environments(project, source)?.Ranges.Any(r => !r.IsGraphic && r.Start <= file.Lines[left] && r.End >= file.Lines[right]) != true) return null;
        var fraction = (double)(source.Line - file.Lines[left]) / (file.Lines[right] - file.Lines[left]);
        return new(a.Page, a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction, 3, 6);
    }

    private static PdfLocation? ApproximateGraphic(TexProject project, SourceLocation source, FileIndex file, int boundary)
    {
        // Only consider a graphic when its own SyncTeX nodes collapse onto one
        // location. Normal line mappings are more accurate than this estimate.
        if (boundary >= file.Lines.Length || file.Lines[boundary] != source.Line) return null;
        var range = Environments(project, source)?.Ranges.FirstOrDefault(r => r.IsGraphic && r.Start < source.Line && source.Line < r.End && r.End - r.Start >= 4);
        if (range is null) return null;
        var box = GraphicMaps.GetValue(file, _ => new()).GetOrAdd((range.Start, range.End), _ => InspectGraphic(file, range)).Box;
        if (box is null) return null;
        var fraction = (double)(source.Line - range.Start - 1) / (range.End - range.Start - 2);
        return new(box.Page, box.X, box.Y + box.Height * Math.Clamp(fraction, 0, 1), box.Width, 6);
    }

    private static GraphicProjection InspectGraphic(FileIndex file, EnvironmentRange range)
    {
        var first = Array.BinarySearch(file.Lines, range.Start + 1);
        if (first < 0) first = ~first;
        var count = 0;
        PdfLocation? point = null;
        for (var i = first; i < file.Lines.Length && file.Lines[i] < range.End; i++)
        {
            var node = file.Rows[i].FirstOrDefault(n => n.IsContent);
            if (node is null) continue;
            point ??= node.Box;
            if (node.Box.Page != point.Page || Math.Abs(node.Box.Y - point.Y) > 2) return new(null);
            count++;
        }
        if (count < 3 || point is null) return new(null);
        var last = Array.BinarySearch(file.Lines, range.End);
        if (last < 0) return new(null);
        var box = file.Rows[last].Where(n => n.IsBox && n.Box.Page == point.Page && n.Box.Height is >= 20 and <= 700 &&
                n.Box.Width is >= 6 and <= 600 && n.Box.Y >= 0 && point.X >= n.Box.X - 6 && point.X <= n.Box.X + n.Box.Width + 6)
            .OrderByDescending(n => n.Box.Height).FirstOrDefault()?.Box;
        return new(box);
    }

    /// <summary>Prepare the immutable native index while the PDF is loading.</summary>
    public async Task PrepareAsync(string pdfPath, CancellationToken ct = default)
    {
        try { await GetIndexAsync(pdfPath, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<SourceLocation?> InverseAsync(TexProject project, string pdfPath, PdfLocation point, AppSettings settings, CancellationToken ct = default)
    {
        if (point.Page < 1 || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || !ValidOutput(project, pdfPath)) return null;
        var index = await GetIndexAsync(pdfPath, ct).ConfigureAwait(false);
        if (InverseGraphic(project, index, point) is { } graphic) return graphic;
        if (InverseBlock(project, index, point) is { } block) return block;
        var command = GetExecutable(settings);
        if (command is not null)
        {
            var position = FormattableString.Invariant($"{point.Page}:{point.X}:{point.Y}:{pdfPath}");
            var output = await QueryAsync(command, ["edit", "-o", position], CompileService.EntryDirectory(project), ct);
            var result = ParseInverseOutput(output, project);
            if (result is not null) return result;
        }
        var files = new Dictionary<string, ValidatedFile?>();
        SourceLocation? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var node in index.Pages.GetValueOrDefault(point.Page) ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var source = ResolveCachedSource(project, files, node);
            if (source is null) continue;
            var xDistance = Math.Max(Math.Max(node.Box.X - point.X, point.X - node.Box.X - node.Box.Width), 0);
            var yDistance = Math.Max(Math.Max(node.Box.Y - point.Y, point.Y - node.Box.Y - node.Box.Height), 0);
            var distance = yDistance * yDistance * 16 + xDistance * xDistance
                + (node.IsBox ? 16 + node.Box.Width * node.Box.Height / 1000 : 0);
            if (distance < bestDistance) { best = source; bestDistance = distance; }
        }
        return best;
    }

    private static SourceLocation? InverseGraphic(TexProject project, SyncIndex index, PdfLocation point)
    {
        foreach (var (input, file) in index.Files)
        {
            var validFile = ValidateFile(project, input);
            if (validFile is null) continue;
            var ranges = EnvironmentMaps.GetValue(validFile.File.Content, ReadEnvironments).Ranges;
            foreach (var range in ranges.Where(r => r.IsGraphic && r.End - r.Start >= 4))
            {
                var box = GraphicMaps.GetValue(file, _ => new()).GetOrAdd((range.Start, range.End), _ => InspectGraphic(file, range)).Box;
                if (box is null || box.Page != point.Page || point.X < box.X || point.X > box.X + box.Width || point.Y < box.Y || point.Y > box.Y + box.Height) continue;
                var fraction = Math.Clamp((point.Y - box.Y) / box.Height, 0, 1);
                var line = range.Start + 1 + (int)Math.Round(fraction * (range.End - range.Start - 2));
                return ClampSource(validFile, line, 1);
            }
        }
        return null;
    }

    private static SourceLocation? InverseBlock(TexProject project, SyncIndex index, PdfLocation point)
    {
        foreach (var (input, file) in index.Files)
        {
            var validFile = ValidateFile(project, input);
            if (validFile is null) continue;
            var ranges = EnvironmentMaps.GetValue(validFile.File.Content, ReadEnvironments).Ranges;
            foreach (var range in ranges.Where(r => !r.IsGraphic && r.End - r.Start >= 4))
            {
                var rows = BlockMaps.GetValue(file, _ => new()).GetOrAdd((range.Start, range.End), _ => InspectBlock(file, range)).Rows;
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    if (row.Page != point.Page || point.X < row.X || point.X > row.X + row.Width || point.Y < row.Y - 2 || point.Y > row.Y + row.Height + 2) continue;
                    var fraction = (double)i / (rows.Length - 1);
                    var line = range.Start + 1 + (int)Math.Round(fraction * (range.End - range.Start - 2));
                    return ClampSource(validFile, line, 1);
                }
            }
        }
        return null;
    }

    public static PdfLocation? ParseForwardOutput(string output)
    {
        foreach (var record in Records(output))
        {
            if (!Number(record, "Page", out var page) || page < 1 || page > int.MaxValue || page != Math.Truncate(page)) continue;
            // SyncTeX's h,v,W,H describe a box whose v is the LOWER edge.
            // x,y describe a point and must not be mixed with the box dimensions.
            if (Number(record, "h", out var horizontal) && Number(record, "v", out var vertical)
                && Number(record, "W", out var width) && Number(record, "H", out var height))
            {
                return new((int)page, width < 0 ? horizontal + width : horizontal,
                    vertical - Math.Abs(height), Math.Abs(width), Math.Max(1, Math.Abs(height)));
            }
            if (Number(record, "x", out var x) && Number(record, "y", out var y))
                return new((int)page, x, y, 6, 12);
        }
        return null;
    }

    public static SourceLocation? ParseInverseOutput(string output, TexProject project)
    {
        foreach (var record in Records(output))
        {
            if (!record.TryGetValue("Input", out var input) || !Number(record, "Line", out var line) || line < 1 || line > int.MaxValue || line != Math.Truncate(line)) continue;
            var column = Number(record, "Column", out var value) && value > 0 && value < int.MaxValue ? (int)value : 1;
            var source = ResolveSource(project, input, (int)line, column);
            if (source is not null) return source;
        }
        return null;
    }

    private static IEnumerable<Dictionary<string, string>> Records(string output)
    {
        Dictionary<string, string>? record = null;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            if (key == "Output")
            {
                if (record is not null) yield return record;
                record = new(StringComparer.Ordinal);
            }
            if (record is not null) record[key] = line[(colon + 1)..].Trim();
        }
        if (record is not null) yield return record;
    }

    private static bool Number(Dictionary<string, string> record, string key, out double value)
    {
        value = 0;
        return record.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static string? GetExecutable(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SynctexPath))
        {
            var custom = Environment.ExpandEnvironmentVariables(settings.SynctexPath.Trim().Trim('"'));
            return File.Exists(custom) ? Path.GetFullPath(custom) : null;
        }
        var besideEngine = string.IsNullOrWhiteSpace(settings.EnginePath) ? null : Path.GetDirectoryName(settings.EnginePath);
        return CompileService.FindTool("synctex", CompileService.ToolDirectories(besideEngine));
    }

    private static async Task<string> QueryAsync(string command, string[] arguments, string root, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await CompileService.RunProcessAsync(command, arguments, root, null, timeout.Token);
            return result.ExitCode == 0 ? result.Output : "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ""; }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
    }

    private static bool ValidOutput(TexProject project, string pdfPath)
    {
        try { return CompileService.IsWithin(project.Root, pdfPath) && File.Exists(pdfPath); }
        catch (ArgumentException) { return false; }
    }

    private static SourceLocation? ResolveSource(TexProject project, string input, int line, int column)
    {
        if (line < 1) return null;
        return ClampSource(ValidateFile(project, input), line, column);
    }

    private sealed record ValidatedFile(ProjectFile File, string[] Lines);

    private static SourceLocation? ResolveCachedSource(TexProject project, Dictionary<string, ValidatedFile?> cache, SyncNode node)
    {
        if (!cache.TryGetValue(node.Input, out var file))
        {
            file = ValidateFile(project, node.Input);
            cache[node.Input] = file;
        }
        return ClampSource(file, node.Line, node.Column);
    }

    private static SourceLocation? ClampSource(ValidatedFile? file, int line, int column)
    {
        if (file is null || line < 1) return null;
        line = Math.Clamp(line, 1, file.Lines.Length);
        column = Math.Clamp(column, 1, file.Lines[line - 1].TrimEnd('\r').Length + 1);
        return new(file.File.Path, line, column);
    }

    private static ValidatedFile? ValidateFile(TexProject project, string input)
    {
        try
        {
            var path = Path.GetFullPath(input.Trim('"'), CompileService.EntryDirectory(project));
            if (!CompileService.IsWithin(project.Root, path)) return null;
            var file = project.Files.FirstOrDefault(f => f.IsEditable && PathsEqual(f.Path, path));
            if (file is null) return null;
            // An included path may be a symlink to something outside the project.
            var currentPath = Path.GetFullPath(project.Root);
            foreach (var segment in Path.GetRelativePath(project.Root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (File.Exists(currentPath) || Directory.Exists(currentPath))
                    if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0) return null;
            }
            return new(file, file.Content.Split('\n'));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record SyncNode(string Input, int Line, int Column, PdfLocation Box, bool IsBox, bool IsContent, int Order);
    private sealed record FileIndex(int[] Lines, SyncNode[][] Rows);
    private sealed record SyncIndex(Dictionary<string, FileIndex> Files, Dictionary<int, SyncNode[]> Pages)
    {
        public static SyncIndex Empty { get; } = new([], []);
        public static SyncIndex Build(IReadOnlyList<SyncNode> nodes)
        {
            var files = nodes.GroupBy(n => n.Input).ToDictionary(g => g.Key, g =>
            {
                var rows = g.GroupBy(n => n.Line).OrderBy(row => row.Key).ToArray();
                return new FileIndex(rows.Select(row => row.Key).ToArray(), rows.Select(row => row.ToArray()).ToArray());
            });
            return new(files, nodes.GroupBy(n => n.Box.Page).ToDictionary(g => g.Key, g => g.ToArray()));
        }
    }
    private sealed record RawNode(int Page, int Tag, int Line, int Column, double X, double Y, double Width, double Height, double Depth, bool IsBox, bool IsContent);

    private async Task<SyncIndex> GetIndexAsync(string pdfPath, CancellationToken ct)
    {
        await cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plain = Path.ChangeExtension(pdfPath, ".synctex");
            var path = new[] { plain, plain + ".gz" }.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (path is null) return SyncIndex.Empty;
            var info = new FileInfo(path);
            if (cachedPath == path && cachedTime == info.LastWriteTimeUtc && cachedSize == info.Length) return cachedIndex;
            if (info.Length > 64 * 1024 * 1024) return SyncIndex.Empty;
            await using var file = File.OpenRead(path);
            using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? (Stream)new GZipStream(file, CompressionMode.Decompress) : file;
            using var text = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (text.Length + count > 64 * 1024 * 1024) return SyncIndex.Empty;
                text.Write(buffer, 0, count);
            }
            // Windows TeX Live writes ANSI input filenames into SyncTeX; UTF-8
            // replacement characters would make every Chinese path unresolvable.
            var nativeText = CompileService.DecodeToolText(text.ToArray()).TrimStart('\uFEFF');
            var index = await Task.Run(() => SyncIndex.Build(ParseNative(nativeText, ct)), ct).ConfigureAwait(false);
            cachedPath = path;
            cachedTime = info.LastWriteTimeUtc;
            cachedSize = info.Length;
            cachedIndex = index;
            return index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return SyncIndex.Empty; }
        finally { cacheGate.Release(); }
    }

    // The native fallback handles ordinary boxes and glyph/glue/math records, including
    // compressed coordinates. The official CLI remains preferred for PDF form objects
    // and the complete SyncTeX matching algorithm.
    private static IReadOnlyList<SyncNode> ParseNative(string text, CancellationToken ct = default)
    {
        if (!text.StartsWith("SyncTeX Version:", StringComparison.Ordinal) || !text.Contains("Postamble:", StringComparison.Ordinal)) return [];
        var inputs = new Dictionary<int, string>();
        var raw = new List<RawNode>();
        var stack = new Stack<(char Kind, RawNode Box)>();
        double unit = 8192, magnification = 1000, xOffset = 578, yOffset = 578, postMagnification = 1;
        double? postX = null, postY = null;
        double lastY = 0;
        var page = 0;
        var postScriptum = false;
        var inForm = false;
        foreach (var record in text.Replace("\r", "").Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            if (record.Length == 0) continue;
            if (record == "Post Scriptum:") { postScriptum = true; continue; }
            if (record.StartsWith("Input:", StringComparison.Ordinal))
            {
                var split = record.IndexOf(':', 6);
                if (split > 6 && int.TryParse(record.AsSpan(6, split - 6), out var tag)) inputs[tag] = record[(split + 1)..];
                continue;
            }
            if (record.StartsWith("Magnification:", StringComparison.Ordinal))
            {
                if (double.TryParse(record.AsSpan(14), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                {
                    if (postScriptum) postMagnification = value; else magnification = value;
                }
                continue;
            }
            if (record.StartsWith("Unit:", StringComparison.Ordinal))
            {
                if (double.TryParse(record.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0) unit = value;
                continue;
            }
            if (record.StartsWith("X Offset:", StringComparison.Ordinal) || record.StartsWith("Y Offset:", StringComparison.Ordinal))
            {
                var horizontal = record[0] == 'X';
                if (postScriptum)
                {
                    var value = DimensionInPdfPoints(record[9..]);
                    if (horizontal) postX = value; else postY = value;
                }
                else if (double.TryParse(record.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    if (horizontal) xOffset = value; else yOffset = value;
                }
                continue;
            }
            if (record[0] == '{') { int.TryParse(record.AsSpan(1), out page); stack.Clear(); continue; }
            if (record[0] == '}') { page = 0; stack.Clear(); continue; }
            if (record[0] == '<') { inForm = true; continue; }
            if (record[0] == '>') { inForm = false; continue; }
            if (inForm || page <= 0) continue;
            if (record[0] is ')' or ']') { if (stack.Count > 0) stack.Pop(); continue; }
            if ("[(vhxgk$".IndexOf(record[0]) < 0) continue;
            var fields = record[1..].Split(':');
            if (fields.Length < 2) continue;
            var link = fields[0].Split(',');
            var coordinates = fields[1].Split(',');
            if (link.Length < 2 || coordinates.Length < 2 || !int.TryParse(link[0], out var inputTag) || !int.TryParse(link[1], out var sourceLine)
                || !double.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) continue;
            double y;
            if (coordinates[1] == "=") y = lastY;
            else if (double.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) lastY = y;
            else continue;
            var column = link.Length > 2 && int.TryParse(link[2], out var parsedColumn) ? parsedColumn : 1;
            var isBox = record[0] is '[' or '(' or 'v' or 'h';
            double width = 0, height = 0, depth = 0;
            if (fields.Length > 2)
            {
                var size = fields[2].Split(',');
                double.TryParse(size[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                if (size.Length > 1) double.TryParse(size[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                if (size.Length > 2) double.TryParse(size[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out depth);
            }
            if (!isBox)
            {
                // Packages often wrap each fragment in a zero-height horizontal box.
                // Its enclosing line still supplies the glyph's usable vertical extent.
                var parent = stack.FirstOrDefault(item => item.Kind == '(' && item.Box.Height + item.Box.Depth > 0).Box;
                if (parent is not null) { height = parent.Height; depth = parent.Depth; }
            }
            var isContent = record[0] is 'x' or 'g' or '$' && height + depth > 0;
            var node = new RawNode(page, inputTag, sourceLine, column, x, y, width, height, depth, isBox, isContent);
            if (record[0] is '[' or '(') stack.Push((record[0], node));
            if (record[0] != '[' && sourceLine > 0) raw.Add(node);
            if (raw.Count > 1_000_000) break;
        }
        var scale = unit / 65781.76 * magnification / 1000 * postMagnification;
        var offsetX = postX ?? xOffset * unit / 65781.76;
        var offsetY = postY ?? yOffset * unit / 65781.76;
        return raw.Where(n => inputs.ContainsKey(n.Tag)).Select((n, order) =>
        {
            var width = Math.Abs(n.Width * scale);
            var height = Math.Abs((n.Height + n.Depth) * scale);
            var x = n.X * scale + offsetX + Math.Min(0, n.Width * scale);
            var y = (n.Y - n.Height) * scale + offsetY;
            return new SyncNode(inputs[n.Tag], n.Line, n.Column, new(n.Page, x, y, Math.Max(3, width), Math.Max(6, height)), n.IsBox, n.IsContent, order);
        }).Where(n => double.IsFinite(n.Box.X) && double.IsFinite(n.Box.Y) && double.IsFinite(n.Box.Width) && double.IsFinite(n.Box.Height)).ToList();
    }

    private static double? DimensionInPdfPoints(string text)
    {
        var match = Regex.Match(text.Trim(), @"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*(sp|pt|bp|in|cm|mm|pc|dd|cc)?$");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return value * (match.Groups[2].Value switch
        {
            "bp" => 1, "pt" => 72d / 72.27, "in" => 72, "cm" => 72 / 2.54, "mm" => 72 / 25.4,
            "pc" => 12 * 72d / 72.27, "dd" => 1238d / 1157 * 72 / 72.27, "cc" => 12 * 1238d / 1157 * 72 / 72.27,
            _ => 1 / 65781.76
        });
    }
}
