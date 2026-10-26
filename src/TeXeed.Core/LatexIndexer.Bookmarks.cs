namespace TeXeed.Core;

public static partial class LatexIndexer
{
    internal static IReadOnlyList<IndexEntry> OrderBookmarks(IReadOnlyList<ProjectFile> files, IEnumerable<BookmarkEvent> events)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var loaded = files.ToDictionary(file => Path.GetFullPath(file.Path), comparer);
        var streams = events.GroupBy(item => Path.GetFullPath(item.Path), comparer).ToDictionary(group => group.Key, group => group.ToArray(), comparer);
        var visited = new HashSet<string>(comparer); var ordered = new List<IndexEntry>();
        var root = files.Count == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(files[0].Path))!;
        void Visit(string path)
        {
            if (!visited.Add(path) || !streams.TryGetValue(path, out var stream)) return;
            foreach (var item in stream)
            {
                if (item.Entry is { } entry) { ordered.Add(entry); continue; }
                var input = item.Input?.Trim().Trim('"');
                if (string.IsNullOrEmpty(input) || input.IndexOfAny(['\\', '$', '#', '{', '}']) >= 0) continue;
                string? target = null;
                foreach (var directory in new[] { Path.GetDirectoryName(path)!, root })
                {
                    foreach (var extension in new[] { "", ".tex" })
                    {
                        try
                        {
                            var candidate = Path.GetFullPath(Path.Combine(directory, input + extension));
                            // Only traverse files already approved and loaded by ProjectService.
                            if (loaded.ContainsKey(candidate)) { target = candidate; break; }
                        }
                        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
                    }
                    if (target is not null) break;
                }
                if (target is not null) Visit(target);
            }
        }
        foreach (var file in files) Visit(Path.GetFullPath(file.Path));
        return ResolveBookmarkLevels(ordered);
    }
}
