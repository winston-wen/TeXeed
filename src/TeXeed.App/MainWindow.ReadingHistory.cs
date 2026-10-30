using System.IO;
using System.Windows;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private PdfReadingPosition? _pendingPreviewPosition;

    private void SaveReadingHistory()
    {
        if (_project is null) return;
        var editorPositions = new Dictionary<string, EditorReadingPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var editor in Editors)
            foreach (var pair in editor.CaptureReadingPositions()) editorPositions[pair.Key] = pair.Value;
        var leftWidth = _wasSplit ? Workspace.ColumnDefinitions[2].ActualWidth : _splitLeftWidth.Value;
        var rightWidth = _wasSplit ? Workspace.ColumnDefinitions[4].ActualWidth : _splitRightWidth.Value;
        var bounds = _fullscreen ? _beforeFullscreenBounds : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var state = new ProjectReadingState
        {
            LeftTabs = _workspaceTabs.Tabs(TabSide.Left).ToArray(), RightTabs = _workspaceTabs.Tabs(TabSide.Right).ToArray(),
            LeftActive = _workspaceTabs.Active(TabSide.Left), RightActive = _workspaceTabs.Active(TabSide.Right), FocusedSide = _workspaceTabs.FocusedSide,
            Editors = editorPositions, Preview = Preview.CaptureReadingPosition() ?? _pendingPreviewPosition,
            LeftWidth = leftWidth, RightWidth = rightWidth, SidebarExpanded = _workspaceExpanded,
            SidebarWidth = _workspaceExpanded ? SidebarColumn.ActualWidth : _expandedWorkspaceWidth, Sidebar = _sidebar,
            WindowLeft = bounds.Left, WindowTop = bounds.Top, WindowWidth = bounds.Width, WindowHeight = bounds.Height,
            Maximized = (_fullscreen ? _beforeFullscreenState : WindowState) == WindowState.Maximized, Fullscreen = _fullscreen
        };
        try { ProjectReadingStore.Save(_project.MainPath, state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { Status("浏览位置暂未保存：" + ex.Message); }
    }

    private async Task<bool> RestoreReadingHistoryAsync(ProjectReadingState? state)
    {
        if (state is null || _project is null) return false;
        var requested = (state.LeftTabs ?? []).Concat(state.RightTabs ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Take(200).ToArray();
        foreach (var path in requested.Where(TabWorkspace.IsSource))
        {
            if (CurrentFiles().Any(f => f.IsEditable && f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (!AiWorkspaceFiles.IsText(path)) continue;
                var file = await AiWorkspaceFiles.ReadAsync(_project.Root, Path.GetRelativePath(_project.Root, path), CancellationToken.None);
                if (file.IsEditable) _project = _project with { Files = _project.Files.Where(f => !f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Append(file).ToArray() };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        bool Valid(string key)
        {
            if (key == TabWorkspace.PreviewKey || key == TabWorkspace.AiSettingsKey || key == TabWorkspace.AiHistoryKey) return true;
            if (TabWorkspace.IsAiConversation(key))
            {
                var id = key.StartsWith(TabWorkspace.AiKey + ":", StringComparison.Ordinal) ? key[(TabWorkspace.AiKey.Length + 1)..] : "";
                if (_aiStore?.Book.Conversations.Any(c => c.Id == id) != true) return false;
                EnsureAiPanel(id); return true;
            }
            return CurrentFiles().Any(f => f.IsEditable && f.Path.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        var left = (state.LeftTabs ?? []).Where(key => key is not null && Valid(key)).Take(200).ToArray();
        var right = (state.RightTabs ?? []).Where(key => key is not null && Valid(key)).Take(200).ToArray();
        if (left.Concat(right).Contains(TabWorkspace.AiHistoryKey)) OpenAiHistory();
        foreach (var (path, position) in state.Editors ?? [])
        {
            if (position is null || !left.Concat(right).Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            var file = CurrentFiles().FirstOrDefault(f => f.IsEditable && f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is not null) SourceEditor(left.Contains(path, StringComparer.OrdinalIgnoreCase) ? TabSide.Left : TabSide.Right).RestoreReadingPosition(file, position);
        }
        _workspaceTabs.Restore(left, right, state.LeftActive, state.RightActive, state.FocusedSide);
        _splitLeftWidth = new GridLength(ValidWidth(state.LeftWidth, 1), GridUnitType.Star);
        _splitRightWidth = new GridLength(ValidWidth(state.RightWidth, 1), GridUnitType.Star);
        Workspace.ColumnDefinitions[2].Width = _splitLeftWidth; Workspace.ColumnDefinitions[4].Width = _splitRightWidth;
        _wasSplit = true;
        _sidebar = new[] { "outline", "files", "search", "bookmarks", "labels", "todos" }.Contains(state.Sidebar) ? state.Sidebar : "outline";
        var sidebarWidth = Math.Clamp(ValidWidth(state.SidebarWidth, 294), 242, 1000);
        if (_workspaceExpanded != state.SidebarExpanded) ToggleWorkspace();
        _expandedWorkspaceWidth = sidebarWidth;
        if (_workspaceExpanded) SidebarColumn.Width = new GridLength(_expandedWorkspaceWidth);
        RestoreWindowBounds(state);
        _pendingPreviewPosition = state.Preview;
        _followAwaitingSourceInteraction = state.Preview is not null;
        RefreshWorkspaceViews(); RefreshIndex();
        return true;
    }

    private static double ValidWidth(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private void RestoreWindowBounds(ProjectReadingState state)
    {
        if (_fullscreen) ToggleFullscreen();
        if (double.IsFinite(state.WindowLeft) && double.IsFinite(state.WindowTop) && double.IsFinite(state.WindowWidth) && double.IsFinite(state.WindowHeight)
            && state.WindowWidth >= MinWidth && state.WindowHeight >= MinHeight && state.WindowWidth <= 10000 && state.WindowHeight <= 10000)
        {
            var bounds = new Rect(state.WindowLeft, state.WindowTop, state.WindowWidth, state.WindowHeight);
            var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screens.IntersectsWith(bounds))
            {
                var width = Math.Min(bounds.Width, Math.Max(MinWidth, screens.Width));
                var height = Math.Min(bounds.Height, Math.Max(MinHeight, screens.Height));
                var left = Math.Clamp(bounds.Left, screens.Left, Math.Max(screens.Left, screens.Right - width));
                var top = Math.Clamp(bounds.Top, screens.Top, Math.Max(screens.Top, screens.Bottom - height));
                var current = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                if (current.IsEmpty || Math.Abs(current.Left - left) > .1 || Math.Abs(current.Top - top) > .1
                    || Math.Abs(current.Width - width) > .1 || Math.Abs(current.Height - height) > .1)
                {
                    if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                    Left = left; Top = top; Width = width; Height = height;
                }
            }
        }
        var targetState = state.Maximized ? WindowState.Maximized : WindowState.Normal;
        if (WindowState != targetState) WindowState = targetState;
        if (state.Fullscreen) ToggleFullscreen();
    }

    private void RestorePendingPreviewPosition()
    {
        if (_pendingPreviewPosition is not { } position || !Preview.IsVisible) return;
        UpdateLayout();
        if (Preview.RestoreReadingPosition(position)) _pendingPreviewPosition = null;
    }

    private void QueueRestorePendingPreviewPosition() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestorePendingPreviewPosition));
}
