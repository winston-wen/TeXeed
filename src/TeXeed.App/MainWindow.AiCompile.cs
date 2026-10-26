using System.IO;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    // The PDF can represent a one-off verification entry without changing the
    // user's open project/main file. SyncTeX must follow the actual PDF sources.
    private TexProject? _previewProject;
    private string? _lastCompileEntryPath;
    private int _lastCompileRevision = -1, _lastCompileBuildEpoch = -1;
    private bool _lastCompilePreviewPublished;
    private bool _lastCompileDependenciesCurrent;
    private CompileInputSnapshot? _activeCompileInputs, _previewInputs;

    private sealed class CompileInputSnapshot(TexProject project)
    {
        internal TexProject Project { get; } = project;
        private readonly HashSet<string> _paths = project.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long Length, long Modified)> _assets = project.Files.Where(f => !f.IsEditable)
            .ToDictionary(f => f.Path, f => Stamp(f.Path), StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        private bool _invalidated;
        internal int ChangeVersion { get; private set; }
        internal bool Contains(string path) => _paths.Contains(path);
        internal void Observe(string path) { _changed.Add(path); ChangeVersion++; }
        internal void Invalidate() { _invalidated = true; ChangeVersion++; }
        private static (long, long) Stamp(string path)
        {
            try { var info = new FileInfo(path); return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, -1); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, -1); }
        }
        internal async Task<bool> IsCurrentAsync(ProjectService service, CancellationToken token = default)
        {
            var version = ChangeVersion;
            // Unknown files can be dynamically included by TeX. A watcher event
            // outside the static graph therefore conservatively retires the PDF.
            if (_invalidated || _changed.Any(path => !_paths.Contains(path) || _assets.ContainsKey(path))) return false;
            if (_assets.Any(pair => Stamp(pair.Key) != pair.Value)) return false;
            return await Task.Run(() => service.MatchesSourcesAsync(Project, token), token) && version == ChangeVersion;
        }
    }

    // TeX can open arbitrary data files through macros (for example CSV tables or
    // \openin), so the static \input graph and source-extension list are not enough.
    // Exclude known compiler/cache output instead of assuming an unknown suffix
    // cannot affect a document. App compiler output lives entirely in .texeed.
    private static readonly HashSet<string> CompileOutputExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".aux", ".log", ".toc", ".out", ".bbl", ".blg", ".bcf", ".fls", ".fdb_latexmk", ".synctex", ".xdv", ".dvi", ".nav", ".snm", ".vrb", ".upa", ".upb", ".tmp", ".bak", ".swp", ".pyc" };
    private static readonly HashSet<string> CompileIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".texeed", ".git", ".svn", ".hg", ".vs", "node_modules", "__pycache__", "texmf-var", "texmf-cache" };

    private static bool IsWatchedCompileLocation(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !Path.IsPathRooted(relative) && parts[0] != ".." && !parts.Any(CompileIgnoredDirectories.Contains);
    }

    private static bool IsPotentialCompileInput(string root, string path)
    {
        if (!IsWatchedCompileLocation(root, path)) return false;
        var name = Path.GetFileName(path);
        return !CompileOutputExtensions.Contains(Path.GetExtension(path)) &&
            !name.EndsWith(".synctex.gz", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".run.xml", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith('~');
    }

    private void InvalidateCompileInputTracking()
    {
        // After a watcher overflow the missing events cannot be reconstructed from
        // the static graph. Retire both snapshots even when editable text matches.
        _activeCompileInputs?.Invalidate();
        if (!ReferenceEquals(_activeCompileInputs, _previewInputs)) _previewInputs?.Invalidate();
        _lastCompileDependenciesCurrent = false; _lastCompilePreviewPublished = false;
        _pdfRevision = -1; CancelForwardSync(); _syncTimer.Stop();
        _externalChangePending = true; _assetChangePending = true;
        QueueExternalRefresh();
    }

    private async Task<bool> ImportPreviewSourceAsync(SourceLocation source)
    {
        if (_project is null || _previewProject is null || _previewInputs is null || _pdfRevision != _revision) return false;
        var session = _session; var revision = _revision; var preview = _previewInputs;
        var file = _previewProject.Files.FirstOrDefault(f => f.IsEditable && string.Equals(f.Path, source.Path, StringComparison.OrdinalIgnoreCase));
        if (file is null || !string.Equals(_project.Root, _previewProject.Root, StringComparison.OrdinalIgnoreCase)) return false;
        await _saveGate.WaitAsync();
        try
        {
            if (!await preview.IsCurrentAsync(_projects) || session != _session || revision != _revision || _pdfRevision != _revision ||
                !ReferenceEquals(preview, _previewInputs) || _transitioning || _closing) return false;
            // Only a source already verified as part of the displayed PDF can be
            // imported. This user navigation does not change the default entry.
            var existing = CurrentFiles().FirstOrDefault(f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                _project = _project! with { Files = [.. _project!.Files, file] };
            else if (!existing.IsEditable)
                _project = _project! with { Files = _project!.Files.Select(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) ? file : f).ToArray() };
            return true;
        }
        finally { _saveGate.Release(); }
    }

    private async Task<TexProject> LoadCompileEntryAsync(TexProject current, string? entryFile, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(entryFile)) return current;
        var path = ValidateAiPath(current.Root, entryFile);
        if (!Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("验证入口必须是当前项目根目录内已有的 .tex 文件。");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到指定的编译入口，请先读取目录或创建包装文件。", entryFile);
        // Preserve the workspace boundary when the alternate entry is in a
        // subdirectory; its dependencies may legitimately reside at the root.
        return await Task.Run(() => _projects.LoadAsync(path, token, current.Root), token);
    }
}
