using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace TeXeed.Core;

public sealed partial class SyncTexService
{
    private SyncTexService? _externalSync;
    private SyncTexService ExternalSync => _externalSync ??= new SyncTexService { _disableExternal = true };
    private sealed record IncludedGraphic(EpsSyncMap.Pair Pair, string Input, int Line, int EndLine, int Page);
    private sealed record ParsedCommand(LatexCommand Command, int First, int Last, bool Ambiguous);
    private sealed record ParsedGraphics(ParsedCommand[] Commands, string[] Paths, bool Transformed);
    private static readonly ConditionalWeakTable<string, ParsedGraphics> GraphicCommands = new();
    private static readonly ConcurrentDictionary<string, (long Ticks, long Size, string Content)> SourceContents = new(StringComparer.OrdinalIgnoreCase);

    private static ParsedGraphics ParseGraphics(string text)
    {
        var commands = LatexSyntax.Commands(LatexSyntax.Analyze(text).Code,
            new HashSet<string>(["includegraphics", "graphicspath", "rotatebox", "scalebox", "reflectbox", "resizebox", "adjustbox"], StringComparer.Ordinal)).ToArray();
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        int Line(int offset) { var index = starts.BinarySearch(offset); return (index < 0 ? ~index - 1 : index) + 1; }
        var graphics = commands.Where(c => c.Name == "includegraphics" && c.Arguments.Count > 0).Select(c => (Command: c, First: Line(c.Index), Last: Line(c.End))).ToArray();
        var counts = new Dictionary<int, int>();
        foreach (var item in graphics) for (var line = item.First; line <= item.Last; line++) counts[line] = counts.GetValueOrDefault(line) + 1;
        var parsed = graphics.Select(item => new ParsedCommand(item.Command, item.First, item.Last,
            Enumerable.Range(item.First, item.Last - item.First + 1).Any(line => counts[line] > 1))).ToArray();
        var paths = new List<string>();
        foreach (var command in commands.Where(c => c.Name == "graphicspath" && c.Arguments.Count > 0))
        {
            var index = 0;
            while (LatexSyntax.ReadGroup(command.Arguments[0], ref index, '{', '}') is { } path)
                if (path.IndexOfAny(['\\', '$', '#']) < 0) paths.Add(path);
        }
        return new(parsed, paths.ToArray(), commands.Any(c => c.Name is "rotatebox" or "scalebox" or "reflectbox" or "resizebox" or "adjustbox"));
    }

