using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;
public partial class MainWindow
{
    private AiPanel _aiPanel = null!;
    private void InitializeAi()
    {
        _aiSettings = new AiSettingsPanel(_aiConnection); _aiPanel = CreateAiPanel();
        foreach (var editor in Editors)
        {
            var menu = new ContextMenu();
            void Command(string title, RoutedCommand command, string gesture)
            {
                // Avoid global CanExecute requery opening the shared Windows clipboard.
                var item = new MenuItem { Header = title, InputGestureText = gesture };
                menu.Opened += (_, _) => { try { item.IsEnabled = command.CanExecute(null, editor.TextArea); } catch (System.Runtime.InteropServices.COMException) { item.IsEnabled = false; } };
                item.Click += (_, _) => { try { command.Execute(null, editor.TextArea); } catch (System.Runtime.InteropServices.COMException) { Status("剪贴板暂时被其他程序占用，请重试。"); } };
                menu.Items.Add(item);
            }
            Command("撤销", ApplicationCommands.Undo, "Ctrl+Z"); Command("重做", ApplicationCommands.Redo, "Ctrl+Y"); menu.Items.Add(new Separator());
            Command("剪切", ApplicationCommands.Cut, "Ctrl+X"); Command("复制", ApplicationCommands.Copy, "Ctrl+C"); Command("粘贴", ApplicationCommands.Paste, "Ctrl+V"); Command("全选", ApplicationCommands.SelectAll, "Ctrl+A"); menu.Items.Add(new Separator());
            foreach (var (title, action) in new[] { ("切换注释", "comment"), ("增加缩进", "indent"), ("减少缩进", "outdent"), ("查找", "find"), ("替换", "replace"), ("格式化文档", "format") })
            { var item = new MenuItem { Header = title }; item.Click += (_, _) => editor.ExecuteCommand(action); menu.Items.Add(item); }
            editor.ContextMenu = menu;
        }
        InitializeProjectComments();
    }
    private async void Ai_Click(object sender, RoutedEventArgs e) { HideMainMenu(false); await OpenNewAiConversationAsync(); }
    private void OpenAiTab()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        if (_aiStore is not null) _ = OpenAiConversationAsync(_aiPanel.ConversationId);
    }
    private void OpenAiSettings()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        _workspaceTabs.Open(TabWorkspace.AiSettingsKey);
        RefreshWorkspaceViews();
    }
}
