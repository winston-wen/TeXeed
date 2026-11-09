using System.Collections;
using System.IO;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private sealed class EntryPreview
    {
        internal string? Pdf;
        internal TexProject? Project;
        internal CompileInputSnapshot? Inputs;
        internal int Revision = -1;
        internal bool Warning;
        internal PdfReadingPosition? Position;
        internal string Log = "", Header = "编译输出", Status = "等待首次编译";
        internal bool LogExpanded;
        internal IEnumerable? Diagnostics;
    }
    private readonly Dictionary<string, EntryPreview> _entryPreviews = new(StringComparer.OrdinalIgnoreCase);
    private string? _activePreviewEntry;
    private bool _switchingEntryPreview;

    private string? PreviewEntryForKey(string key) => key == TabWorkspace.PreviewKey ? _project?.MainPath : TabWorkspace.PreviewEntry(key);
    private string PreviewKeyForEntry(string path) => string.Equals(path, _project?.MainPath, StringComparison.OrdinalIgnoreCase) ? TabWorkspace.PreviewKey : TabWorkspace.PreviewKeyFor(path);
    private EntryPreview EntryState(string path)
    {
        if (!_entryPreviews.TryGetValue(path, out var state)) _entryPreviews[path] = state = new();
        return state;
    }
    private void CaptureEntryPreview()
    {
        if (_activePreviewEntry is null) return;
        var state = EntryState(_activePreviewEntry);
        state.Pdf = _pdfPath; state.Project = _previewProject; state.Inputs = _previewInputs;
        state.Revision = _pdfRevision; state.Warning = _previewWarning;
        state.Position = _pendingPreviewPosition ?? Preview.CaptureReadingPosition();
        state.Log = LogText.Text; state.Header = LogExpander.Header?.ToString() ?? "编译输出";
        state.Status = PdfStatus.Text; state.Diagnostics = DiagnosticsList.ItemsSource; state.LogExpanded = LogExpander.IsExpanded;
    }
    private async Task<bool> SelectEntryPreviewAsync(string path)
    {
        if (_project is null || _closing || _transitioning || _switchingEntryPreview) return false;
        if (string.Equals(_activePreviewEntry, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (_building) { Status("正在编译，请等待完成或取消后切换预览。"); return false; }
        var session = _session;
        var previousEntry = _activePreviewEntry;
        _switchingEntryPreview = true;
        try
        {
            CaptureEntryPreview(); CancelPendingNavigation(); _syncPreparationCancellation?.Cancel(); _pdfGeneration++;
            var state = EntryState(path); _activePreviewEntry = path;
            _pdfPath = state.Pdf; _previewProject = state.Project; _previewInputs = state.Inputs;
            _pdfRevision = state.Revision; _previewWarning = state.Warning;
            _pendingPreviewPosition = state.Position; _followAwaitingSourceInteraction = state.Position is not null;
            LogText.Text = state.Log; LogExpander.Header = state.Header; DiagnosticsList.ItemsSource = state.Diagnostics; LogExpander.IsExpanded = state.LogExpanded;
            PdfStatus.Text = state.Status; Preview.Clear();
            if (state.Pdf is not null && File.Exists(state.Pdf))
            {
                await Preview.LoadAsync(state.Pdf);
                if (_closing || _transitioning || _session != session) return false;
                if (state.Inputs is not null)
                {
                    // Dynamic TeX inputs may not appear in the static source graph.
                    var unchanged = await state.Inputs.IsCurrentAsync(_projects) && _changes.Count == 0;
                    if (!unchanged) { _pdfRevision = -1; _previewWarning = true; }
                    else if (state.Revision >= 0 && !state.Warning) _pdfRevision = _revision;
                }
                if (_closing || _transitioning || _session != session) return false;
                if (_pdfRevision == _revision && !_previewWarning) PrepareSync(state.Pdf);
                RestorePendingPreviewPosition();
            }
            else { _pdfPath = null; _pdfRevision = -1; PdfStatus.Text = "等待首次编译"; }
            var label = Path.GetRelativePath(_project.Root, path);
            Status(_pdfPath is null ? $"当前预览：{label} · 尚未编译" : _pdfRevision != _revision || _previewWarning
                ? $"当前预览：{label} · 结果已过期，请重新编译" : $"当前预览：{label}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            if (_session == session && previousEntry is not null && !_closing && !_transitioning)
            {
                var previous = EntryState(previousEntry); _activePreviewEntry = previousEntry;
                _pdfPath = previous.Pdf; _previewProject = previous.Project; _previewInputs = previous.Inputs;
                _pdfRevision = previous.Revision; _previewWarning = previous.Warning; _pendingPreviewPosition = previous.Position;
                LogText.Text = previous.Log; LogExpander.Header = previous.Header; DiagnosticsList.ItemsSource = previous.Diagnostics; LogExpander.IsExpanded = previous.LogExpanded;
                Preview.Clear();
                try { if (_pdfPath is not null && File.Exists(_pdfPath)) { await Preview.LoadAsync(_pdfPath); if (_pdfRevision == _revision && !_previewWarning) PrepareSync(_pdfPath); RestorePendingPreviewPosition(); } }
                catch (Exception restoreError) when (restoreError is IOException or InvalidOperationException or ArgumentException) { _pdfPath = null; _pdfRevision = -1; }
            }
            Status("预览无法读取：" + ex.Message); return false;
        }
        finally { _switchingEntryPreview = false; }
    }
    private async Task OpenProjectEntryAsync(string path, bool buildIfMissing = true, bool preserveFocus = false, bool throwErrors = false)
    {
        if (_project is null || _building || _switchingEntryPreview || _transitioning || _closing)
        { Status("请等待当前编译或预览切换完成。"); return; }
        try
        {
            var session = _session;
            var root = _project.Root;
            var focusedKey = _workspaceTabs.FocusedTab;
            var focusedSide = _workspaceTabs.FocusedSide;
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Path.GetExtension(full).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择当前项目目录内的 .tex 文件。");
            var project = await LoadCompileEntryAsync(_project, relative, CancellationToken.None);
            if (_project is null || _session != session || !string.Equals(_project.Root, root, StringComparison.OrdinalIgnoreCase) || _closing || _transitioning) return;
            if (_building || _switchingEntryPreview) { Status("请等待当前编译或预览切换完成。"); return; }
            foreach (var file in project.Files.Where(f => f.IsEditable))
                if (!_project.Files.Any(existing => existing.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                    _project = _project with { Files = [.. _project.Files, file] };
            if (!full.Equals(_project.MainPath, StringComparison.OrdinalIgnoreCase)) _project = _project with { IsolateEntryOutputs = true };
            if (!preserveFocus) ActivateFile(full);
            var key = PreviewKeyForEntry(full);
            if (!await SelectEntryPreviewAsync(full)) { RefreshTabs(); return; }
            _workspaceTabs.Open(key);
            if (preserveFocus && focusedKey is not null && _workspaceTabs.Owner(focusedKey) == focusedSide) _workspaceTabs.Select(focusedSide, focusedKey);
            RefreshTabs();
            if (buildIfMissing && _pdfPath is null) await BuildAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { if (throwErrors) throw; Status("无法打开主文件：" + ex.Message); }
    }
}
