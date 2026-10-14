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
    private bool _aiMarking;
    private void InitializeAi()
    {
        _aiPanel.SettingsRequested += (_, _) => OpenAiSettings();
        _aiPanel.ProjectFiles = () => CurrentFiles().Where(f => f.IsEditable).ToArray();
        _aiPanel.CreateToolRunner = CreateAiToolRunner;
        Preview.AiSelectionConfirmed += (_, entry) => { OpenAiTab(); _aiPanel.AddEntry(entry); };
        Preview.AiMarkingChanged += (_, active) => SetAiMarking(active);
        foreach (var editor in Editors)
        {
            var menu = new ContextMenu();
            editor.AiMarked += (_, mark) => CollectSourceMark(editor, mark);
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
            _ = new AiMarkMenu(editor, () => _aiMarking, () => SetAiMarking(false));
        }
    }
    private void SetAiMarking(bool active)
    {
        if (active && (_project is null || _transitioning)) return;
        if (_aiMarking == active) return;
        _aiMarking = active;
        UpdateWorkspaceIcons();
        foreach (var editor in Editors) editor.SetAiMarking(active);
        Preview.SetAiMarking(active);
        Status(active ? "AI 圈点：单击点位，按住选择；Esc 或右键双击退出" : "已退出 AI 圈点");
    }
    private void AiMarking_Click(object sender, RoutedEventArgs e) => SetAiMarking(!_aiMarking);
    private void CollectSourceMark(LatexEditor editor, SourceAiMark mark)
    {
        if (_project is null || editor.CurrentFilePath is not { } path || mark.Length > 40000) return;
        var line = editor.Document.GetLineByOffset(mark.Offset).LineNumber;
        var endLine = editor.Document.GetLineByOffset(mark.Offset + Math.Max(0, mark.Length - 1)).LineNumber;
        var entry = new AiEntry { Title = $"{Path.GetFileName(path)}:{line} · {(mark.IsPoint ? "点位" : "句子")}", IsPoint = mark.IsPoint };
        entry.Sources.Add(new AiSourceBlock(Path.GetRelativePath(_project.Root, path), line, endLine,
            mark.Offset, editor.Document.GetText(mark.Offset, mark.Length), AiStorage.Hash(editor.Text)));
        OpenAiTab();
        _aiPanel.AddEntry(entry);
    }
    private void Ai_Click(object sender, RoutedEventArgs e) { HideMainMenu(false); OpenAiTab(); }
    private void OpenAiTab()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        _workspaceTabs.Open(TabWorkspace.AiKey); RefreshWorkspaceViews();
    }
    private void OpenAiSettings()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        _workspaceTabs.Open(TabWorkspace.AiSettingsKey);
        RefreshWorkspaceViews();
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
