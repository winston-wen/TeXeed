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
    private bool _aiMarking;
    private readonly Dictionary<LatexEditor, AiAnnotationLayer> _sourceAnnotations = [];
    private readonly Dictionary<PdfPreview, AiAnnotationLayer> _pdfAnnotationLayers = [];
    private void InitializeAi()
    {
        _aiSettings = new AiSettingsPanel(_aiConnection); _aiPanel = CreateAiPanel();
        RegisterPdfAi(InitialPreview);
        foreach (var editor in Editors)
        {
            var annotations = new AiAnnotationLayer(editor, () => _aiPanel.AnnotationEntries,
                entry => _project is null ? null : editor.AnnotationPosition(entry, _project.Root), entry=>_aiPanel.RemoveAnnotation(entry), margin: true, confirm: entry=>_aiPanel.ConfirmAnnotation(entry), commit: () => _aiPanel.SaveAnnotationChanges());
            _sourceAnnotations[editor] = annotations;
            editor.AiMarked += (_, mark) => CollectSourceMark(editor, mark);
            editor.PreviewMouseRightButtonDown += (_, e) => editor.CaptureAnnotationContext(e.GetPosition(editor.TextArea.TextView));
            var menu = new ContextMenu();
            var annotate = new MenuItem { Header = "批注到当前会话" };
            menu.Opened += (_, _) => annotate.IsEnabled = _project is not null && editor.CurrentFilePath is not null && !_transitioning;
            annotate.Click += (_, _) => AnnotateSource(editor);
            menu.Items.Add(annotate); menu.Items.Add(new Separator());
            void Command(string title, RoutedCommand command, string gesture)
            {
                // Avoid global CanExecute requery opening the shared Windows clipboard.
                var item = new MenuItem { Header = title, InputGestureText = gesture };
                menu.Opened += (_, _) => { try { item.IsEnabled = command.CanExecute(null, editor.TextArea); } catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException) { item.IsEnabled = false; } };
                item.Click += (_, _) => { try { command.Execute(null, editor.TextArea); } catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException) { Status("剪贴板暂时无法访问，请重试。"); } };
                menu.Items.Add(item);
            }
            Command("撤销", ApplicationCommands.Undo, "Ctrl+Z"); Command("重做", ApplicationCommands.Redo, "Ctrl+Y"); menu.Items.Add(new Separator());
            Command("剪切", ApplicationCommands.Cut, "Ctrl+X"); Command("复制", ApplicationCommands.Copy, "Ctrl+C"); Command("粘贴", ApplicationCommands.Paste, "Ctrl+V"); Command("全选", ApplicationCommands.SelectAll, "Ctrl+A"); menu.Items.Add(new Separator());
            foreach (var (title, action) in new[] { ("切换注释", "comment"), ("增加缩进", "indent"), ("减少缩进", "outdent"), ("查找", "find"), ("替换", "replace"), ("格式化文档", "format") })
            { var item = new MenuItem { Header = title }; item.Click += (_, _) => editor.ExecuteCommand(action); menu.Items.Add(item); }
            editor.ContextMenu = menu;
        }
    }
    private void RegisterPdfAi(PdfPreview view)
    {
        var layer = new AiAnnotationLayer(view, () => _aiPanel.AnnotationEntries, view.AnnotationPosition, entry => _aiPanel.RemoveAnnotation(entry), confirm: entry => _aiPanel.ConfirmAnnotation(entry), commit: () => _aiPanel.SaveAnnotationChanges());
        _pdfAnnotationLayers[view] = layer;
        view.AiSelectionConfirmed += (_, entry) => { if (_aiPanel.AddEntry(entry, false)) layer.Edit(entry); };
        view.AiMarkingChanged += (_, active) =>
        {
            if (active) CancelOtherMarkingModes(keepPdf: view);
            if (ReferenceEquals(view, Preview)) _aiMarking = active;
            Status(active ? "PDF AI 圈点：拖动选择区域；Esc 退出" : "已退出 PDF AI 圈点");
        };
    }
    private void RemovePdfAi(PdfPreview view)
    { if (_pdfAnnotationLayers.Remove(view, out var layer)) layer.Dispose(); }
    private void SetAiMarking(bool active)
    {
        if (active && (_project is null || _transitioning)) return;
        if (!active) { CancelOtherMarkingModes(); return; }
        if (!Preview.IsAiSelecting) Preview.SetAiMarking(true);
    }
    private void CancelOtherMarkingModes(LatexEditor? keepEditor = null, PdfPreview? keepPdf = null, AiPanel? keepAnswer = null)
    {
        foreach (var editor in Editors)
            if (!ReferenceEquals(editor, keepEditor) && editor.IsAiMarking) editor.SetAiMarking(false);
        foreach (var preview in ExistingPreviews)
            if (!ReferenceEquals(preview, keepPdf) && preview.IsAiSelecting) preview.SetAiMarking(false);
        foreach (var panel in _aiPanels.Values.Append(_aiPanel).Where(panel => panel is not null).Distinct())
            if (!ReferenceEquals(panel, keepAnswer)) panel.CancelAnswerMarking();
        _aiMarking = keepPdf is not null && ReferenceEquals(keepPdf, Preview) && keepPdf.IsAiSelecting;
    }
    private void AnnotateSource(LatexEditor editor)
    {
        if (_project is null || _transitioning || editor.CurrentFilePath is not { } path || editor.Document is null) return;
        var segments = editor.TextArea.Selection.Segments.Where(segment => segment.Length > 0).ToArray();
        CancelOtherMarkingModes(keepEditor: editor);
        if (segments.Length == 0)
        {
            editor.SetAiMarking(true); editor.TextArea.Focus();
            Status("源码 AI 批注：拖动选择文字；Esc 退出");
            return;
        }
        if (segments.Sum(segment => (long)segment.Length) > 40000) { Status("每个源码选区最多 4 万字符，请缩小选区。"); return; }
        var revision = AiStorage.Hash(editor.Text);
        var entry = new AiEntry { Title = $"{Path.GetFileName(path)} · 源码批注" };
        foreach (var segment in segments)
            entry.Sources.Add(new AiSourceBlock(Path.GetRelativePath(_project.Root, path), editor.Document.GetLineByOffset(segment.StartOffset).LineNumber,
                editor.Document.GetLineByOffset(segment.EndOffset - 1).LineNumber, segment.StartOffset, editor.Document.GetText(segment.StartOffset, segment.Length), revision));
        if (_aiPanel.AddEntry(entry, false)) _sourceAnnotations[editor].Edit(entry);
    }
    private void CollectSourceMark(LatexEditor editor, SourceAiMark mark)
    {
        if (_project is null || editor.CurrentFilePath is not { } path || mark.IsPoint || mark.Length <= 0 || mark.Length > 40000 || mark.Offset < 0 || mark.Offset + mark.Length > editor.Document.TextLength) return;
        var line = editor.Document.GetLineByOffset(mark.Offset).LineNumber;
        var endLine = editor.Document.GetLineByOffset(mark.Offset + Math.Max(0, mark.Length - 1)).LineNumber;
        var entry = new AiEntry { Title = $"{Path.GetFileName(path)}:{line} · 源码批注" };
        entry.Sources.Add(new AiSourceBlock(Path.GetRelativePath(_project.Root, path), line, endLine,
            mark.Offset, editor.Document.GetText(mark.Offset, mark.Length), AiStorage.Hash(editor.Text)));
        if (_aiPanel.AddEntry(entry, false)) _sourceAnnotations[editor].Edit(entry);
    }
    private async void Ai_Click(object sender, RoutedEventArgs e) { HideMainMenu(false); await OpenNewAiConversationAsync(); }
    private void OpenAiTab()
    {
        if (_project is null) { Status("请先打开项目，再使用 AI。"); return; }
        if (_aiStore is not null) _ = OpenAiConversationAsync(_aiPanel.ConversationId);
    }
    private void OpenAiSettings()
    {
        if (_project is null) { ShowWelcomeSettings(); return; }
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
