namespace TeXeed.Core;

/// <summary>Per-session file indexes. Unchanged files and ordered snapshots are reused.</summary>
public sealed class ProjectIndexCache
{
    private readonly Dictionary<string, (ProjectFile File, ProjectIndex Index)> _files = new(StringComparer.OrdinalIgnoreCase);
    private string[] _order = [];
    private ProjectIndex? _snapshot;
    public int LastParsedFileCount { get; private set; }

    public ProjectIndex Update(IEnumerable<ProjectFile> files)
    {
        var editable = files.Where(file => file.IsEditable).ToArray();
        var order = editable.Select(file => file.Path).ToArray();
        var changed = _snapshot is null || !_order.SequenceEqual(order, StringComparer.OrdinalIgnoreCase);
        LastParsedFileCount = 0;
        foreach (var file in editable)
        {
            if (_files.TryGetValue(file.Path, out var cached) && cached.File.Path == file.Path
                && cached.File.RelativePath == file.RelativePath && cached.File.Kind == file.Kind && cached.File.Content == file.Content) continue;
            _files[file.Path] = (file, LatexIndexer.Build([file]));
            LastParsedFileCount++; changed = true;
        }
        if (!changed) return _snapshot!;
        var retained = order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _files.Keys.Where(path => !retained.Contains(path)).ToArray()) _files.Remove(path);
        var outline = new List<IndexEntry>(); var labels = new List<IndexEntry>();
        var citations = new List<IndexEntry>(); var todos = new List<IndexEntry>();
        foreach (var path in order)
        {
            var index = _files[path].Index;
            outline.AddRange(index.Outline); labels.AddRange(index.Labels);
            citations.AddRange(index.Citations); todos.AddRange(index.Todos);
        }
        _order = order;
        return _snapshot = new ProjectIndex(outline, labels, citations, todos);
    }
    public void Clear() { _files.Clear(); _order = []; _snapshot = null; LastParsedFileCount = 0; }
}
