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
    private readonly HashSet<string> _closingAi = [];
    private readonly HashSet<Task> _aiCloseTasks = [];
    private readonly HashSet<string> _deletingAi = [];
    private AiAnnotationLayer? _pdfAnnotations;
    private AiPanel CreateAiPanel()
    {
        var panel = new AiPanel(_aiConnection, _aiSettings);
        panel.SettingsRequested += (_,_) => OpenAiSettings();
        panel.ProjectFiles = () => CurrentFiles().Where(f => f.IsEditable).ToArray();
        panel.CreateToolRunner = () => CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        panel.AnnotationsChanged += (_,_) => { if (ReferenceEquals(_aiPanel,panel)) RefreshAiAnnotations(); };
        panel.PreviewMouseDown += (_,_) => SetAiTarget(panel);
        panel.GotKeyboardFocus += (_,_) => SetAiTarget(panel);
        return panel;
    }
    private void SetAiTarget(AiPanel panel)
    {
        if (ReferenceEquals(_aiPanel,panel)) return;
        _aiPanel = panel;
        if (_aiStore is not null) _aiStore.Book.ActiveConversationId = panel.ConversationId;
        RefreshAiAnnotations();
    }
    private void RefreshAiAnnotations()
    {
        _pdfAnnotations?.Refresh();
        foreach (var layer in _sourceAnnotations.Values) layer.Refresh();
    }
    private async Task<AiPanel> EnsureAiPanelAsync(string id)
    {
        if (_aiPanels.TryGetValue(id,out var panel)) return panel;
        panel = CreateAiPanel(); await panel.BindAsync(_aiStore!,id); panel.IsMarking = _aiMarking;
        _aiPanels.Add(id,panel); return panel;
    }
    private async Task InitializeAiProjectAsync(string project)
    {
        await StopAiWorkspaceAsync();
        _aiStore = await AiWorkspaceStore.LoadAsync(project);
        _aiStore.Changed += AiStoreChanged;
        _aiHistory = null;
        _aiPanel = await EnsureAiPanelAsync(_aiStore.Book.Active.Id);
    }
    private async Task StopAiWorkspaceAsync()
    {
        // Tab closing saves asynchronously after removing its view from the map.
        // Finish it before a project switch snapshots/disposes the remaining panels.
        if (_aiCloseTasks.Count > 0) await Task.WhenAll(_aiCloseTasks.ToArray());
        if (_aiHistory is not null) { await _aiHistory.CommitRenameAsync(); if (_aiHistory.Parent is Panel historyParent) historyParent.Children.Remove(_aiHistory); }
        foreach (var panel in _aiPanels.Values.Append(_aiPanel).Where(p => p is not null).Distinct().ToArray())
        {
            await panel.StopAndSaveAsync(); if (panel.Parent is Panel parent) parent.Children.Remove(panel);
            panel.Dispose();
        }
        _aiPanels.Clear(); _closingAi.Clear(); _deletingAi.Clear();
        if (_aiStore is not null) _aiStore.Changed -= AiStoreChanged;
        _aiStore = null;
    }
    private bool IsAiConversationOpen(string id) => _closingAi.Contains(id) || _workspaceTabs.Owner(TabWorkspace.AiConversationKey(id)) is not null;
    private async Task OpenAiConversationAsync(string id)
    {
        if (_aiStore is null || !_aiStore.Book.Conversations.Any(c=>c.Id==id)) return;
        if (_closingAi.Contains(id)) { Status("该会话正在关闭并保存，请稍后再打开。"); return; }
        if (_deletingAi.Contains(id)) { Status("该会话正在删除，无法打开。"); return; }
        try
        {
            var panel = await EnsureAiPanelAsync(id); SetAiTarget(panel);
            _aiStore.Book.ActiveConversationId = id;
            _workspaceTabs.Open(TabWorkspace.AiConversationKey(id)); RefreshWorkspaceViews();
            _aiHistory?.RefreshExternal(); await _aiStore.SaveAsync(); panel.Focus();
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
    private async Task DeleteAiConversationsAsync(IReadOnlyCollection<string> ids)
    {
        if (_aiStore is null) return;
        if(ids.Any(IsAiConversationOpen)) throw new InvalidOperationException("会话窗口已打开，关闭后才能删除。请先关闭对应的会话标签。");
        foreach (var id in ids) _deletingAi.Add(id);
        try
        {
            foreach(var id in ids)
                if(_aiPanels.Remove(id,out var panel)) { await panel.StopAndSaveAsync(); panel.Dispose(); }
            _aiStore.Book.Conversations.RemoveAll(c=>ids.Contains(c.Id));
            var active=_aiStore.Book.Active;
            if(!_aiPanels.Values.Contains(_aiPanel)) _aiPanel=await EnsureAiPanelAsync(active.Id);
            await _aiStore.SaveAsync(); RefreshAiAnnotations();
        }
        finally { foreach (var id in ids) _deletingAi.Remove(id); }
    }
    private async Task CloseAiPanelsAsync(IEnumerable<string> keys)
    {
        var ids = keys.Where(TabWorkspace.IsAiConversation).Select(key => key[(TabWorkspace.AiKey.Length + 1)..]).ToArray();
        _closingAi.UnionWith(ids);
        Exception? failure = null;
        try
        {
            foreach (var id in ids)
            {
                try
                {
                    if (!_aiPanels.Remove(id, out var panel)) continue;
                    try { await panel.StopAndSaveAsync(); }
                    finally
                    {
                        if (panel.Parent is Panel parent) parent.Children.Remove(panel);
                        panel.Dispose();
                        if (ReferenceEquals(panel, _aiPanel) && _aiStore is not null)
                            _aiPanel = _aiPanels.Values.FirstOrDefault() ?? await EnsureAiPanelAsync(_aiStore.Book.Active.Id);
                    }
                }
                catch (Exception ex) { failure ??= ex; }
            }
        }
        finally
        {
            foreach (var id in ids) _closingAi.Remove(id);
            _aiHistory?.RefreshExternal(); RefreshAiAnnotations();
        }
        if (failure is not null) throw failure;
    }
    private string AiTabTitle(string key) => AiConversationForKey(key)?.TabTitle ?? (key==TabWorkspace.AiHistoryKey ? "AI 会话历史" : key==TabWorkspace.AiSettingsKey ? "AI 设置" : "AI 对话");
    private AiConversation? AiConversationForKey(string key) => _aiStore?.Book.Conversations.FirstOrDefault(c=>TabWorkspace.AiConversationKey(c.Id)==key);
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
