using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _fileTreeExpanded = new(StringComparer.OrdinalIgnoreCase);
    private string? _fileTreeProject, _fileTreeQuery;
    private bool _fileTreeFiltering, _buildingFileTree;
    private DirectoryEntry[]? _directoryEntries;
    private DirectoryEntry[]? _displayedDirectoryEntries;
    private Dictionary<string, DirectoryEntry[]> _directoryChildren = new(StringComparer.OrdinalIgnoreCase), _activeTreeChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewItem> _fileNodeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Session, FileSystemWatcher Watcher)> _catalogChanges = new(StringComparer.OrdinalIgnoreCase);
    private int _catalogDispatchQueued, _directoryScanCount, _fileTreeApplyCount, _directoryChangeVersion;
    private bool _catalogDirty;
    private Task? _fileCatalogTask;
    private CancellationTokenSource? _fileCatalogCancellation;
    private DispatcherTimer? _directoryRefresh;
    private int _fileTreeMatchingCount = -1, _fileOpenRequest;
    private readonly HashSet<string> _openingExplorerFiles = new(StringComparer.OrdinalIgnoreCase);

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetSidebarTreeExpanded(true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetSidebarTreeExpanded(false);
    private void SetSidebarTreeExpanded(bool expanded)
    {
        if (_sidebar == "outline") OutlineTree.SetAllExpanded(expanded);
        else if (_sidebar == "bookmarks") BookmarkTree.SetAllExpanded(expanded);
        else if (_sidebar == "files")
            foreach (var item in FileTreeNodes(FileTree.Items).Where(item => item.Tag is null)) item.IsExpanded = expanded;
    }
    private static IEnumerable<TreeViewItem> FileTreeNodes(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in FileTreeNodes(item.Items)) yield return child;
        }
    }
    private static DependencyObject? SidebarParent(DependencyObject element) => element is Visual
        ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    private void Sidebar_BlankDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        for (var target = e.OriginalSource as DependencyObject; target is not null && target != WorkspaceRail; target = SidebarParent(target))
            if (target is TreeViewItem or ListBoxItem or ButtonBase or TextBoxBase or ScrollBar or Thumb or Hyperlink or Controls.HoverMarquee
                || target is TextBlock { Text.Length: > 0 }) return;
        ToggleWorkspace(); e.Handled = true;
    }
    private void FileSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_catalogDirty && !_buildingFileTree) InvalidateDirectoryCatalog();
        if (IsInitialized && !_buildingFileTree && _project is not null && _sidebar == "files") BuildFileTree(CurrentFiles());
    }
    private void FileSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FileSearchBox.Text.Length != 0) { FileSearchBox.Clear(); e.Handled = true; }
    }
    private void ClearFileSearch_Click(object sender, RoutedEventArgs e) { FileSearchBox.Clear(); FileSearchBox.Focus(); }
    private TreeViewItem? FileRow(DependencyObject? target)
    {
        for (; target is not null && target != FileTree; target = SidebarParent(target))
            if (target is TreeViewItem item) return item;
        return null;
    }
    private void FileTree_LeftDown(object sender, MouseButtonEventArgs e)
    {
        _suppressExplorerClick = false;
        _explorerDragPath = FileRow(e.OriginalSource as DependencyObject)?.Tag as string; _explorerDragStart = e.GetPosition(FileTree);
        // Folder clicks are handled once on release, without the native double-click toggle.
        if (e.ClickCount == 2 && FileRow(e.OriginalSource as DependencyObject) is { Tag: null }) e.Handled = true;
    }
    private void FileTree_LeftUp(object sender, MouseButtonEventArgs e)
    {
        _explorerDragPath = null;
        if (_suppressExplorerClick) { _suppressExplorerClick = false; e.Handled = true; return; }
        if (FileRow(e.OriginalSource as DependencyObject) is not { } item) return;
        if (item.Tag is string path) { if (!string.Equals(path, Editor.CurrentFilePath, StringComparison.OrdinalIgnoreCase)) OpenExplorerFile(path); }
        else { item.IsExpanded = !item.IsExpanded; e.Handled = true; }
    }
    private async void OpenExplorerFile(string path)
    {
        if (_project is null || !_openingExplorerFiles.Add(path)) return;
        var session = _session; var request = ++_fileOpenRequest;
        try
        {
            if (CurrentFiles().Any(file => file.IsEditable && file.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) { ActivateFile(path); ResumeFollowFromSource(Editor); return; }
            if (!AiWorkspaceFiles.IsText(path)) { Status("项目资源：" + Path.GetRelativePath(_project.Root, path)); return; }
            var file = await AiWorkspaceFiles.ReadAsync(_project.Root, Path.GetRelativePath(_project.Root, path), CancellationToken.None);
            if (session != _session || _project is null || _closing || _transitioning || request != _fileOpenRequest) return;
            if (!CurrentFiles().Any(current => current.IsEditable && current.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _project = _project with { Files = _project.Files.Where(current => !current.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Append(file).ToArray() };
            ActivateFile(path); ResumeFollowFromSource(Editor); RefreshIndex();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (session == _session) Status("无法打开项目文件：" + ex.Message); }
        finally { _openingExplorerFiles.Remove(path); }
    }
    private void ResetDirectoryCatalog()
    {
        _fileCatalogCancellation?.Cancel(); _directoryRefresh?.Stop();
        _fileCatalogTask = null; _directoryEntries = _displayedDirectoryEntries = null; _fileTreeProject = null;
        _fileNodeCache.Clear(); _catalogChanges.Clear(); _catalogDirty = false;
    }
    private void QueueDirectoryCatalogChange(string path, int session, FileSystemWatcher watcher)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".texeed-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        _catalogChanges[Path.GetDirectoryName(path)!] = (session, watcher);
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _catalogDispatchQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _catalogDispatchQueued, 0);
            var visible = false;
            foreach (var pair in _catalogChanges.ToArray())
            {
                if (!_catalogChanges.TryRemove(pair.Key, out var owner) || owner.Session != _session || !ReferenceEquals(owner.Watcher, _watcher) || _project is null || _closing) continue;
                _catalogDirty = true; _directoryChangeVersion++;
                visible |= _sidebar == "files" && _workspaceExpanded && (pair.Key.Equals(_project.Root, StringComparison.OrdinalIgnoreCase)
                    || FileTreeNodes(FileTree.Items).Any(node => node.Tag is null && node.IsExpanded && Path.Combine(_project.Root, node.Uid).Equals(pair.Key, StringComparison.OrdinalIgnoreCase)));
            }
            if (visible) InvalidateDirectoryCatalog();
        }));
    }
    private void InvalidateDirectoryCatalog()
    {
        if (_directoryRefresh is null)
        {
            _directoryRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _directoryRefresh.Tick += (_, _) =>
            {
                _directoryRefresh.Stop(); _fileCatalogCancellation?.Cancel(); _fileCatalogTask = null; _directoryEntries = null;
                if (_sidebar == "files" && _project is not null && !_closing) BuildFileTree(CurrentFiles());
            };
        }
        _directoryRefresh.Stop(); _directoryRefresh.Start();
    }
    private async Task LoadDirectoryCatalogAsync(string root)
    {
        var cancellation = new CancellationTokenSource(); _fileCatalogCancellation = cancellation;
        try
        {
            _directoryScanCount++; var changeVersion = _directoryChangeVersion;
            var snapshot = await Task.Run(() => ProjectDirectoryCatalog.Snapshot(root, cancellation.Token));
            if (cancellation.IsCancellationRequested || _project?.Root != root || _closing || !ReferenceEquals(_fileCatalogCancellation, cancellation)) return;
            _catalogDirty = changeVersion != _directoryChangeVersion;
            if (_priorityProject != root)
            {
                var priority = await Task.Run(() => ProjectExplorerSettings.Load(root));
                if (cancellation.IsCancellationRequested || _project?.Root != root || _closing || !ReferenceEquals(_fileCatalogCancellation, cancellation)) return;
                _priorityFiles = priority; _priorityProject = root; RefreshPriorityFiles();
            }
            var previous = _displayedDirectoryEntries;
            var unchanged = previous is not null && previous.Length == snapshot.Entries.Length
                && await Task.Run(() => previous.Select(entry => (entry.Path, entry.IsDirectory)).ToHashSet().SetEquals(snapshot.Entries.Select(entry => (entry.Path, entry.IsDirectory))));
            if (cancellation.IsCancellationRequested || _project?.Root != root || _closing || !ReferenceEquals(_fileCatalogCancellation, cancellation)) return;
            if (unchanged) { _directoryEntries = previous; return; }
            _directoryEntries = snapshot.Entries; _directoryChildren = snapshot.Children;
            RefreshPriorityFiles();
            if (_sidebar == "files") BuildFileTree(CurrentFiles());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (_project?.Root == root) Status("无法读取目录：" + ex.Message); }
        finally { cancellation.Dispose(); if (ReferenceEquals(_fileCatalogCancellation, cancellation)) _fileCatalogCancellation = null; }
    }
    private void UpdateFileTreeEmptyState()
    {
        EmptyIndex.Text = _directoryEntries is null ? "正在读取目录…" : _fileTreeFiltering ? "没有匹配的文件" : "目录为空";
        EmptyIndex.Visibility = _directoryEntries is null && FileTree.Items.Count == 0 || _fileTreeMatchingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BuildFileTree(IEnumerable<ProjectFile> files)
    {
        if (_project is null || _buildingFileTree) return;
        _buildingFileTree = true;
        try
        {
            if (_fileTreeProject != _project.Root)
            {
                _fileTreeProject = _project.Root; _fileTreeExpanded.Clear(); _fileTreeFiltering = false; _fileTreeQuery = null;
                _directoryEntries = _displayedDirectoryEntries = null; _fileCatalogCancellation?.Cancel(); _fileCatalogTask = null;
                _fileNodeCache.Clear();
                _priorityProject = null; _priorityFiles = []; RefreshPriorityFiles();
                FileSearchBox.Clear(); FileTree.Items.Clear(); _fileTreeMatchingCount = -1;
            }
            if (_directoryEntries is null)
            {
                _fileCatalogTask ??= LoadDirectoryCatalogAsync(_project.Root);
                UpdateFileTreeEmptyState(); return;
            }
            var query = FileSearchBox.Text.Trim();
            if (ReferenceEquals(_displayedDirectoryEntries, _directoryEntries) && _fileTreeQuery == query) { UpdateFileTreeEmptyState(); return; }
            if (!_fileTreeFiltering)
                foreach (var node in FileTreeNodes(FileTree.Items).Where(item => item.Tag is null && item.Uid.Length > 0)) _fileTreeExpanded[node.Uid] = node.IsExpanded;
            if (_fileTreeQuery != query) { FileTree.Items.Clear(); _fileNodeCache.Clear(); }
            _fileTreeFiltering = query.Length != 0; _fileTreeApplyCount++;
            _fileTreeQuery = query; _displayedDirectoryEntries = _directoryEntries;
            var matching = _fileTreeFiltering ? _directoryEntries.Where(entry => !entry.IsDirectory && Path.GetFileName(entry.Path).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray() : [];
            _fileTreeMatchingCount = _fileTreeFiltering ? matching.Length : _directoryEntries.Length;
            var included = matching.Select(entry => entry.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_fileTreeFiltering)
                foreach (var entry in matching)
                {
                    var relative = entry.RelativePath.Replace('\\', '/');
                    while (relative.LastIndexOf('/') is var slash && slash >= 0) { relative = relative[..slash]; included.Add(relative); }
                }
            var children = !_fileTreeFiltering ? _directoryChildren : _directoryEntries.Where(entry => included.Contains(entry.RelativePath.Replace('\\', '/')))
                .GroupBy(entry => (Path.GetDirectoryName(entry.RelativePath) ?? "").Replace('\\', '/'))
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
            _activeTreeChildren = children;
            var selectedPath = (FileTree.SelectedItem as TreeViewItem)?.Tag as string;
            void Reconcile(ItemCollection items, DirectoryEntry[] entries)
            {
                var expected = entries.Select(Node).ToArray();
                foreach (var stale in items.OfType<TreeViewItem>().Except(expected).ToArray())
                {
                    items.Remove(stale);
                    foreach (var removed in FileTreeNodes(stale.Items).Prepend(stale))
                        if (_fileNodeCache.TryGetValue(removed.Uid, out var cached) && ReferenceEquals(cached, removed)) _fileNodeCache.Remove(removed.Uid);
                }
                for (var i = 0; i < expected.Length; i++)
                {
                    if (i < items.Count && ReferenceEquals(items[i], expected[i])) continue;
                    items.Remove(expected[i]); items.Insert(i, expected[i]);
                }
            }
            TreeViewItem Node(DirectoryEntry entry)
            {
                var id = entry.RelativePath.Replace('\\', '/');
                if (_fileNodeCache.TryGetValue(id, out var retained) && entry.IsDirectory == (retained.Tag is null)
                    && Equals(retained.ToolTip, entry.Path))
                {
                    if (entry.IsDirectory && retained.IsExpanded) Populate(retained);
                    return retained;
                }
                var node = new TreeViewItem
                {
                    Header = Path.GetFileName(entry.Path) + (entry.Path.Equals(_project.MainPath, StringComparison.OrdinalIgnoreCase) ? "  · 主文件" : ""),
                    HeaderTemplate = (DataTemplate)FileTree.FindResource("ProjectFileHeader"), Uid = id, Tag = entry.IsDirectory ? null : entry.Path,
                    ToolTip = entry.Path
                };
                _fileNodeCache[id] = node;
                if (entry.IsDirectory)
                {
                    node.Items.Add(new TreeViewItem());
                    node.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, node)) { Populate(node); if (_catalogDirty) InvalidateDirectoryCatalog(); } };
                    if (_fileTreeFiltering || _fileTreeExpanded.GetValueOrDefault(id, false)) { Populate(node); node.IsExpanded = true; }
                }
                else node.ContextMenu = ExplorerFileMenu(entry.Path);
                if (!entry.IsDirectory && string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) node.IsSelected = true;
                return node;
            }
            void Populate(TreeViewItem node) => Reconcile(node.Items, _activeTreeChildren.GetValueOrDefault(node.Uid, []));
            Reconcile(FileTree.Items, children.GetValueOrDefault("", []));
            UpdateFileTreeEmptyState();
        }
        finally { _buildingFileTree = false; }
    }
}
