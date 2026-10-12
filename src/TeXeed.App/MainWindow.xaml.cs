using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TeXeed.App.Services;
using TeXeed.Core;
using TeXeed.App.Controls;

namespace TeXeed.App;
public partial class MainWindow : Window
{
    private readonly ProjectService _projects = new();
    private readonly CompileService _compiler = new();
    private readonly SyncTexService _sync = new();
    private ProjectIndexCache _sidebarIndex = new();
    private ProjectIndex? _outlineIndex;
    private AppSettings _settings = SettingsStore.Load();
    private TexProject? _project;
    private readonly Dictionary<string, string> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tabs = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DispatcherTimer _liveTimer = new();
    private readonly DispatcherTimer _indexTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private FileSystemWatcher? _watcher;
    private Task<CompileResult>? _compileTask;
    private CancellationTokenSource? _forwardSyncCancellation;
    private CancellationTokenSource? _syncPreparationCancellation;
    private string _sidebar = "outline";
    private string? _pdfPath;
    private int _session, _revision, _pdfRevision = -1, _buildEpoch, _navigationRequest, _pdfGeneration, _inverseNavigationOwner;
    private int _activeBuildRevision = -1, _activeBuildEpoch;
    private long _liveBuildDueAt;
    private AppSettings? _activeBuildSettings;
    private bool _building, _buildAgain, _closing, _allowClose;
    private bool _buildAgainExplicit, _scheduledBuildExplicit;
    private bool _refreshing, _transitioning, _externalChangePending, _assetChangePending;