    private IEnumerable<IncludedGraphic> ExternalReferences(TexProject project)
    {
        if (!project.Files.Any(f => f.Kind == ProjectFileKind.Asset && (f.Path.EndsWith(".eps", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))) yield break;
        foreach (var input in project.Files.Where(f => f.Kind == ProjectFileKind.Tex))
        {
            var parsed = GraphicCommands.GetValue(input.Content, ParseGraphics);
            if (parsed.Transformed) continue;
            foreach (var item in parsed.Commands)
            {
                    var command = item.Command; var first = item.First; var last = item.Last;
                    if (item.Ambiguous) continue;
                    var options = command.OptionalArguments.SelectMany(o => o.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                {
                    var page = 1; var valid = true;
                    foreach (var option in options)
                    {
                        var split = option.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (split[0] == "page" && split.Length == 2 && int.TryParse(split[1], out var number) && number > 0) page = number;
                        else if (split[0] is "width" or "height" or "scale" && split.Length == 2 && !split[1].Contains('-')) { }
                        else if (split[0] == "keepaspectratio" && (split.Length == 1 || split[1] == "true")) { }
                        else { valid = false; break; }
                    }
                    var name = command.Arguments[0].Trim();
                    if (!valid || name.IndexOfAny(['\\', '$', '#']) >= 0) continue;
                    var dirs = new List<string> { Path.GetDirectoryName(input.Path)!, project.Root };
                    dirs.AddRange(parsed.Paths.SelectMany(path => new[] { Path.Combine(Path.GetDirectoryName(input.Path)!, path), Path.Combine(project.Root, path) }));
                    ProjectFile? asset = null;
                    foreach (var dir in dirs)
                    {
                        string candidate;
                        try { candidate = Path.GetFullPath(name, dir); }
                        catch (ArgumentException) { continue; }
                        asset = project.Files.FirstOrDefault(f => f.Kind == ProjectFileKind.Asset && (PathsEqual(f.Path, candidate) || !Path.HasExtension(candidate) &&
                            (PathsEqual(f.Path, candidate + ".pdf") || PathsEqual(f.Path, candidate + ".eps"))));
                        if (asset is not null) break;
                    }
                    if (asset is null) continue;
                    var pair = EpsSyncMap.Inspect(project.Root, asset.Path);
                    if (pair is null || !pair.IsPdf && page != 1) continue;
                    var source = project.Files.FirstOrDefault(f => f.IsEditable && PathsEqual(f.Path, pair.Source));
                    if (source is null) continue;
                    try
                    {
                        var info = new FileInfo(source.Path);
                        if (!SourceContents.TryGetValue(source.Path, out var cached) || cached.Ticks != info.LastWriteTimeUtc.Ticks || cached.Size != info.Length)
                        {
                            cached = (info.LastWriteTimeUtc.Ticks, info.Length, File.ReadAllText(source.Path));
                            if (SourceContents.Count > 128) SourceContents.Clear();
                            SourceContents[source.Path] = cached;
                        }
                        if (!string.Equals(cached.Content, source.Content, StringComparison.Ordinal)) continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    yield return new(pair, input.Path, first, last, page);
                }
            }
        }
    }

    private static PdfLocation? IncludedBox(TexProject project, SyncIndex index, IncludedGraphic reference)
    {
        foreach (var (input, file) in index.Files)
        {
            try { if (!PathsEqual(Path.GetFullPath(input.Trim('"'), CompileService.EntryDirectory(project)), reference.Input)) continue; }
            catch (ArgumentException) { continue; }
            var boxes = Enumerable.Range(reference.Line, reference.EndLine - reference.Line + 1)
                .Select(line => Array.BinarySearch(file.Lines, line)).Where(row => row >= 0)
                .SelectMany(row => file.Rows[row]).Where(n => n.IsBox && n.Box.Width > 6 && n.Box.Height > 6).Select(n => n.Box).Distinct().OrderBy(b => b.Width * b.Height).ToArray();
            if (boxes.Length > 0) return boxes[0];
        }
        return null;
    }

    private (double Left, double Top, double Width, double Height)? OriginalBounds(IncludedGraphic reference)
    {
        if (!reference.Pair.IsPdf) return (reference.Pair.Left, reference.Pair.Top, reference.Pair.Width, reference.Pair.Height);
        var size = PdfPageSize?.Invoke(reference.Pair.Output, reference.Page);
        return size is { Width: > 0, Height: > 0 } ? (0, 0, size.Value.Width, size.Value.Height) : null;
    }

    private async Task<PdfLocation?> ForwardExternalAsync(TexProject project, string pdfPath, SourceLocation source, AppSettings settings, CancellationToken ct)
    {
        var all = ExternalReferences(project).ToArray();
        var refs = all.Where(r => PathsEqual(r.Pair.Source, source.Path)).ToArray();
        if (refs.Length == 0) return null;
        var child = ExternalSync;
        var childProject = project with { MainPath = refs[0].Pair.Source };
        var sourceIndex = await child.GetIndexAsync(refs[0].Pair.Output, ct).ConfigureAwait(false);
        PdfLocation? position = null; PdfLocation? estimated = null;
        foreach (var (input, file) in sourceIndex.Files)
        {
            try { if (!PathsEqual(Path.GetFullPath(input.Trim('"'), CompileService.EntryDirectory(childProject)), source.Path)) continue; }
            catch (ArgumentException) { continue; }
            var row = Array.BinarySearch(file.Lines, source.Line);
            if (row >= 0) position = file.Rows[row].Where(n => n.IsContent).OrderBy(n => n.Box.Height).FirstOrDefault()?.Box;
            var boundary = row < 0 ? ~row : row;
            estimated = child.ApproximateExternalBlock(childProject, source, file, boundary);
        }
        position ??= estimated;
        if (position is null) return null;
        var matches = refs.Where(r => r.Page == position.Page).ToArray();
        if (matches.Length != 1) return null;
        var reference = matches[0]; var bounds = OriginalBounds(reference); if (bounds is null) return null;
        var b = bounds.Value;
        if (estimated is { } estimate && estimate.Page == reference.Page && estimate.X >= b.Left - 3 && estimate.X <= b.Left + b.Width + 3 &&
            estimate.Y >= b.Top - 3 && estimate.Y <= b.Top + b.Height + 3) position = estimate;
        if (position.X < b.Left - 3 || position.X > b.Left + b.Width + 3 || position.Y < b.Top - 3 || position.Y > b.Top + b.Height + 3) return null;
        var index = await GetIndexAsync(pdfPath, ct).ConfigureAwait(false); var box = IncludedBox(project, index, reference); if (box is null) return null;
        return new(box.Page, box.X + Math.Clamp((position.X - b.Left) / b.Width, 0, 1) * box.Width, box.Y + Math.Clamp((position.Y - b.Top) / b.Height, 0, 1) * box.Height,
            Math.Max(1, position.Width / b.Width * box.Width), Math.Max(1, position.Height / b.Height * box.Height));
    }

    private PdfLocation? ApproximateExternalBlock(TexProject project, SourceLocation source, FileIndex file, int boundary) =>
        ApproximateGraphic(project, source, file, boundary) ?? ApproximateBlock(project, source, file, boundary) ??
        (boundary < file.Lines.Length && file.Lines[boundary] != source.Line ? InterpolateEnvironment(project, source, file, boundary) : null);

    private async Task<SourceLocation?> InverseExternalAsync(TexProject project, SyncIndex index, PdfLocation point, AppSettings settings, CancellationToken ct)
    {
        var all = ExternalReferences(project).ToArray();
        var hits = all.Select(r => (Reference: r, Box: IncludedBox(project, index, r)))
            .Where(x => x.Box is { } b && b.Page == point.Page && point.X >= b.X && point.X <= b.X + b.Width && point.Y >= b.Y && point.Y <= b.Y + b.Height).ToArray();
        if (hits.Length != 1) return null;
        var (reference, box) = hits[0]; var bounds = OriginalBounds(reference); if (box is null || bounds is null) return null;
        var b = bounds.Value;
        var original = new PdfLocation(reference.Page, b.Left + (point.X - box.X) / box.Width * b.Width, b.Top + (point.Y - box.Y) / box.Height * b.Height, 0, 0);
        var child = ExternalSync;
        return await child.InverseAsync(project with { MainPath = reference.Pair.Source }, reference.Pair.Output, original, settings, ct);
    }
}
