using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly TabWorkspace _workspaceTabs = new();
    private LatexEditor? _activeEditor;
    private bool _updatingWorkspace;
    private bool _wasSplit = true;
    private GridLength _splitLeftWidth = new(1, GridUnitType.Star), _splitRightWidth = new(.95, GridUnitType.Star);
    private LatexEditor Editor => _activeEditor ?? LeftEditor;
    private LatexEditor SourceEditor(TabSide side) => side == TabSide.Left ? LeftEditor : RightEditor;
    private Grid SourceView(TabSide side) => side == TabSide.Left ? LeftSourceView : RightSourceView;
    private Grid GroupBody(TabSide side) => side == TabSide.Left ? LeftGroupBody : RightGroupBody;
    private Grid GroupPane(TabSide side) => side == TabSide.Left ? EditorPane : PreviewPane;
    private EditorTabStrip GroupStrip(TabSide side) => side == TabSide.Left ? FileTabs : RightFileTabs;
    private IEnumerable<LatexEditor> Editors => new[] { LeftEditor, RightEditor };
    private void InitializeTabs()
    {
        RegisterName("Editor", LeftEditor); // Compatibility for source-editor accessibility/test clients.
        foreach (var side in new[] { TabSide.Left, TabSide.Right })
        {
            var strip = GroupStrip(side); var editor = SourceEditor(side);
            strip.Selected += key => SelectWorkspaceTab(side, key);
            strip.CloseRequested += CloseTabs;
            strip.ReorderRequested += (key, target) => MoveTab(key, target);
            strip.SettingsRequested += OpenPreviewSettings;
            strip.DockDragMoved = UpdateDockDrag;
            strip.DockDropped += DropWorkspaceTab;
            strip.DragEnded += () => DockDropCue.Visibility = Visibility.Collapsed;
            GroupPane(side).PreviewMouseDown += (_, _) => { if (!_updatingWorkspace) _workspaceTabs.Focus(side); };
            editor.GotKeyboardFocus += (_, _) => SetActiveSource(editor);
        }
        LogToggle.Click += (_, _) => MoveLogTo(TabSide.Left);
    }
    private void SetActiveSource(LatexEditor editor)
    {
        if (_updatingWorkspace) return;
        _activeEditor = editor;
        _workspaceTabs.Focus(ReferenceEquals(editor, LeftEditor) ? TabSide.Left : TabSide.Right);
        OutlineTree.MarkCaret(editor.CurrentLocation); BookmarkTree.MarkCaret(editor.CurrentLocation);
        OutlineTree.HighlightViewport(editor.VisibleSourceRange); BookmarkTree.HighlightViewport(editor.VisibleSourceRange);
        MoveLogTo(ReferenceEquals(editor, LeftEditor) ? TabSide.Left : TabSide.Right);
        QueueFollowSync();
    }
    private void MoveLogTo(TabSide side)
    {
        var source = SourceView(side);
        if (LogExpander.Parent is Panel parent && parent != source) { parent.Children.Remove(LogExpander); source.Children.Add(LogExpander); Grid.SetRow(LogExpander, 1); }
    }
    private void RightLog_Click(object sender, RoutedEventArgs e) => MoveLogTo(TabSide.Right);
    private void SelectWorkspaceTab(TabSide side, string key)
    {
        if (_transitioning || _closing) return;
        _workspaceTabs.Select(side, key); RefreshWorkspaceViews();
        if (TabWorkspace.IsSource(key)) SourceEditor(side).TextArea.Focus();
    }
    private void CloseTabs(string path, TabCloseScope scope)
    {
        if (_transitioning || _closing || _allowClose) return;
        var range = scope switch { TabCloseScope.All => TabCloseRange.All, TabCloseScope.Others => TabCloseRange.Others,
            TabCloseScope.Left => TabCloseRange.Before, TabCloseScope.Right => TabCloseRange.After, _ => TabCloseRange.This };
        var closed = _workspaceTabs.Close(path, range);
        foreach (var editor in Editors)
            if (editor.CurrentFilePath is { } current && closed.Contains(current, StringComparer.OrdinalIgnoreCase)) editor.CloseView();
        RefreshTabs();
        if (_workspaceTabs.FocusedTab is { } active && TabWorkspace.IsSource(active)) SourceEditor(_workspaceTabs.FocusedSide).TextArea.Focus();
        if (closed.Any(tab => _changes.ContainsKey(tab))) Status("标签已关闭 · 未保存内容仍保留，重新打开可继续编辑");
    }
    private void MoveTab(string path, int target)
    {
        if (_transitioning || _closing || _allowClose || _workspaceTabs.Owner(path) is not { } side) return;
        var active = _workspaceTabs.Active(side); var focused = _workspaceTabs.FocusedSide;
        _workspaceTabs.Move(path, side, target);
        if (active is not null) _workspaceTabs.Select(side, active);
        _workspaceTabs.Focus(focused); RefreshTabs();
    }
    private void MoveWorkspaceTab(string key, TabSide side, int index = int.MaxValue, bool split = false)
    {
        if (_transitioning || _closing || _workspaceTabs.Owner(key) is null) return;
        CancelPendingNavigation();
        var before = _workspaceTabs.Sources.ToDictionary(path => path, path => _workspaceTabs.Owner(path)!.Value, StringComparer.OrdinalIgnoreCase);
        _updatingWorkspace = true;
        try
        {
            if (split) _workspaceTabs.Split(key, side); else _workspaceTabs.Move(key, side, index);
            foreach (var (path, oldSide) in before)
                if (_workspaceTabs.Owner(path) is { } newSide && oldSide != newSide) SourceEditor(oldSide).TransferSessionTo(SourceEditor(newSide), path);
        }
        finally { _updatingWorkspace = false; }
        if (TabWorkspace.IsSource(key)) _activeEditor = SourceEditor(side);
        RefreshTabs();
        if (TabWorkspace.IsSource(key)) SourceEditor(side).TextArea.Focus();
    }
    private void RefreshWorkspaceViews()
    {
        if (_updatingWorkspace) return;
        _updatingWorkspace = true;
        try
        {
            var grid = Workspace;
            var split = _workspaceTabs.Has(TabSide.Left) && _workspaceTabs.Has(TabSide.Right);
            if (_wasSplit && !split)
            { _splitLeftWidth = grid.ColumnDefinitions[2].Width; _splitRightWidth = grid.ColumnDefinitions[4].Width; }
            else if (!_wasSplit && split)
            { grid.ColumnDefinitions[2].Width = _splitLeftWidth; grid.ColumnDefinitions[4].Width = _splitRightWidth; }
            foreach (var side in new[] { TabSide.Left, TabSide.Right })
            {
                var has = _workspaceTabs.Has(side); var pane = GroupPane(side); var source = SourceView(side); var editor = SourceEditor(side);
                pane.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                var column = grid.ColumnDefinitions[side == TabSide.Left ? 2 : 4];
                column.MinWidth = has ? 240 : 0;
                if (!has) column.Width = new GridLength(0);
                else if (!split) column.Width = new GridLength(1, GridUnitType.Star);
                else if (column.Width.IsAbsolute && column.Width.Value == 0) column.Width = new GridLength(1, GridUnitType.Star);
                var active = _workspaceTabs.Active(side);
                var isSource = active is not null && TabWorkspace.IsSource(active);
                source.Visibility = isSource ? Visibility.Visible : Visibility.Collapsed;
                editor.Visibility = isSource ? Visibility.Visible : Visibility.Collapsed;
                if (isSource)
                {
                    var file = CurrentFiles().FirstOrDefault(item => string.Equals(item.Path, active, StringComparison.OrdinalIgnoreCase));
                    if (file is not null)
                    {
                        var other = SourceEditor(side == TabSide.Left ? TabSide.Right : TabSide.Left);
                        other.TransferSessionTo(editor, file.Path);
                        editor.SetProjectFiles(CurrentFiles());
                        if (!string.Equals(editor.CurrentFilePath, file.Path, StringComparison.OrdinalIgnoreCase) || editor.Text != file.Content) editor.ShowFile(file, file.Content);
                    }
                }
                if (active == TabWorkspace.AiKey)
                {
                    if (_aiPanel.Parent is Panel oldAi && oldAi != GroupBody(side)) oldAi.Children.Remove(_aiPanel);
                    if (_aiPanel.Parent is null) GroupBody(side).Children.Add(_aiPanel);
                }
                if (active == TabWorkspace.PreviewKey)
                {
                    if (PreviewContent.Parent is Panel old && old != GroupBody(side)) { old.Children.Remove(PreviewContent); GroupBody(side).Children.Add(PreviewContent); }
                    PreviewContent.Visibility = Visibility.Visible;
                }
                GroupStrip(side).SetTabs(_workspaceTabs.Tabs(side).Select(path => new EditorTab(path,
                    path == TabWorkspace.PreviewKey ? "预览" : path == TabWorkspace.AiKey ? "AI" : System.IO.Path.GetFileName(path), _changes.ContainsKey(path), path == TabWorkspace.PreviewKey)).ToArray(), active);
            }
            _aiPanel.Visibility = _workspaceTabs.Owner(TabWorkspace.AiKey) is { } aiSide && _workspaceTabs.Active(aiSide) == TabWorkspace.AiKey ? Visibility.Visible : Visibility.Collapsed;
            var previewSide = _workspaceTabs.Owner(TabWorkspace.PreviewKey)!.Value;
            PreviewContent.Visibility = _workspaceTabs.Active(previewSide) == TabWorkspace.PreviewKey ? Visibility.Visible : Visibility.Collapsed;
            _wasSplit = split;
            PreviewSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            Workspace.ColumnDefinitions[3].Width = new GridLength(split ? 2 : 0);
            NoOpenFile.Visibility = Visibility.Collapsed;
            _tabs.Clear(); _tabs.AddRange(_workspaceTabs.Sources);
            if (_workspaceTabs.FocusedTab is { } selected && TabWorkspace.IsSource(selected)) _activeEditor = SourceEditor(_workspaceTabs.FocusedSide);
            UpdateCursorStatus();
        }
        finally { _updatingWorkspace = false; }
        if (!Editor.IsVisible && Editors.FirstOrDefault(editor => editor.IsVisible) is { } visible) _activeEditor = visible;
        OutlineTree.MarkCaret(Editor.CurrentLocation); BookmarkTree.MarkCaret(Editor.CurrentLocation);
        OutlineTree.HighlightViewport(Editor.IsVisible ? Editor.VisibleSourceRange : null); BookmarkTree.HighlightViewport(Editor.IsVisible ? Editor.VisibleSourceRange : null);
    }
    private (TabSide Side, bool Split, Rect Bounds)? DockTarget(Point screen)
    {
        var point = DockDropOverlay.PointFromScreen(screen);
        if (point.Y < 0 || point.Y > DockDropOverlay.ActualHeight || point.X < 0 || point.X > DockDropOverlay.ActualWidth) return null;
        var single = !_workspaceTabs.Has(TabSide.Left) || !_workspaceTabs.Has(TabSide.Right);
        if (single && (point.X < 52 || point.X > DockDropOverlay.ActualWidth - 52))
        {
            var left = point.X < 52;
            return (left ? TabSide.Left : TabSide.Right, true, new Rect(left ? 0 : DockDropOverlay.ActualWidth / 2, 0, DockDropOverlay.ActualWidth / 2, DockDropOverlay.ActualHeight));
        }
        foreach (var side in new[] { TabSide.Left, TabSide.Right })
        {
            if (!_workspaceTabs.Has(side)) continue;
            var pane = GroupPane(side); var bounds = pane.TransformToVisual(DockDropOverlay).TransformBounds(new Rect(pane.RenderSize));
            if (bounds.Contains(point)) return (side, false, bounds);
        }
        return null;
    }
    private bool UpdateDockDrag(string key, Point screen)
    {
        var target = DockTarget(screen);
        if (target is null || !target.Value.Split && target.Value.Side == _workspaceTabs.Owner(key)) { DockDropCue.Visibility = Visibility.Collapsed; return false; }
        var box = target.Value.Bounds;
        Canvas.SetLeft(DockDropCue, box.X + 3); Canvas.SetTop(DockDropCue, box.Y + 3);
        DockDropCue.Width = Math.Max(0, box.Width - 6); DockDropCue.Height = Math.Max(0, box.Height - 6); DockDropCue.Visibility = Visibility.Visible;
        return true;
    }
    private void DropWorkspaceTab(string key, Point screen)
    {
        DockDropCue.Visibility = Visibility.Collapsed;
        if (DockTarget(screen) is not { } target || !target.Split && target.Side == _workspaceTabs.Owner(key)) return;
        var strip = GroupStrip(target.Side);
        var point = strip.IsVisible ? strip.PointFromScreen(screen) : new Point();
        var index = strip.IsVisible && point.Y <= strip.ActualHeight ? strip.InsertionIndex(screen) : int.MaxValue;
        MoveWorkspaceTab(key, target.Side, index, target.Split);
    }
}
