using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        internal bool PositionPending;
        internal string Log = "", Header = "编译输出", Status = "等待首次编译";
        internal bool LogExpanded;
        internal IEnumerable? Diagnostics;
        internal PdfPreview? View;
        internal Grid? Container;
        internal TextBlock? StatusText;
        internal string? LoadedPdf;
        internal (long Length, DateTime WriteTime)? LoadedStamp;
        internal Task? Loading;
    }
    private readonly Dictionary<string, EntryPreview> _entryPreviews = new(StringComparer.OrdinalIgnoreCase);
    private string? _activePreviewEntry;
    private bool _switchingEntryPreview;
    private Task<bool>? _entryPreviewSwitch;
    private string? _entryPreviewSwitchTarget;
    private string? _queuedBuildPreview;
    private TaskCompletionSource<bool>? _queuedBuildPreviewCompletion;
    private long _previewInputSequence;
    private PdfPreview? _currentPreview;
    private PdfPreview Preview => _currentPreview ?? InitialPreview;
    private IEnumerable<PdfPreview> ExistingPreviews => _entryPreviews.Values.Select(state => state.View).OfType<PdfPreview>().Append(InitialPreview).Distinct();
    private TextBlock PdfStatus => _activePreviewEntry is { } path && EntryState(path).StatusText is { } text ? text : InitialPdfStatus;

    private Grid PreviewContainerForKey(string key)
    {
        var path = PreviewEntryForKey(key) ?? throw new InvalidOperationException("预览没有对应的主文件。");
        var state = EntryState(path);
        if (state.Container is not null) return state.Container;
        state.PositionPending = state.Position is not null;
        if (_project is not null && path.Equals(_project.MainPath, StringComparison.OrdinalIgnoreCase))
        {
            state.View = InitialPreview; state.Container = PreviewContent; state.StatusText = InitialPdfStatus;
        }
        else
        {
            var view = new PdfPreview();
            var status = new TextBlock { Text = state.Status, Margin = new Thickness(12, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "右键菜单或 Ctrl+左键返回源码；预览设置在排版菜单中" };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var bar = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "SidebarBackground"); bar.SetResourceReference(Border.BorderBrushProperty, "Line");
            var grid = new Grid(); grid.SetResourceReference(Grid.BackgroundProperty, "PreviewBackground");
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            grid.Children.Add(view); Grid.SetRow(bar, 1); grid.Children.Add(bar);
            state.View = view; state.Container = grid; state.StatusText = status;
            ConfigurePreview(view, path, status); RegisterPdfAi(view);
        }
        return state.Container;
    }

    private void RefreshPreviewContainers(IEnumerable<string> visibleKeys)
    {
        var visible = visibleKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in _entryPreviews)
        {
            if (state.Container is null) continue;
            var show = visible.Contains(PreviewKeyForEntry(path));
            state.Container.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && state.View is not null && !ReferenceEquals(state.View, Preview) && state.Pdf is not null && state.LoadedPdf != state.Pdf && state.Loading is null)
                state.Loading = LoadVisibleEntryPreviewAsync(path, state);
        }
    }

    private async Task LoadVisibleEntryPreviewAsync(string path, EntryPreview state)
    {
        var view = state.View;
        try
        {
            if (view is null || state.Pdf is null || !File.Exists(state.Pdf)) return;
            var pdf = state.Pdf; var session = _session;
            await view.LoadAsync(pdf);
            if (_session != session || !_entryPreviews.TryGetValue(path, out var current) || !ReferenceEquals(current, state) || !ReferenceEquals(state.View, view)) return;
            state.LoadedPdf = pdf;
            state.LoadedStamp = PdfStamp(pdf);
            if (state.PositionPending && state.Position is { } position && view.IsVisible && view.RestoreReadingPosition(position)) state.PositionPending = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { if (state.StatusText is not null) state.StatusText.Text = "预览无法读取：" + ex.Message; }
        finally { if (ReferenceEquals(state.View, view)) state.Loading = null; }
    }

    private void ActivatePreviewInstance(EntryPreview state)
    {
        var view = state.View ?? InitialPreview;
        if (ReferenceEquals(_currentPreview, view)) return;
        if (Preview.IsAiSelecting) Preview.SetAiMarking(false);
        _currentPreview = view;
        UnregisterName("Preview"); RegisterName("Preview", view);
        _aiMarking = view.IsAiSelecting;
    }

    private void ConfigurePreview(PdfPreview view, string? path, TextBlock status)
    {
        view.InvertColors = _settings.InvertPdfColors;
        view.SetCropOptions(_settings.AutoCropPdf, _settings.CompressPdfWhitespace);
        view.AutoWidthMaximumCm = _settings.AutoWidthMaximumCm;
        string? Entry() => _entryPreviews.FirstOrDefault(pair => ReferenceEquals(pair.Value.View, view)).Key
            ?? (ReferenceEquals(view, InitialPreview) ? _project?.MainPath : path);
        void UserInput()
        {
            _previewInputSequence++;
            var entry = Entry();
            if (entry is null || _project is null || _transitioning || _closing) return;
            var state = EntryState(entry); state.Position = null; state.PositionPending = false;
            if (ReferenceEquals(view, Preview)) { _pendingPreviewPosition = null; return; }
            if (_workspaceTabs.Owner(PreviewKeyForEntry(entry)) is { } side) _workspaceTabs.Focus(side);
            _ = SelectEntryPreviewAsync(entry);
        }
        view.PreviewMouseDown += (_, _) => UserInput();
        view.PreviewMouseWheel += (_, _) => UserInput();
        view.ContextMenuOpening += async (_, e) =>
        {
            var entry = Entry();
            if (entry is null || ReferenceEquals(view, Preview) && !_switchingEntryPreview) return;
            e.Handled = true;
            var sequence = _previewInputSequence;
            if (await SelectEntryPreviewAsync(entry) && sequence == _previewInputSequence && view.IsVisible && ReferenceEquals(view, Preview) && view.ContextMenu is { } menu)
                menu.IsOpen = true;
        };
        view.ViewInteracted += (_, _) =>
        {
            if (Entry() is { } entry) { EntryState(entry).Position = null; EntryState(entry).PositionPending = false; }
            if (!ReferenceEquals(view, Preview)) return;
            _pendingPreviewPosition = null;
            LeftEditor.SuppressPendingViewportFollow(); RightEditor.SuppressPendingViewportFollow();
            CancelPendingNavigation();
        };
        view.UserScrollRequested += (_, _) => _previewScrollSequence = ++_userNavigationSequence;
        view.UserScrolled += (_, _) =>
        {
            if (!ReferenceEquals(view, Preview) || _previewScrollSequence != _userNavigationSequence) return;
            _previewUserOwnsView = true; CancelPendingNavigation();
        };
        view.IsVisibleChanged += (_, _) =>
        {
            if (!view.IsVisible) return;
            if (ReferenceEquals(view, Preview)) QueueRestorePendingPreviewPosition();
            else if (Entry() is { } entry)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    var state = EntryState(entry);
                    if (view.IsVisible && state.PositionPending && state.Position is { } position && state.LoadedPdf == state.Pdf && view.RestoreReadingPosition(position)) state.PositionPending = false;
                }));
        };
        view.InverseRequested += async (_, location) =>
        {
            var entry = Entry();
            if (entry is not null && !string.Equals(_activePreviewEntry, entry, StringComparison.OrdinalIgnoreCase) && !await SelectEntryPreviewAsync(entry)) return;
            await InverseAsync(location);
        };
        view.StatusChanged += (_, text) => { status.Text = text; if (Entry() is { } entry) EntryState(entry).Status = text; };
    }

    private void DisposeAdditionalPreviews()
    {
        foreach (var state in _entryPreviews.Values)
        {
            if (state.View is null || ReferenceEquals(state.View, InitialPreview)) continue;
            RemovePdfAi(state.View); state.View.Dispose();
            if (state.Container?.Parent is Panel panel) panel.Children.Remove(state.Container);
        }
        _currentPreview = InitialPreview;
        UnregisterName("Preview"); RegisterName("Preview", InitialPreview);
    }

    private void ReleaseClosedPreviewViews(IEnumerable<string> keys)
    {
        foreach (var key in keys.Where(TabWorkspace.IsPreview))
        {
            var path = PreviewEntryForKey(key);
            if (path is null || !_entryPreviews.TryGetValue(path, out var state) || state.View is null || ReferenceEquals(state.View, InitialPreview)) continue;
            state.Position = state.View.CaptureReadingPosition() ?? state.Position;
            state.PositionPending = state.Position is not null;
            if (ReferenceEquals(state.View, Preview)) continue;
            RemovePdfAi(state.View); state.View.Dispose();
            if (state.Container?.Parent is Panel panel) panel.Children.Remove(state.Container);
            state.View = null; state.Container = null; state.StatusText = null; state.LoadedPdf = null; state.LoadedStamp = null; state.Loading = null;
        }
    }

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
        state.PositionPending = _pendingPreviewPosition is not null;
        if (!string.Equals(state.LoadedPdf, _pdfPath, StringComparison.OrdinalIgnoreCase))
        { state.LoadedPdf = _pdfPath; state.LoadedStamp = PdfStamp(_pdfPath); }
        state.Log = LogText.Text; state.Header = LogExpander.Header?.ToString() ?? "编译输出";
        state.Status = PdfStatus.Text; state.Diagnostics = DiagnosticsList.ItemsSource; state.LogExpanded = LogExpander.IsExpanded;
    }
    private static (long Length, DateTime WriteTime)? PdfStamp(string? path)
    {
        try { if (path is not null && File.Exists(path)) { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc); } }
        catch (IOException) { }
        return null;
    }
    private async Task LoadActivePreviewAsync(string path)
    {
        await Preview.LoadAsync(path);
        if (_activePreviewEntry is { } entry)
        { var state = EntryState(entry); state.LoadedPdf = path; state.LoadedStamp = PdfStamp(path); }
    }
    private Task<bool> SelectEntryPreviewAsync(string path)
    {
        if (_building)
        {
            if (string.Equals(path, _activePreviewEntry, StringComparison.OrdinalIgnoreCase))
            { CancelQueuedBuildPreview(); return Task.FromResult(true); }
            if (string.Equals(path, _queuedBuildPreview, StringComparison.OrdinalIgnoreCase) && _queuedBuildPreviewCompletion is { } existing)
                return existing.Task;
            CancelQueuedBuildPreview();
            _queuedBuildPreview = path;
            _queuedBuildPreviewCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Status("正在编译；完成后切换到所选预览。");
            return _queuedBuildPreviewCompletion.Task;
        }
        if (_entryPreviewSwitch is { IsCompleted: false } pending)
            return string.Equals(path, _entryPreviewSwitchTarget, StringComparison.OrdinalIgnoreCase) ? pending : SelectAfterPendingAsync(pending, path);
        _entryPreviewSwitchTarget = path;
        return _entryPreviewSwitch = SwitchEntryPreviewAsync(path);
    }
    private void CancelQueuedBuildPreview()
    {
        var completion = _queuedBuildPreviewCompletion;
        _queuedBuildPreview = null; _queuedBuildPreviewCompletion = null;
        completion?.TrySetResult(false);
    }
    private async Task CompleteQueuedBuildPreviewAsync()
    {
        var path = _queuedBuildPreview; var completion = _queuedBuildPreviewCompletion;
        _queuedBuildPreview = null; _queuedBuildPreviewCompletion = null;
        if (path is null || completion is null) return;
        try
        {
            if (_closing || _transitioning || _project is null || _workspaceTabs.Owner(PreviewKeyForEntry(path)) is not { } owner || _workspaceTabs.FocusedSide != owner)
            { completion.TrySetResult(false); return; }
            completion.TrySetResult(await SelectEntryPreviewAsync(path));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private async Task<bool> SelectAfterPendingAsync(Task<bool> pending, string path)
    {
        await pending;
        return await SelectEntryPreviewAsync(path);
    }
    private async Task<bool> SwitchEntryPreviewAsync(string path)
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
            var state = EntryState(path); PreviewContainerForKey(PreviewKeyForEntry(path));
            if (state.Loading is not null) await state.Loading;
            if (_closing || _transitioning || _session != session) return false;
            if (state.Pdf is not null && File.Exists(state.Pdf) && (state.LoadedPdf != state.Pdf || state.LoadedStamp != PdfStamp(state.Pdf)))
            { await state.View!.LoadAsync(state.Pdf); state.LoadedPdf = state.Pdf; state.LoadedStamp = PdfStamp(state.Pdf); }
            if (_closing || _transitioning || _session != session) return false;
            _activePreviewEntry = path; ActivatePreviewInstance(state);
            _pdfPath = state.Pdf; _previewProject = state.Project; _previewInputs = state.Inputs;
            _pdfRevision = state.Revision; _previewWarning = state.Warning;
            _pendingPreviewPosition = state.PositionPending ? state.Position : null; _followAwaitingSourceInteraction = _pendingPreviewPosition is not null;
            LogText.Text = state.Log; LogExpander.Header = state.Header; DiagnosticsList.ItemsSource = state.Diagnostics; LogExpander.IsExpanded = state.LogExpanded;
            PdfStatus.Text = state.Status;
            if (state.Pdf is not null && File.Exists(state.Pdf))
            {
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
            else { _pdfPath = null; _pdfRevision = -1; Preview.Clear(); state.LoadedPdf = null; state.LoadedStamp = null; PdfStatus.Text = "等待首次编译"; }
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
                ActivatePreviewInstance(previous);
                _pdfPath = previous.Pdf; _previewProject = previous.Project; _previewInputs = previous.Inputs;
                _pdfRevision = previous.Revision; _previewWarning = previous.Warning; _pendingPreviewPosition = previous.PositionPending ? previous.Position : null;
                LogText.Text = previous.Log; LogExpander.Header = previous.Header; DiagnosticsList.ItemsSource = previous.Diagnostics; LogExpander.IsExpanded = previous.LogExpanded;
                try { if (_pdfPath is not null && File.Exists(_pdfPath)) { if (_pdfRevision == _revision && !_previewWarning) PrepareSync(_pdfPath); RestorePendingPreviewPosition(); } }
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