    public MainWindow(string? initialPath = null)
    {
        try { ThemeManager.Apply(_settings.ThemeId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { ThemeManager.UseDefault(); _settings = _settings with { ThemeId = ThemeManager.DefaultId }; }
        InitializeComponent();
        InitializeTitleBar();
        InitializeFullscreen();
        InitializeTabs();
        RefreshThemeMenu();
        UpdateWorkspaceIcons();
        WindowTheme.Attach(this);
        LiveMenuItem.IsChecked = _settings.LivePreview;
        Preview.InvertColors = _settings.InvertPdfColors;
        Preview.AutoCrop = _settings.AutoCropPdf;
        foreach (var sourceEditor in Editors) { sourceEditor.FontSize = _settings.FontSize; sourceEditor.FontFamily = new FontFamily(_settings.EditorFontFamily); }
        ApplyWordWrap();
        _liveTimer.Interval = TimeSpan.FromMilliseconds(_settings.DebounceMs);
        _liveTimer.Tick += async (_, _) =>
        {
            var explicitRequest = _scheduledBuildExplicit;
            _scheduledBuildExplicit = false; _liveTimer.Stop();
            await BuildAsync(explicitRequest);
        };
        _indexTimer.Tick += (_, _) => { _indexTimer.Stop(); RefreshIndex(); };
        _syncTimer.Tick += async (_, _) => { _syncTimer.Stop(); await ForwardAsync(false); };
        _watchTimer.Tick += async (_, _) => { _watchTimer.Stop(); await ReloadExternalAsync(); };
        foreach (var sourceEditor in Editors)
        {
        var owner = sourceEditor;
        owner.ContentEdited += (_, _) => { _activeEditor = owner; OnEdited(); };
        owner.CursorLocationChanged += (_, location) =>
        {
            if (_updatingWorkspace || !ReferenceEquals(owner, Editor)) return;
            OutlineTree.MarkCaret(location);
            BookmarkTree.MarkCaret(location);
            UpdateCursorStatus();
            if (_settings.FollowMode == PreviewFollowMode.Cursor) QueueFollowSync();
        };
        owner.ViewportLocationChanged += (_, _) =>
        {
            if (_updatingWorkspace || !ReferenceEquals(owner, Editor)) return;
            if (_settings.FollowMode == PreviewFollowMode.Viewport) QueueFollowSync();
        };
        owner.VisibleRangeChanged += (_, _) => { if (!_updatingWorkspace && ReferenceEquals(owner, Editor)) { OutlineTree.HighlightViewport(Editor.VisibleSourceRange); BookmarkTree.HighlightViewport(Editor.VisibleSourceRange); } };
        }
        Preview.ViewInteracted += (_, _) => CancelPendingNavigation();
        Preview.InverseRequested += async (_, location) => await InverseAsync(location);
        Preview.StatusChanged += (_, status) => PdfStatus.Text = status;
        UpdateRecent();
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialPath)) await OpenAsync(initialPath);
            else
            {
                var tools = await CompileService.DetectToolsAsync();
                if (_project != null || _transitioning || _closing) return;
                StatusText.Text = tools.Engines.Count > 0 ? $"就绪 · 检测到 {tools.Engines[0].Name}" : "就绪 · 首次排版前请在设置中配置 TeX Live 或 MiKTeX";
            }
        };
    }

    private IEnumerable<ProjectFile> CurrentFiles() => _project?.Files.Select(f => _changes.TryGetValue(f.Path, out var text) ? f with { Content = text } : f) ?? [];
    private void Status(string message) { StatusText.Text = message; StatusText.ToolTip = message; }
    private void Error(Exception error) { Status(error.Message); MessageBox.Show(this, error.Message, "TeXeed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void PersistSettings()
    {
        try { SettingsStore.Save(_settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status("设置暂未保存：" + ex.Message); }
    }
    private void UpdateRecent()
    {
        RecentMenu.Items.Clear(); RecentList.Children.Clear();
        foreach (var path in _settings.RecentProjects.Take(8))
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await OpenAsync(path); RecentMenu.Items.Add(item);
            if (RecentList.Children.Count >= 3) continue;
            var button = new Button { Content = Path.GetFileName(path), ToolTip = path, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3) };

            button.Click += async (_, _) => await OpenAsync(path); RecentList.Children.Add(button);
        }
        NoRecent.Visibility = RecentList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (_changes.Count == 0) return true;
        var answer = MessageBox.Show(this, "保存当前项目中的修改？", "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && await SaveAsync(true) && _changes.Count == 0;
    }
    private Task OpenAsync(string path) => SwitchProjectAsync(path, false);
    private async Task SwitchProjectAsync(string path, bool createNew)
    {
        if (_transitioning || _closing) return;
        _transitioning = true; foreach (var sourceEditor in Editors) sourceEditor.IsEnabled = false;
        try
        {
            await PauseWorkAsync();
            if (!await ConfirmLeaveAsync()) return;
            if (createNew) await File.WriteAllTextAsync(path, NewDocument);
            var project = await Task.Run(() => _projects.LoadAsync(path));
            var preparedIndex = new ProjectIndexCache();
            await Task.Run(() => preparedIndex.Update(project.Files));
            _session++; _revision = 0; _pdfRevision = -1; _pdfGeneration++; _navigationRequest++;
            _externalChangePending = false; _assetChangePending = false;
            _project = project; _changes.Clear(); _tabs.Clear(); _pdfPath = null;
            _sidebarIndex = preparedIndex; _outlineIndex = null;
            foreach (var sourceEditor in Editors) sourceEditor.ClearSession(); Preview.Clear();
            Workspace.Visibility = Visibility.Visible; Welcome.Visibility = Visibility.Collapsed;
            Title = project.Name + " — TeXeed";
            LogText.Text = ""; DiagnosticsList.ItemsSource = null; LogExpander.IsExpanded = false;
            PdfStatus.Text = "等待首次编译";
            var main = project.Files.First(f => string.Equals(f.Path, project.MainPath, StringComparison.OrdinalIgnoreCase));
            _workspaceTabs.Reset(main.Path); _activeEditor = LeftEditor;
            ActivateFile(main.Path); RefreshIndex(); SetupWatcher();
            _settings = _settings with { RecentProjects = new[] { project.MainPath }.Concat(_settings.RecentProjects.Where(p => !p.Equals(project.MainPath, StringComparison.OrdinalIgnoreCase))).Take(10).ToArray() };
            PersistSettings(); UpdateRecent();
            Status(project.Warnings.Count > 0 ? string.Join("；", project.Warnings) : $"已打开 {project.Name} · {project.Files.Count} 个文件");
            var cached = await _compiler.GetCachedPreviewAsync(project, _settings);
            if (cached is not null)
            {
                try
                {
                    if (cached.IsCurrent) PrepareSync(cached.PdfPath);
                    await Preview.LoadAsync(cached.PdfPath);
                    _pdfPath = cached.PdfPath;
                    _pdfRevision = cached.IsCurrent ? _revision : -1;
                    Status(cached.IsCurrent ? "已加载本地编译缓存 · 无需重新编译"
                        : _settings.LivePreview ? "已恢复上次预览 · 正在后台更新…" : "已恢复上次预览 · 可手动编译更新");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                { Preview.Clear(); Status("本地预览无法读取 · 等待编译更新"); }
            }
        }
        catch (Exception ex) { Error(ex); }
        finally
        {
            _transitioning = false; foreach (var sourceEditor in Editors) sourceEditor.IsEnabled = true;
            QueueExternalRefresh();
            if (_settings.LivePreview && _project != null && (_changes.Count > 0 || _pdfRevision != _revision)) ScheduleLiveBuild();
            else if (_pdfRevision == _revision && _pdfPath is not null) QueueFollowSync();
        }
    }
    private async Task PauseWorkAsync()
    {
        _indexTimer.Stop();
        _liveTimer.Stop(); _syncTimer.Stop(); _watchTimer.Stop();
        _scheduledBuildExplicit = false; _buildAgainExplicit = false;
        CancelForwardSync();
        _syncPreparationCancellation?.Cancel();
        _buildAgain = false; _buildEpoch++; _navigationRequest++; _compiler.Cancel();
        if (_compileTask is { } compilation) await compilation;
        // Finish any in-flight save before asking whether the remaining buffers should be discarded.
        await _saveGate.WaitAsync(); _saveGate.Release();
    }
    private void ActivateFile(string path)
    {
        var file = CurrentFiles().FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (file == null) return;
        if (!file.IsEditable) { Status("此文件是项目资源或不可编辑的编码：" + file.RelativePath); return; }
        _workspaceTabs.Open(path); _activeEditor = SourceEditor(_workspaceTabs.Owner(path)!.Value);
        RefreshTabs();
        QueueFollowSync();
    }
    private void UpdateCursorStatus()
    {
        foreach (var sourceEditor in Editors)
        {
            var format = Path.GetExtension(sourceEditor.CurrentFilePath ?? "").Equals(".bib", StringComparison.OrdinalIgnoreCase) ? "BibTeX" : "LaTeX";
            var location = sourceEditor.CurrentLocation;
            var status = ReferenceEquals(sourceEditor, LeftEditor) ? CursorStatus : RightCursorStatus;
            status.Text = location is null ? "未打开文件" : $"行 {location.Line}，列 {location.Column}  ·  {format}";
        }
    }
    private void RefreshTabs()
    {
        RefreshWorkspaceViews();
        Title = (_changes.Count > 0 ? "● " : "") + (_project?.Name ?? "") + " — TeXeed";
    }
    private void OnEdited()
    {
        if (_project == null || Editor.CurrentFilePath == null) return;
        _syncTimer.Stop(); CancelForwardSync();
        var file = _project.Files.FirstOrDefault(f => f.Path.Equals(Editor.CurrentFilePath, StringComparison.OrdinalIgnoreCase));
        if (file == null) return;
        if (Editor.Text == file.Content) _changes.Remove(file.Path); else _changes[file.Path] = Editor.Text;
        _revision++; RefreshTabs();
        _indexTimer.Stop(); _indexTimer.Start();
        if (_scheduledBuildExplicit) ScheduleBuildAt(Environment.TickCount64, true);
        else { _liveTimer.Stop(); if (_settings.LivePreview) ScheduleLiveBuild(); }
        if (_changes.Count == 0) QueueExternalRefresh();
        Status(_changes.Count == 0 ? "已保存" : _settings.LivePreview ? "已修改 · 等待自动保存并编译…" : "已修改 · Ctrl+S 保存并编译");
    }
    private async Task<bool> SaveAsync(bool duringTransition = false)
    {
        if (_project == null || (_transitioning || _closing) && !duringTransition) return false;
        var session = _session;
        await _saveGate.WaitAsync();
        try
        {
            if (_project == null || session != _session || (_transitioning || _closing) && !duringTransition) return false;
            var original = _project;
            var snapshot = new Dictionary<string, string>(_changes, StringComparer.OrdinalIgnoreCase);
            if (snapshot.Count > 0 && _compileTask is { IsCompleted: false } compilation)
            {
                // Do not change files underneath an engine still reading them.
                _buildEpoch++; _compiler.Cancel(); _buildAgain |= _settings.LivePreview;
                await compilation;
            }
            var updated = await _projects.SaveAsync(original, snapshot);
            if (session != _session) return false;
            // The user may type, or undo back to the old baseline, while the disk write is awaited.
            var liveEdits = CurrentFiles().Where(f => snapshot.ContainsKey(f.Path) || _changes.ContainsKey(f.Path))
                .ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase);
            var externalChanges = updated.Files.Any(f => !original.Files.Any(old => old.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase) &&
                (snapshot.TryGetValue(f.Path, out var saved) ? saved : old.Content) == f.Content));
            // A newly edited buffer keeps its original disk baseline, so an external edit
            // discovered during this save still produces a conflict on the next save.
            var baselines = updated.Files.Select(f =>
            {
                var old = original.Files.FirstOrDefault(previous => previous.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase));
                return old != null && liveEdits.ContainsKey(f.Path)
                    ? old with { Content = snapshot.TryGetValue(f.Path, out var saved) ? saved : old.Content }
                    : f;
            });
            var retained = original.Files.Where(f => !updated.Files.Any(next => next.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)) &&
                (_tabs.Contains(f.Path, StringComparer.OrdinalIgnoreCase) || liveEdits.ContainsKey(f.Path)))
                .Select(f => snapshot.TryGetValue(f.Path, out var saved) ? f with { Content = saved } : f);
            _project = updated with { Files = baselines.Concat(retained).ToArray() };
            _changes.Clear();
            foreach (var file in _project.Files)
            {
                if (liveEdits.TryGetValue(file.Path, out var text) && text != file.Content) _changes[file.Path] = text;
            }
            if (externalChanges) _revision++;
            if (Editor.CurrentFilePath is { } active) ActivateFile(active);
            RefreshTabs(); RefreshIndex(); Status(_changes.Count == 0 ? "所有修改已保存" : "保存完成 · 仍有更新的未保存修改"); return true;
        }
        catch (Exception ex) { _liveTimer.Stop(); _scheduledBuildExplicit = false; Error(ex); return false; }
        finally { _saveGate.Release(); QueueExternalRefresh(); }
    }
    private void ScheduleBuildAt(long dueAt, bool explicitRequest)
    {
        _liveTimer.Stop();
        _liveBuildDueAt = dueAt; _scheduledBuildExplicit = explicitRequest;
        _liveTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, dueAt - Environment.TickCount64));
        _liveTimer.Start();
    }

    private void ScheduleLiveBuild()
    {
        // An explicit save/build already waiting to run must not become another
        // automatic debounce when the user types or changes settings meanwhile.
        if (_scheduledBuildExplicit) return;
        ScheduleBuildAt(Environment.TickCount64 + _settings.DebounceMs, false);
    }

    private bool ActiveBuildNeedsReplacement => _revision != _activeBuildRevision || _buildEpoch != _activeBuildEpoch ||
        _activeBuildSettings is { } active && (active.Engine != _settings.Engine || active.EnginePath != _settings.EnginePath);

    private async Task BuildAsync(bool explicitRequest = true)
    {
        if (_project == null || _transitioning || _closing) return;
        if (!explicitRequest && !_settings.LivePreview) return;
        if (_building)
        {
            if (ActiveBuildNeedsReplacement) { _buildAgain = true; _buildAgainExplicit |= explicitRequest; }
            return;
        }
        _building = true; _buildAgain = false; var session = _session; var epoch = _buildEpoch;
        _buildAgainExplicit = false; _scheduledBuildExplicit = false;
        _activeBuildRevision = _revision; _activeBuildEpoch = epoch; _activeBuildSettings = _settings;
        _liveTimer.Stop(); CancelBuildMenuItem.IsEnabled = true;
        try
        {
            if (!await SaveAsync() || session != _session || epoch != _buildEpoch || _project == null || _transitioning || _closing) return;
            if (_changes.Count > 0) { _buildAgain = true; _buildAgainExplicit |= explicitRequest; return; }
            var revision = _revision; var project = _project;
            _activeBuildRevision = revision; _activeBuildSettings = _settings;
            if (!explicitRequest)
            {
                var cached = await _compiler.GetCachedPreviewAsync(project, _settings);
                if (revision != _revision || _changes.Count > 0) { _buildAgain = true; return; }
                if (cached is { IsCurrent: true })
                {
                    if (_pdfPath != cached.PdfPath) { _pdfGeneration++; await Preview.LoadAsync(cached.PdfPath); }
                    _pdfPath = cached.PdfPath; _pdfRevision = revision;
                    Status("已复用本地编译缓存"); QueueFollowSync();
                    return;
                }
            }
            Status("正在排版，仍可继续编辑…");
            _compileTask = _compiler.CompileAsync(project, _settings);
            var result = await _compileTask;
            if (session != _session || epoch != _buildEpoch || _transitioning || _closing) return;
            LogText.Text = result.Log; DiagnosticsList.ItemsSource = result.Diagnostics;
            LogExpander.Header = result.Success ? $"编译输出 · {result.Duration.TotalSeconds:F1}s · {result.Engine}" : "编译输出 · 需要处理的问题";
            if (result.Success && result.PdfPath != null)
            {
                _pdfGeneration++; _pdfRevision = -1;
                PrepareSync(result.PdfPath);
                await Preview.LoadAsync(result.PdfPath);
                if (session != _session || epoch != _buildEpoch || _transitioning || _closing) return;
                _pdfPath = result.PdfPath; _pdfRevision = revision;
                Status($"编译成功 · {result.Engine} · {result.Duration.TotalSeconds:F1}s" + (!result.SynctexAvailable ? " · 未生成 SyncTeX 定位信息" : ""));
                if (revision == _revision) QueueFollowSync();
            }
            else
            {
                LogExpander.IsExpanded = true;
                Status("编译未完成 · 已保留上次预览，请查看编译输出");
            }
            if (_settings.LivePreview && revision != _revision) _buildAgain = true;
        }
        catch (OperationCanceledException) { if (session == _session && epoch == _buildEpoch && !_transitioning && !_closing) Status("编译已取消"); }
        catch (Exception ex) { if (session == _session && epoch == _buildEpoch && !_transitioning && !_closing) Error(ex); }
        finally
        {
            var again = _buildAgain && ActiveBuildNeedsReplacement;
            var explicitAgain = _buildAgainExplicit;
            _buildAgain = false; _buildAgainExplicit = false; _activeBuildRevision = -1; _activeBuildSettings = null;
            _compileTask = null; _building = false; CancelBuildMenuItem.IsEnabled = false;
            if (again && !_closing && !_transitioning && (explicitAgain || _settings.LivePreview))
                ScheduleBuildAt(explicitAgain ? Environment.TickCount64 : _liveBuildDueAt, explicitAgain);
            QueueExternalRefresh();
        }
    }
    private void CancelForwardSync() => _forwardSyncCancellation?.Cancel();
    private void PrepareSync(string pdfPath)
    {
        _syncPreparationCancellation?.Cancel();
        _syncPreparationCancellation?.Dispose();
        _syncPreparationCancellation = new CancellationTokenSource();
        _ = _sync.PrepareAsync(pdfPath, _syncPreparationCancellation.Token);
    }

    private void CancelPendingNavigation()
    {
        _navigationRequest++; _syncTimer.Stop(); CancelForwardSync();
    }

    private SourceLocation? AutomaticSyncLocation => !Editor.IsVisible ? null : _settings.FollowMode switch
    {
        PreviewFollowMode.Cursor => Editor.CurrentLocation,
        PreviewFollowMode.Viewport => Editor.ViewportFollowLocation,
        _ => null
    };

    private void QueueFollowSync()
    {
        if (_inverseNavigationOwner != 0) return;
        _navigationRequest++;
        CancelForwardSync();
        if (_settings.FollowMode == PreviewFollowMode.None || _transitioning || _closing) { _syncTimer.Stop(); return; }
        // Throttle updates without waiting for a long scroll/key gesture to finish.
        if (!_syncTimer.IsEnabled) _syncTimer.Start();
    }

    private async Task ForwardAsync(bool explicitRequest)
    {
        if (_transitioning || _closing || _building || _externalChangePending) return;
        if (!explicitRequest && (_settings.FollowMode == PreviewFollowMode.None || _inverseNavigationOwner != 0)) return;
        _syncTimer.Stop();
        var mode = _settings.FollowMode;
        var location = explicitRequest ? Editor.CurrentLocation : AutomaticSyncLocation;
        if (_project == null || _pdfPath == null || location == null) { if (explicitRequest) Status("请先成功编译文档，再定位 PDF。"); return; }
        if (_pdfRevision != _revision) { if (explicitRequest) Status("源码已修改，请编译后再定位 PDF。"); return; }
        var session = _session; var revision = _revision;
        var generation = _pdfGeneration; var request = ++_navigationRequest;
        CancelForwardSync();
        using var cancellation = new CancellationTokenSource();
        _forwardSyncCancellation = cancellation;
        try
        {
            var point = !explicitRequest && mode == PreviewFollowMode.Viewport
                ? await _sync.ForwardViewportAsync(_project, _pdfPath, location, _settings, cancellation.Token)
                : await _sync.ForwardAsync(_project, _pdfPath, location, _settings, cancellation.Token);
            if (session != _session || request != _navigationRequest || revision != _revision || generation != _pdfGeneration ||
                _pdfRevision != revision || _building || _transitioning || _closing || _externalChangePending ||
                (explicitRequest ? Editor.CurrentLocation : AutomaticSyncLocation) != location ||
                !explicitRequest && (_settings.FollowMode != mode || _inverseNavigationOwner != 0)) return;
            if (point != null) Preview.ShowLocation(point, highlight: explicitRequest,
                viewportFraction: explicitRequest && mode != PreviewFollowMode.Cursor ? .35 : .5,
                centerBounds: mode == PreviewFollowMode.Cursor,
                pageStart: !explicitRequest && mode == PreviewFollowMode.Viewport && Editor.VerticalOffset <= .01);
            else if (explicitRequest) Status("此处没有对应的 PDF 位置；请检查 SyncTeX 设置。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (explicitRequest && session == _session && request == _navigationRequest && !_closing && !_transitioning) Status("定位失败：" + ex.Message); }
        finally { if (ReferenceEquals(_forwardSyncCancellation, cancellation)) _forwardSyncCancellation = null; }
    }
    private async Task InverseAsync(PdfLocation point)
    {
        if (_project == null || _pdfPath == null || _transitioning || _closing || _building || _externalChangePending) return;
        if (_pdfRevision != _revision) { Status("源码已修改，请编译后再进行反向定位。"); return; }
        _syncTimer.Stop(); CancelForwardSync();
        var session = _session; var revision = _revision; var generation = _pdfGeneration; var request = ++_navigationRequest;
        var originalLocation = Editor.CurrentLocation;
        var originalViewport = Editor.ViewportLocation;
        try
        {
            var source = await _sync.InverseAsync(_project, _pdfPath, point, _settings);
            if (session != _session || request != _navigationRequest || revision != _revision || generation != _pdfGeneration ||
                _pdfRevision != revision || _building || _transitioning || _closing || _externalChangePending ||
                Editor.CurrentLocation != originalLocation || Editor.ViewportLocation != originalViewport) return;
            if (source == null) { Status("未找到对应源码，请检查 SyncTeX 设置。"); return; }
            if (!CurrentFiles().Any(f => f.IsEditable && f.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
            { Status("定位结果不属于当前已载入的项目文件。"); return; }
            _inverseNavigationOwner = request;
            ActivateFile(source.Path); Editor.NavigateTo(source); _syncTimer.Stop();
            // AvalonEdit restores scroll positions during layout; suppress those deferred
            // viewport events too, otherwise inverse navigation would pull the PDF back.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Status($"已定位到 {Path.GetFileName(source.Path)}:{source.Line}");
        }
        catch (Exception ex) { if (session == _session && request == _navigationRequest && !_closing && !_transitioning) Status("定位失败：" + ex.Message); }
        finally { if (_inverseNavigationOwner == request) _inverseNavigationOwner = 0; }
    }
    private void RefreshIndex()
    {
        if (_project == null) return;
        var files = CurrentFiles().ToArray(); var index = _sidebarIndex.Update(files);
        foreach (var sourceEditor in Editors) sourceEditor.SetProjectFiles(files);
        SidebarTitle.ToolTip = $"{files.Length} 个文件 · {index.Labels.Count} 个标签\n{index.Citations.Count} 条参考文献";
        if (_project.Warnings.Count > 0) SidebarTitle.ToolTip += "\n" + string.Join(Environment.NewLine, _project.Warnings);
        FileTree.Visibility = _sidebar == "files" ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = _sidebar == "outline" ? Visibility.Visible : Visibility.Collapsed;
        BookmarkTree.Visibility = _sidebar == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        BookmarkSearchPanel.Visibility = _sidebar == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        RefreshBookmarks(index);
        if (!ReferenceEquals(_outlineIndex, index))
        {
            OutlineTree.Update(index.Outline, _project.MainPath, index.Todos);
            _outlineIndex = index;
        }
        OutlineTree.MarkCaret(Editor.CurrentLocation);
        OutlineTree.HighlightViewport(Editor.VisibleSourceRange);
        IndexList.Visibility = _sidebar is "files" or "outline" or "bookmarks" ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = _sidebar is "search" or "labels" ? Visibility.Visible : Visibility.Collapsed;
        FileSearchPanel.Visibility = _sidebar == "files" ? Visibility.Visible : Visibility.Collapsed;
        SidebarTreeActions.Visibility = _sidebar is "outline" or "files" or "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        SidebarTitle.Text = _sidebar switch { "files" => "项目文件", "search" => "全文搜索", "bookmarks" => "书签", "labels" => "标签与引用", "todos" => "待办事项", _ => "文档大纲" };
        if (_sidebar == "files") { BuildFileTree(files); return; }
        if (_sidebar == "bookmarks") { UpdateBookmarkEmptyState(); return; }
        if (_sidebar == "outline")
        {
            EmptyIndex.Text = "添加章节标题后，大纲会显示在这里";
            EmptyIndex.Visibility = index.Outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var entries = _sidebar switch
        {
            "search" => string.IsNullOrWhiteSpace(SearchBox.Text) ? [] : LatexIndexer.Search(files, SearchBox.Text),
            "labels" => index.Labels.Where(e => e.Title.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)).ToArray(),
            "todos" => index.Todos,
            _ => Array.Empty<IndexEntry>()
        };
        IndexList.ItemsSource = entries;
        EmptyIndex.Text = _sidebar == "search" && string.IsNullOrWhiteSpace(SearchBox.Text) ? "输入文字，在整个项目中查找" : "暂无条目";
        EmptyIndex.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetupWatcher()
    {
        _watcher?.Dispose(); if (_project == null) return;
        var session = _session;
        var watcher = new FileSystemWatcher(_project.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher = watcher;
        void Changed(string path)
        {
            if (path.Contains(Path.DirectorySeparatorChar + ".texeed" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Dispatcher.HasShutdownStarted) return;
            var extension = Path.GetExtension(path);
            var source = new[] { ".tex", ".bib", ".sty", ".cls", ".bst" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
            var asset = new[] { ".pdf", ".png", ".jpg", ".jpeg", ".eps", ".svg", ".tif", ".tiff", ".bmp" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
            if (!source && !asset) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (session != _session || !ReferenceEquals(watcher, _watcher) || _closing) return;
                _externalChangePending = true; _assetChangePending |= asset;
                QueueExternalRefresh();
            });
        }
        watcher.Changed += (_, e) => Changed(e.FullPath); watcher.Created += (_, e) => Changed(e.FullPath); watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
        watcher.Error += (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
            {
                if (session != _session || !ReferenceEquals(watcher, _watcher) || _closing) return;
                _externalChangePending = true; _assetChangePending = true; QueueExternalRefresh();
            });
        };
        watcher.EnableRaisingEvents = true;
    }
    private void QueueExternalRefresh()
    {
        if (!_externalChangePending || _transitioning || _closing || _building || _refreshing || _saveGate.CurrentCount == 0) return;
        _watchTimer.Stop(); _watchTimer.Start();
    }
    private async Task ReloadExternalAsync()
    {
        if (_project == null || !_externalChangePending || _refreshing || _building || _transitioning || _closing || _saveGate.CurrentCount == 0) return;
        if (_changes.Count != 0) { Status("检测到外部修改；保存时会检查冲突。可通过“重新加载项目”读取磁盘版本。"); return; }
        _refreshing = true; var session = _session;
        await _saveGate.WaitAsync();
        try
        {
            if (session != _session || _project == null || _transitioning || _closing || _changes.Count > 0) return;
            var assetChanged = _assetChangePending;
            _externalChangePending = false; _assetChangePending = false;
            var updated = await _projects.LoadAsync(_project.MainPath);
            if (session != _session || _transitioning || _closing || _changes.Count > 0)
            { if (session == _session) { _externalChangePending = true; _assetChangePending |= assetChanged; } return; }
            // Keep detached tabs in this editing session. Their saved baseline remains
            // available for conflict detection if the now-unreferenced file changes on disk.
            var detached = _project.Files.Where(f => _tabs.Contains(f.Path, StringComparer.OrdinalIgnoreCase) &&
                !updated.Files.Any(next => next.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)));
            updated = updated with { Files = updated.Files.Concat(detached).ToArray() };
            var changed = assetChanged || updated.Files.Count != _project.Files.Count || updated.Files.Any(f => !_project.Files.Any(old => old.Path == f.Path && old.Content == f.Content));
            _project = updated;
            if (!changed)
            {
                return;
            }
            _revision++;
            foreach (var path in _workspaceTabs.Sources.Where(p => !updated.Files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToArray())
                CloseTabs(path, TabCloseScope.This);
            if (_tabs.Count > 0) ActivateFile(updated.Files.Any(f => f.Path == Editor.CurrentFilePath) ? Editor.CurrentFilePath! : _tabs[0]);
            RefreshIndex(); Status("已同步外部文件修改");
            if (_settings.LivePreview) ScheduleLiveBuild();
        }
        catch (Exception ex) { if (session == _session && !_transitioning && !_closing) Status("重新读取失败：" + ex.Message); }
        finally { _refreshing = false; _saveGate.Release(); if (_changes.Count == 0) QueueExternalRefresh(); }
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_transitioning || _closing) return;
        var dialog = new OpenFileDialog { Title = "打开主 LaTeX 文件", Filter = "LaTeX 文档 (*.tex)|*.tex", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_transitioning || _closing) return;
        var dialog = new SaveFileDialog { Title = "新建 LaTeX 文档", Filter = "LaTeX 文档 (*.tex)|*.tex", FileName = "main.tex", DefaultExt = ".tex" };
        if (dialog.ShowDialog(this) != true) return;
        await SwitchProjectAsync(dialog.FileName, true);
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await BuildAsync();
    private async void Build_Click(object sender, RoutedEventArgs e) => await BuildAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) { _buildEpoch++; _buildAgain = false; _buildAgainExplicit = false; _scheduledBuildExplicit = false; _liveTimer.Stop(); _compiler.Cancel(); Status("编译已取消"); }
    private async void Sync_Click(object sender, RoutedEventArgs e) => await ForwardAsync(true);
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (_project != null) await OpenAsync(_project.MainPath); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Live_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { LivePreview = LiveMenuItem.IsChecked }; PersistSettings(); _liveTimer.Stop();
        if (!_settings.LivePreview) _buildAgain = _buildAgainExplicit;
        if (_scheduledBuildExplicit) ScheduleBuildAt(Environment.TickCount64, true);
        else if (_settings.LivePreview && _project != null) ScheduleLiveBuild();
    }
    private void SidebarMode_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _allowClose) return;
        var mode = (string)((Button)sender).Tag;
        if (_workspaceExpanded && _sidebar == mode) { ToggleWorkspace(); ((Button)sender).Focus(); return; }
        if (!_workspaceExpanded) ToggleWorkspace();
        if (_sidebar != mode) { _sidebar = mode; RefreshIndex(); }
        UpdateWorkspaceIcons();
        if (_sidebar == "search") SearchBox.Focus();
        else WorkspaceIconBar.Children.OfType<Button>().First(button => Equals(button.Tag, mode)).Focus();
    }

    private void UpdateWorkspaceIcons()
    {
        foreach (var button in WorkspaceIconBar.Children.OfType<Button>())
            System.Windows.Controls.Primitives.Selector.SetIsSelected(button, _workspaceExpanded && Equals(button.Tag, _sidebar));
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && _project != null) RefreshIndex(); }
    private void FileTree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is TreeViewItem { Tag: string path }) ActivateFile(path); }
    private void Outline_NavigateRequested(object? sender, SourceLocation location)
    {
        ActivateFile(location.Path); Editor.NavigateTo(location, center: true);
        // A repeated click can leave the caret/viewport unchanged. It must still
        // resynchronize after the user has manually moved the PDF preview.
        QueueFollowSync();
    }
    private void Index_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (IndexList.SelectedItem is not IndexEntry item) return;
        ActivateFile(item.Path); Editor.NavigateTo(new SourceLocation(item.Path, item.Line)); IndexList.SelectedItem = null;
    }
    private void Diagnostic_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DiagnosticsList.SelectedItem is not Diagnostic item) return;
        ActivateFile(item.Path); Editor.NavigateTo(new SourceLocation(item.Path, Math.Max(1, item.Line))); DiagnosticsList.SelectedItem = null;
    }
    private void EditorCommand_Click(object sender, RoutedEventArgs e) { if (!_transitioning && !_closing) Editor.ExecuteCommand((string)((MenuItem)sender).Tag); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetManualZoom(Preview.Zoom / 1.15);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetManualZoom(Preview.Zoom * 1.15);
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfPath == null || !File.Exists(_pdfPath)) { Status("请先编译成功，再导出 PDF。"); return; }
        var dialog = new SaveFileDialog { Title = "导出 PDF", Filter = "PDF 文档 (*.pdf)|*.pdf", FileName = _project?.Name + ".pdf", InitialDirectory = _project?.Root };
        if (dialog.ShowDialog(this) != true) return;
        try { if (!Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(_pdfPath), StringComparison.OrdinalIgnoreCase)) File.Copy(_pdfPath, dialog.FileName, true); Status("PDF 已导出：" + dialog.FileName); }
        catch (Exception ex) { Error(ex); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _project is null ? null : _project with { Files = CurrentFiles().ToArray() }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_project is not null) { _project = _project with { ConfiguredName = dialog.ProjectName }; RefreshTabs(); }
        _settings = dialog.Settings; PersistSettings(); LiveMenuItem.IsChecked = _settings.LivePreview;
        foreach (var sourceEditor in Editors) { sourceEditor.FontSize = _settings.FontSize; sourceEditor.FontFamily = new FontFamily(_settings.EditorFontFamily); }
        Preview.InvertColors = _settings.InvertPdfColors;
        Preview.AutoCrop = _settings.AutoCropPdf;
        CancelPendingNavigation();
        QueueFollowSync();
        _liveTimer.Stop(); _liveTimer.Interval = TimeSpan.FromMilliseconds(_settings.DebounceMs);
        if (!_settings.LivePreview) _buildAgain = _buildAgainExplicit;
        if (_scheduledBuildExplicit) ScheduleBuildAt(Environment.TickCount64, true);
        else if (_settings.LivePreview && _project != null) ScheduleLiveBuild();
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "打开主 .tex 文件会自动加载项目依赖。\n\nAlt　激活菜单（菜单栏常驻）\nF11　进入 / 退出全屏\nCtrl+B　折叠 / 展开工作区\nCtrl+S　保存全部文件并编译\nCtrl+Enter　编译文档\nCtrl+Space　自动补全\nCtrl+/　切换注释\nTab / Shift+Tab　缩进 / 取消缩进\nCtrl+Shift+F　格式化\nCtrl+F　文件内查找\nCtrl+H　文件内替换\nAlt+左键单击　添加光标\nAlt+左键拖动 / 中键拖动　列框选\nEsc　返回单光标\nCtrl+J　按光标定位 PDF\nPDF 右键双击或 Ctrl+左键　返回源码\nPDF Ctrl+滚轮　以鼠标下的内容为中心缩放\n\n编辑器字体可在设置中选择，固定按英文一格、中文两格排版。\nPDF 预览栏可选择不跟随、跟随光标或跟随视图，选择会自动保存；跟随光标时，对应内容始终垂直居中。\n实时预览开启时，修改会在短暂延迟后自动保存并编译。\n编译需要安装 TeX Live 或 MiKTeX，也支持配置 Tectonic。\n中文文档建议使用 ctex 宏包及 XeLaTeX。\n动态生成的文件路径、外部目录引用需要手动整理到主文件目录内。", "TeXeed 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, $"TeXeed {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n原生 Windows LaTeX 工作区\n\nC# · .NET 10 · WPF\nAvalonEdit / WpfMath (MIT) · PDFiumCore (Apache-2.0) · PDFium\n\n第三方许可随发行包提供。", "关于 TeXeed", MessageBoxButton.OK, MessageBoxImage.Information);
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (FileTabs.IsDraggingTab || RightFileTabs.IsDraggingTab))
        { FileTabs.CancelDrag(); RightFileTabs.CancelDrag(); DockDropCue.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (HandleMenuKeyDown(e)) return;
        await HandleShortcutAsync(e, Keyboard.Modifiers);
    }
    private async Task HandleShortcutAsync(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.F4 && modifiers == ModifierKeys.Control)
        { e.Handled = true; if (!e.IsRepeat && _workspaceTabs.FocusedTab is { } tab && tab != TabWorkspace.PreviewKey) CloseTabs(tab, TabCloseScope.This); return; }
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Z && modifiers == ModifierKeys.Alt)
        { e.Handled = true; if (!e.IsRepeat) ToggleWordWrap(); return; }
        if (e.Key == Key.F11 && modifiers == ModifierKeys.None)
        { e.Handled = true; if (!e.IsRepeat) ToggleFullscreen(); return; }
        if (modifiers != ModifierKeys.Control) return;
        if (e.Key is not (Key.O or Key.N or Key.S or Key.Enter or Key.J or Key.B or Key.H)) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        switch (e.Key)
        {
            case Key.O: Open_Click(this, e); break;
            case Key.N: New_Click(this, e); break;
            case Key.S: await BuildAsync(); break;
            case Key.Enter: await BuildAsync(); break;
            case Key.J: await ForwardAsync(true); break;
            case Key.B: ToggleWorkspace(); break;
            case Key.H: if (!_transitioning && !_closing) Editor.ExecuteCommand("replace"); break;
        }
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(f => f.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) is { } path) { e.Handled = true; await OpenAsync(path); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; if (_closing || _transitioning) return; _closing = true; foreach (var sourceEditor in Editors) sourceEditor.IsEnabled = false;
        try
        {
            // Even idle/save-free shutdown must leave WPF's Closing callback before
            // invoking Close again; awaited cleanup may otherwise complete inline.
            await Dispatcher.Yield(DispatcherPriority.Background);
            await PauseWorkAsync();
            if (!await ConfirmLeaveAsync()) return;
            _allowClose = true; _session++; _compiler.Cancel(); _watcher?.Dispose();
            _liveTimer.Stop(); _indexTimer.Stop(); _syncTimer.Stop(); _watchTimer.Stop(); Preview.Dispose(); Close();
        }
        catch (Exception ex) { Error(ex); }
        finally
        {
            if (!_allowClose)
            {
                _closing = false; foreach (var sourceEditor in Editors) sourceEditor.IsEnabled = true; QueueExternalRefresh();
                if (_settings.LivePreview && (_changes.Count > 0 || _pdfRevision != _revision)) ScheduleLiveBuild();
            }
        }
    }
    private const string NewDocument = """
        \documentclass[11pt]{article}
        \usepackage[a4paper,margin=28mm]{geometry}
        \usepackage{amsmath}
        \title{A New Beginning}
        \author{}
        \date{\today}

        \begin{document}
        \maketitle

        \section{Introduction}
        \label{sec:introduction}
        Every idea deserves a little space.

        \begin{equation}
          E = mc^2
          \label{eq:energy}
        \end{equation}

        See Equation~\eqref{eq:energy}.
        % TODO: Add your next idea here.

        \end{document}
        """;
}
