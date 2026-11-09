using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private ProjectCommentStore? _projectComments;
    private readonly AiCommentSidebar _projectCommentSidebar = new() { ShowVersions = false, ShowClose = true, TitleText = "项目评论" };
    private readonly DockPanel _projectCommentPanel = new() { Visibility = Visibility.Collapsed };
    private readonly AiReadOnlyCommentList _projectPdfComments = new() { Visibility = Visibility.Collapsed, MaxHeight = 200 };
    private readonly DispatcherTimer _projectCommentTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private int _projectCommentGeneration;
    private bool _projectCommentDirty;
    private string? _projectCommentFile;
    private bool ProjectCommentsVisible => _projectCommentPanel.Visibility == Visibility.Visible;

    private void InitializeProjectComments()
    {
        _projectCommentSidebar.CloseRequested += (_, _) => { HideProjectCommentsSidebar(); ShowAiSidebar(null); };
        _projectCommentPanel.Children.Add(_projectCommentSidebar);
        _projectCommentTimer.Tick += async (_, _) =>
        {
            _projectCommentTimer.Stop();
            if (ProjectCommentsVisible) _projectCommentSidebar.Refresh();
            try { await FlushProjectCommentsAsync(); } catch (Exception ex) { Status("项目评论未能保存：" + ex.Message); }
        };
        foreach (var editor in Editors)
        {
            var viewComments = new MenuItem { Header = "查看项目评论" };
            var item = new MenuItem { Header = "为选区添加项目评论" };
            editor.ContextMenu.Items.Add(new Separator()); editor.ContextMenu.Items.Add(viewComments); editor.ContextMenu.Items.Add(item);
            editor.ContextMenu.Opened += (_, _) =>
            {
                viewComments.IsEnabled = _projectComments is not null && editor.CurrentFilePath is not null;
                item.IsEnabled = viewComments.IsEnabled && editor.SelectionLength > 0;
            };
            viewComments.Click += (_, _) => ShowProjectComments(editor);
            item.Click += (_, _) => OpenProjectComment(editor);
            editor.ContentEdited += (_, _) => ProjectEditorChanged(editor);
            editor.GotKeyboardFocus += (_, _) =>
            {
                if (ProjectCommentsVisible && editor.CurrentFilePath is { } path && !string.Equals(_projectCommentFile, path, StringComparison.OrdinalIgnoreCase))
                    BindProjectCommentFile(editor, false);
            };
        }
        PreviewContent.Children.Remove(Preview);
        var wrapper = new DockPanel();
        var show = new Button { Content = "项目只读评论", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 3, 8, 3) };
        show.Click += (_, _) =>
        {
            _projectPdfComments.Visibility = _projectPdfComments.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (_projectPdfComments.Visibility == Visibility.Visible) RefreshProjectPdfComments(); else _projectPdfComments.Clear();
        };
        DockPanel.SetDock(show, Dock.Top); wrapper.Children.Add(show);
        DockPanel.SetDock(_projectPdfComments, Dock.Top); wrapper.Children.Add(_projectPdfComments);
        wrapper.Children.Add(Preview); PreviewContent.Children.Add(wrapper);
    }

    private void LoadProjectComments(TexProject project)
    {
        _projectCommentTimer.Stop(); _projectCommentDirty = false; _projectCommentFile = null;
        HideProjectCommentsSidebar(); _projectPdfComments.Visibility = Visibility.Collapsed; _projectPdfComments.Clear();
        try { _projectComments = ProjectCommentStore.Load(project.MainPath, project.Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _projectComments = null; Status("无法读取项目评论：" + ex.Message); }
    }
    private void ProjectCommentChanged()
    {
        _projectCommentGeneration++; _projectCommentDirty = true;
        _projectCommentTimer.Stop(); _projectCommentTimer.Start();
    }
    private async Task FlushProjectCommentsAsync()
    {
        _projectCommentTimer.Stop(); var store = _projectComments;
        if (store is null || !_projectCommentDirty) return;
        var generation = _projectCommentGeneration;
        await store.SaveAsync();
        if (ReferenceEquals(store, _projectComments) && generation == _projectCommentGeneration) _projectCommentDirty = false;
    }
    private void ProjectEditorChanged(LatexEditor editor)
    {
        if (_transitioning || editor.CurrentFilePath is not { } path) return;
        if (_pdfPath is not null) PdfStatus.Text = "项目源码已更改，预览已过期，请重新编译。";
        if (_projectComments is null) return;
        try { if (_projectComments.UpdateIfTracked(path, editor.Text)) ProjectCommentChanged(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { Status("项目评论锚点未更新：" + ex.Message); }
    }
    private void BindProjectCommentFile(LatexEditor editor, bool create)
    {
        if (_projectComments is null || editor.CurrentFilePath is not { } path) return;
        try
        {
            var existing = _projectComments.Files.Any(item => string.Equals(item.File, path, StringComparison.OrdinalIgnoreCase));
            if (!existing && !create) { HideProjectCommentsSidebar(); ShowAiSidebar(null); return; }
            var changed = existing && _projectComments.UpdateIfTracked(path, editor.Text);
            var item = _projectComments.GetOrCreate(path, editor.Text); _projectCommentFile = path;
            _projectCommentSidebar.Bind(item, "用户", ProjectCommentChanged, ProjectCommentChanged);
            if (!existing || changed) ProjectCommentChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { Status("无法打开项目评论：" + ex.Message); }
    }
    private void ShowProjectComments(LatexEditor editor)
    {
        if (_projectComments is null || editor.CurrentFilePath is null) return;
        BindProjectCommentFile(editor, true);
        if (_projectCommentFile is null || !string.Equals(_projectCommentFile, editor.CurrentFilePath, StringComparison.OrdinalIgnoreCase)) return;
        ShowAiSidebar(null);
        if (_aiSidebarHost is null) return;
        if (_aiSidebarOriginal is not null) _aiSidebarOriginal.Visibility = Visibility.Collapsed;
        if (_projectCommentPanel.Parent is Panel previous && previous != _aiSidebarHost) previous.Children.Remove(_projectCommentPanel);
        if (_projectCommentPanel.Parent is null) _aiSidebarHost.Children.Add(_projectCommentPanel);
        _projectCommentPanel.Visibility = Visibility.Visible;
        if (!_workspaceExpanded) ToggleWorkspace();
    }
    private void OpenProjectComment(LatexEditor editor)
    {
        if (_projectComments is null || editor.SelectionLength <= 0) return;
        ShowProjectComments(editor);
        if (!ProjectCommentsVisible || _projectCommentFile is null || !string.Equals(_projectCommentFile, editor.CurrentFilePath, StringComparison.OrdinalIgnoreCase)) return;
        var revision = _projectComments.GetOrCreate(_projectCommentFile, editor.Text).SharedDocument.Revision;
        _projectCommentSidebar.BeginComment(revision, editor.SelectionStart, editor.SelectionLength);
    }
    private void HideProjectCommentsSidebar() => _projectCommentPanel.Visibility = Visibility.Collapsed;

    private void RefreshProjectPdfComments()
    {
        if (_projectPdfComments.Visibility != Visibility.Visible || _projectComments is null) return;
        var rows = new List<AiReadOnlyCommentRow>();
        foreach (var (file, conversation) in _projectComments.Files.ToArray())
        {
            var current = CurrentFiles().FirstOrDefault(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase));
            if (current is not null && _projectComments.UpdateIfTracked(file, current.Content)) ProjectCommentChanged();
            foreach (var comment in conversation.SharedDocument.Snapshot().Comments)
            {
                var label = Path.GetFileName(file) + " · " + (string.IsNullOrWhiteSpace(comment.Author) ? "用户" : comment.Author) + "：" + comment.Text + (comment.Orphaned ? " · 无法定位" : " · 点击尝试近似定位");
                rows.Add(new AiReadOnlyCommentRow(label, comment.Replies, () => LocateProjectCommentAsync(file, conversation, comment)));
            }
        }
        _projectPdfComments.SetRows(rows);
    }
    private async Task LocateProjectCommentAsync(string file, AiConversation conversation, AiDocumentComment comment)
    {
        var store = _projectComments; var compiled = _previewProject; var inputs = _previewInputs; var pdf = _pdfPath; var session = _session; var revision = _revision;
        if (store is null || compiled is null || inputs is null || pdf is null) { Status("尚无可定位的项目 PDF。"); return; }
        async Task<bool> Current(CancellationToken token) => session == _session && revision == _revision && _pdfRevision == revision && ReferenceEquals(inputs, _previewInputs) && ReferenceEquals(compiled, _previewProject) && await inputs.IsCurrentAsync(_projects, token);
        try
        {
            var result = await ProjectCommentStore.LocateAsync(conversation, comment, file, compiled, pdf, _settings, _sync, Current);
            if (result.Status == AiCommentLocationStatus.Approximate && result.Location is { } location) { Preview.ShowLocation(location, highlight: false); Status("已定位到项目源码对应的近似位置。"); }
            else Status(result.Status == AiCommentLocationStatus.PreviewOutdated ? "项目预览已过期，请重新编译后定位。" : "此项目评论无法定位到 PDF。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { Status("无法定位项目评论：" + ex.Message); }
    }
}
