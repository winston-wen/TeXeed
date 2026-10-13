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
    private readonly AiPanel _aiPanel = new();
    private void InitializeAi()
    {
        _aiPanel.ProjectFiles = () => CurrentFiles().Where(f => f.IsEditable).ToArray();
        Preview.AiSelectionConfirmed += (_, entry) => { OpenAiTab(); _aiPanel.AddEntry(entry); };
        foreach (var editor in Editors)
        {
            var menu = new ContextMenu();
            var ai = new MenuItem { Header = "选取内容用于 AI 问答" }; ai.Click += (_, _) => CollectSourceForAi(editor); menu.Items.Add(ai); menu.Items.Add(new Separator());
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
            menu.Opened += (_, _) => ai.IsEnabled = editor.CurrentFilePath is not null && !editor.TextArea.Selection.IsEmpty && !_transitioning;
            editor.ContextMenu = menu;
        }
    }
    private void Ai_Click(object sender, RoutedEventArgs e) { HideMainMenu(false); OpenAiTab(); }
    private void OpenAiTab()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        _workspaceTabs.Open(TabWorkspace.AiKey); RefreshWorkspaceViews();
    }
    private void CollectSourceForAi(LatexEditor editor)
    {
        if (_project is null || editor.CurrentFilePath is not { } path || editor.Document is null) return;
        var segments = editor.TextArea.Selection.Segments.Where(s => s.Length > 0).ToArray();
        if (segments.Length == 0) return;
        if (segments.Sum(s => s.Length) > 40000) { Status("每个源码选区最多 4 万字符，请缩小选区。"); return; }
        var entry = new AiEntry { Title = $"{Path.GetFileName(path)} · {segments.Length} 个选区" }; var revision = AiStorage.Hash(editor.Text);
        foreach (var segment in segments)
            entry.Sources.Add(new AiSourceBlock(Path.GetRelativePath(_project.Root, path), editor.Document.GetLineByOffset(segment.StartOffset).LineNumber,
                editor.Document.GetLineByOffset(segment.EndOffset - 1).LineNumber, segment.StartOffset, editor.Document.GetText(segment.StartOffset, segment.Length), revision));
        OpenAiTab(); _aiPanel.AddEntry(entry);
    }
}
