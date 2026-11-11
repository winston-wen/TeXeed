using System.Windows;
using System.Windows.Controls;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _aiRelationViews = [];
    private readonly Dictionary<string, string> _aiRelationVersions = [];

    private void EnsureAiRelations(string id)
    {
        var key = TabWorkspace.AiRelationsKey(id);
        if (_aiStore?.Book.Conversations.Any(c => c.Id == id) != true) return;
        var version = string.Join("\u001e", _aiStore.Book.Conversations.Select(c => c.Id + "\u001f" + c.ParentConversationId + "\u001f" + c.Title));
        if (_aiRelationViews.ContainsKey(key) && _aiRelationVersions.GetValueOrDefault(key) == version) return;
        RemoveAiRelations(key);
        var panel = EnsureAiPanel(id);
        var tree = panel.CreateConversationRelations(conversation => _ = OpenAiConversationAsync(conversation));
        var title = new TextBlock { Text = "会话关系", FontSize = 17, Margin = new Thickness(20, 16, 20, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var header = new TextBlock { Text = "双击打开会话", FontSize = 12, Margin = new Thickness(20, 4, 20, 0) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var layout = new DockPanel();
        var heading = new StackPanel(); heading.Children.Add(title); heading.Children.Add(header);
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading); layout.Children.Add(tree);
        _aiRelationViews.Add(key, layout);
        _aiRelationVersions[key] = version;
    }

    private void OpenAiRelations(string id)
    {
        if (_aiStore?.Book.Conversations.Any(c => c.Id == id) != true) return;
        var key = TabWorkspace.AiRelationsKey(id);
        EnsureAiRelations(id);
        _workspaceTabs.Open(key); RefreshWorkspaceViews();
    }

    private void RemoveAiRelations(string key)
    {
        if (!_aiRelationViews.Remove(key, out var view)) return;
        _aiRelationVersions.Remove(key);
        if (view.Parent is Panel parent) parent.Children.Remove(view);
    }
}
