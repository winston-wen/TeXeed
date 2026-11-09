using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;
public partial class MainWindow
{
    private readonly AiConnection _aiConnection = new();
    private AiSettingsPanel _aiSettings = null!;
    private AiWorkspaceStore? _aiStore;
    private AiHistoryView? _aiHistory;
    private readonly Dictionary<string,AiPanel> _aiPanels = [];
    private readonly Dictionary<string,AiSessionSourceView> _aiSources = [];
    private readonly Dictionary<string,AiSessionPdfView> _aiPreviews = [];
    private readonly Dictionary<string,Border> _aiCommentTabs = [];
    private Grid? _aiSidebarHost;
    private UIElement? _aiSidebarOriginal;
    private readonly HashSet<string> _closingAi = [];
    private readonly HashSet<Task> _aiCloseTasks = [];
    private readonly HashSet<string> _deletingAi = [];
    private AiPanel CreateAiPanel()
    {
        var panel = new AiPanel(_aiConnection, _aiSettings);
        panel.SettingsRequested += (_,_) => OpenAiSettings();
        panel.ProjectFiles = () => CurrentFiles().Where(f => f.IsEditable).ToArray();
        panel.CreateToolRunner = () => CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        panel.PreviewMouseDown += (_,_) => SetAiTarget(panel);
        panel.GotKeyboardFocus += (_,_) => SetAiTarget(panel);
        return panel;
    }
    private void SetAiTarget(AiPanel panel)
    {
        if (ReferenceEquals(_aiPanel,panel)) return;
        _aiPanel = panel;
        if (_aiStore is not null) _aiStore.Book.ActiveConversationId = panel.ConversationId;
    }
    private AiPanel EnsureAiPanel(string id)
    {
        if (_aiPanels.TryGetValue(id,out var panel)) return panel;
        panel = CreateAiPanel(); panel.Bind(_aiStore!,id);
        _aiPanels.Add(id,panel); return panel;
    }
    private void EnsureAiSessionViews(string id)
    {
        if (_aiStore is null || _aiSources.ContainsKey(id)) return;
        var panel = EnsureAiPanel(id); var document = panel.Conversation.SharedDocument;
        var source = new AiSessionSourceView(document, AiDocumentRepository.GetDirectory(_aiStore.Project, id), _settings);
        var pdf = new AiSessionPdfView(_aiStore.Project, id, document, _settings);
        pdf.PreviewSettingsRequested += (_, _) => OpenAiPreviewSettings(pdf.Preview);
        if (Preview.IsAutoWidth) pdf.Preview.FitWidth(); else pdf.Preview.SetZoom(Preview.Zoom);
        _aiSources.Add(id, source); _aiPreviews.Add(id, pdf);
        _aiCommentTabs.Add(id, new Border { Child = new TextBlock { Text = "该会话的评论与附言在左侧。", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap } });
        source.DocumentEdited += (_, _) => panel.NotifyExternalDocumentChanged();
        source.CommentRequested += (_, _) => { var selection = source.Selection; panel.BeginSourceComment(source.Revision, selection.Start, selection.Length); };
        source.CompileRequested += async (_, _) => { if (!source.HasConflict) await pdf.CompileAsync(); else Status("请先处理会话源码冲突。 "); };
        panel.SharedDocumentChanged += (_, _) => { source.RefreshFromDocument(); pdf.MarkSourceChanged(); };
        panel.SharedCommentsChanged += (_, _) => pdf.RefreshComments();
        panel.DocumentMatchRequested += (start, length) =>
        {
            NavigateAiSource(id, panel, source, document, document.Revision, start, length);
        };
    }
    private void NavigateAiSource(string id, AiPanel panel, AiSessionSourceView source, AiSharedDocument document, string revision, int start, int length)
    {
        if (_closing || _transitioning || _aiStore?.Book.Conversations.Any(c => c.Id == id && ReferenceEquals(c.SharedDocument, document)) != true ||
            revision != document.Revision || source.HasConflict || !source.RefreshFromDocument() || source.Revision != document.Revision || start < 0 || length < 0 || start > document.Source.Length || length > document.Source.Length - start)
        { Status("文稿已变化或源码有冲突，请先处理后重新定位。"); return; }
        _workspaceTabs.Open(TabWorkspace.AiSourceKey(id)); SetAiTarget(panel); RefreshWorkspaceViews();
        source.Editor.CaretOffset = start; source.Editor.Select(start, length);
        source.Editor.ScrollTo(source.Editor.TextArea.Caret.Line, source.Editor.TextArea.Caret.Column); source.Editor.TextArea.Focus();
    }
    private void ShowAiSidebar(AiPanel? panel)
    {
        if (panel is not null) HideProjectCommentsSidebar();
        if (_aiSidebarHost is null)
        {
            var original = SidebarPanel.Child; SidebarPanel.Child = null; _aiSidebarOriginal = original;
            _aiSidebarHost = new Grid(); if (original is not null) _aiSidebarHost.Children.Add(original);
            SidebarPanel.Child = _aiSidebarHost;
        }
        foreach (var other in _aiPanels.Values) if (other.Parent == _aiSidebarHost) other.Visibility = Visibility.Collapsed;
        if (_aiSidebarOriginal is not null) _aiSidebarOriginal.Visibility = panel is null && !ProjectCommentsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (panel is null) return;
        if (panel.Parent is Panel old && old != _aiSidebarHost) old.Children.Remove(panel);
        if (panel.Parent is null) _aiSidebarHost.Children.Add(panel);
        panel.Visibility = Visibility.Visible;
    }
    private async Task BuildFocusedDocumentAsync()
    {
        var key = _workspaceTabs.FocusedTab;
        var id = key is null ? null : TabWorkspace.AiDocumentConversationId(key) ?? AiConversationForKey(key)?.Id;
        if (id is not null && _aiPreviews.TryGetValue(id, out var preview))
        {
            if (_aiSources.TryGetValue(id, out var source) && source.HasConflict) { Status("请先处理会话源码冲突。"); return; }
            await preview.CompileAsync(); return;
        }
        await BuildAsync();
    }
    private async Task InitializeAiProjectAsync(string project)
    {
        await StopAiWorkspaceAsync();
        _aiStore = await AiWorkspaceStore.LoadAsync(project);
        _aiStore.Changed += AiStoreChanged;
        _aiHistory = null;
        _aiPanel = EnsureAiPanel(_aiStore.Book.Active.Id);
    }
    private async Task StopAiWorkspaceAsync()
    {
        if (_aiSources.Values.Any(view => view.HasConflict)) throw new InvalidOperationException("会话源码存在冲突草稿，请先处理；内容仍保留在编辑器中。");
        // Tab closing saves asynchronously after removing its view from the map.
        // Finish it before a project switch snapshots/disposes the remaining panels.
        if (_aiCloseTasks.Count > 0) await Task.WhenAll(_aiCloseTasks.ToArray());
        var panels = _aiPanels.Values.Append(_aiPanel).Where(p => p is not null).Distinct().ToArray();
        var frozen = panels.Cast<UIElement>().Concat(_aiSources.Values).Concat(_aiPreviews.Values).Concat(_aiCommentTabs.Values)
            .Select(view => (View: view, Enabled: view.IsEnabled)).ToArray();
        foreach (var entry in frozen) entry.View.IsEnabled = false;
        try
        {
            if (_aiHistory is not null) await _aiHistory.CommitRenameAsync();
            foreach (var panel in panels) await panel.StopAndSaveAsync();
        }
        catch { foreach (var entry in frozen) entry.View.IsEnabled = entry.Enabled; throw; }
        if (_aiHistory?.Parent is Panel historyParent) historyParent.Children.Remove(_aiHistory);
        foreach (var panel in panels) { if (panel.Parent is Panel parent) parent.Children.Remove(panel); panel.Dispose(); }
        foreach (var view in _aiSources.Values) if (view.Parent is Panel parent) parent.Children.Remove(view);
        foreach (var view in _aiPreviews.Values) { if (view.Parent is Panel parent) parent.Children.Remove(view); view.Dispose(); }
        foreach (var view in _aiCommentTabs.Values) if (view.Parent is Panel parent) parent.Children.Remove(view);
        _aiSources.Clear(); _aiPreviews.Clear(); _aiCommentTabs.Clear(); _aiPanels.Clear(); _closingAi.Clear(); _deletingAi.Clear();
        if (_aiStore is not null) _aiStore.Changed -= AiStoreChanged;
        _aiStore = null;
    }
    private bool IsAiConversationOpen(string id) => _closingAi.Contains(id) || new[] { TabWorkspace.AiConversationKey(id), TabWorkspace.AiSourceKey(id), TabWorkspace.AiPreviewKey(id) }.Any(key => _workspaceTabs.Owner(key) is not null);
    private async Task OpenAiConversationAsync(string id)
    {
        if (_aiStore is null || !_aiStore.Book.Conversations.Any(c=>c.Id==id)) return;
        if (_closingAi.Contains(id)) { Status("该会话正在关闭并保存，请稍后再打开。"); return; }
        if (_deletingAi.Contains(id)) { Status("该会话正在删除，无法打开。"); return; }
        try
        {
            var panel = EnsureAiPanel(id); EnsureAiSessionViews(id); SetAiTarget(panel);
            _aiStore.Book.ActiveConversationId = id;
            _workspaceTabs.Open(TabWorkspace.AiConversationKey(id)); _workspaceTabs.Open(TabWorkspace.AiSourceKey(id)); _workspaceTabs.Open(TabWorkspace.AiPreviewKey(id)); RefreshWorkspaceViews();
            _aiHistory?.RefreshExternal(); await _aiStore.SaveAsync();
        }
        catch(Exception ex) { Error(ex); }
    }
    private async Task OpenNewAiConversationAsync()
    {
        if (_aiStore is null) { Status("请先打开项目，再使用 AI。"); return; }
        var c = _aiStore.Book.Active;
        if (IsAiConversationOpen(c.Id) || c.Turns.Count>0 || c.Entries.Count>0 || c.Draft.Length>0) c = _aiStore.Book.NewConversation();
        await OpenAiConversationAsync(c.Id);
    }
    private void OpenAiHistory()
    {
        if (_aiStore is null) { Status("请先打开项目，再使用 AI。"); return; }
        _aiHistory ??= new AiHistoryView(_aiStore.Book,DeleteAiConversationsAsync,RenameAiConversationAsync,IsAiConversationOpen,id=>_ = OpenAiConversationAsync(id));
        _workspaceTabs.Open(TabWorkspace.AiHistoryKey); RefreshWorkspaceViews(); _aiHistory.RefreshExternal();
    }
    private async Task RenameAiConversationAsync(string id,string title)
    {
        if (_aiStore is null || string.IsNullOrWhiteSpace(title) || title.Length>200) return;
        var c=_aiStore.Book.Conversations.FirstOrDefault(c=>c.Id==id); if(c is null)return;
        var previous=c.Title; c.Title=title.Trim();
        try { await _aiStore.SaveAsync(); }
        catch { c.Title=previous; AiStoreChanged(); throw; }
    }
    private void ShowAiConversationRename(string key)
    {
        if (AiConversationForKey(key) is not { } conversation || _closing || _transitioning) return;
        var input = new TextBox { Text = conversation.Title, MaxLength = 200, MinWidth = 280 };
        System.Windows.Automation.AutomationProperties.SetName(input, "AI 对话标题");
        var accept = new Button { Content = "确定", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(cancel); actions.Children.Add(accept);
        var content = new StackPanel { Margin = new Thickness(20) }; content.Children.Add(input); content.Children.Add(actions);
        var dialog = new Window { Owner = this, Title = "重命名 AI 对话", Content = content, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        TeXeed.App.Services.WindowTheme.Attach(dialog);
        var saving = false;
        dialog.Closing += (_, e) => { if (saving) e.Cancel = true; };
        accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        accept.Click += async (_, _) =>
        {
            saving = true; accept.IsEnabled = input.IsEnabled = cancel.IsEnabled = false;
            try { await RenameAiConversationAsync(conversation.Id, input.Text); saving = false; dialog.DialogResult = true; }
            catch (Exception ex) { Status("重命名失败：" + ex.Message); input.IsEnabled = accept.IsEnabled = cancel.IsEnabled = true; }
            finally { saving = false; }
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }
    private async Task DeleteAiConversationsAsync(IReadOnlyCollection<string> ids)
    {
        if (_aiStore is null) return;
        if (ids.Any(IsAiConversationOpen)) throw new InvalidOperationException("会话窗口已打开，关闭后才能删除。请先关闭对应的会话标签。");
        var store = _aiStore; var originals = store.Book.Conversations.ToArray(); var activeId = store.Book.ActiveConversationId;
        foreach (var id in ids) _deletingAi.Add(id);
        try
        {
            foreach (var id in ids)
                if (_aiPanels.Remove(id, out var panel)) { await panel.StopAndSaveAsync(); panel.Dispose(); }
            store.Book.Conversations.RemoveAll(c => ids.Contains(c.Id));
            var active = store.Book.Active;
            if (!_aiPanels.Values.Contains(_aiPanel)) _aiPanel = EnsureAiPanel(active.Id);
            await store.SaveAsync();
        }
        catch
        {
            // A still-present notebook conversation recovers its tombstone and prior selection.
            var saved = await AiStorage.LoadAsync(store.Project);
            foreach (var original in originals.Where(c => saved.Conversations.Any(x => x.Id == c.Id)))
                if (!store.Book.Conversations.Any(c => c.Id == original.Id)) store.Book.Conversations.Add(original);
            if (store.Book.Conversations.Any(c => c.Id == activeId)) store.Book.ActiveConversationId = activeId;
            throw;
        }
        finally { foreach (var id in ids) _deletingAi.Remove(id); }
    }
    private async Task CloseAiPanelsAsync(IEnumerable<string> keys)
    {
        var ids = keys.Select(key => AiConversationForKey(key)?.Id).Where(id => id is not null).Cast<string>().Distinct().Where(id => !IsAiConversationOpen(id)).ToArray();
        _closingAi.UnionWith(ids);
        Exception? failure = null;
        try
        {
            foreach (var id in ids)
            {
                try
                {
                    if (!_aiPanels.Remove(id, out var panel)) continue;
                    if (_aiSources.Remove(id, out var source) && source.Parent is Panel sourceParent) sourceParent.Children.Remove(source);
                    if (_aiPreviews.Remove(id, out var pdf)) { if (pdf.Parent is Panel pdfParent) pdfParent.Children.Remove(pdf); pdf.Dispose(); }
                    if (_aiCommentTabs.Remove(id, out var comment) && comment.Parent is Panel commentParent) commentParent.Children.Remove(comment);
                    try { await Task.CompletedTask; }
                    finally
                    {
                        if (panel.Parent is Panel parent) parent.Children.Remove(panel);
                        panel.Dispose();
                        if (ReferenceEquals(panel, _aiPanel) && _aiStore is not null)
                            _aiPanel = _aiPanels.Values.FirstOrDefault() ?? EnsureAiPanel(_aiStore.Book.Active.Id);
                    }
                }
                catch (Exception ex) { failure ??= ex; }
            }
        }
        finally
        {
            foreach (var id in ids) _closingAi.Remove(id);
            _aiHistory?.RefreshExternal();
        }
        if (failure is not null) throw failure;
    }
    private string AiTabTitle(string key) => AiConversationForKey(key) is { } c ? c.TabTitle + (TabWorkspace.IsAiSource(key) ? " · LaTeX" : TabWorkspace.IsAiPreview(key) ? " · PDF" : " · 评论") : key==TabWorkspace.AiHistoryKey ? "AI 会话历史" : key==TabWorkspace.AiSettingsKey ? "AI 设置" : "AI 对话";
    private AiConversation? AiConversationForKey(string key) => _aiStore?.Book.Conversations.FirstOrDefault(c=>TabWorkspace.AiConversationKey(c.Id)==key || TabWorkspace.AiDocumentConversationId(key)==c.Id);
    private void AiStoreChanged()
    {
        foreach(var side in new[]{TabSide.Left,TabSide.Right}) UpdateTabStrip(side);
        if (_aiHistory?.IsVisible==true) _aiHistory.RefreshExternal();
    }
    private void UpdateTabStrip(TabSide side) => GroupStrip(side).SetTabs(_workspaceTabs.Tabs(side).Select(key=>new EditorTab(key,
        key==TabWorkspace.PreviewKey ? "预览" : TabWorkspace.IsSource(key) ? System.IO.Path.GetFileName(key) : AiTabTitle(key),
        _changes.ContainsKey(key),key==TabWorkspace.PreviewKey,key==TabWorkspace.PreviewKey&&_previewWarning,AiConversationForKey(key)?.Title)).ToArray(),_workspaceTabs.Active(side));
    private void AiSettings_Click(object sender,RoutedEventArgs e) { HideMainMenu(false); OpenAiSettings(); }
    private void AiHistory_Click(object sender,RoutedEventArgs e) { HideMainMenu(false); OpenAiHistory(); }
    private void AiRecent_Opened(object sender,RoutedEventArgs e)
    {
        AiRecentMenu.Items.Clear();
        var recent=_aiStore?.Book.Conversations.Where(c=>c.LastSend is not null).OrderByDescending(c=>c.LastSend).Take(5).ToArray() ?? [];
        foreach(var conversation in recent)
        {
            var item=new MenuItem {Header=conversation.TabTitle,ToolTip=conversation.Title+"\n最后发送："+AiTimestamp.Full(conversation.LastSend!.Value)};
            item.Click+=async(_,_)=>{HideMainMenu(false);await OpenAiConversationAsync(conversation.Id);}; AiRecentMenu.Items.Add(item);
        }
        if(recent.Length==0)AiRecentMenu.Items.Add(new MenuItem{Header="暂无发送记录",IsEnabled=false});
        AiRecentMenu.Items.Add(new Separator());
        var manage=new MenuItem{Header="管理 AI 会话历史"};manage.Click+=AiHistory_Click;AiRecentMenu.Items.Add(manage);
    }
}
