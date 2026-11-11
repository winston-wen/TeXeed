using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    // This is the format emitted by the project tree in MainWindow.FilePriority.cs.
    private const string ProjectTreeDragFormat = "TeXeed.ProjectFile";

    private void InitializeComposerDrop()
    {
        Composer.AllowDrop = true;
        Composer.PreviewDragOver += ComposerDragOver;
        Composer.PreviewDrop += ComposerDrop;
    }

    internal static bool TryComposerDropPaths(IDataObject data, out string[] paths)
    {
        if (data.GetDataPresent(ProjectTreeDragFormat, false))
        {
            paths = data.GetData(ProjectTreeDragFormat, false) is string path && path.Length > 0 ? [path] : [];
            return true;
        }
        if (data.GetDataPresent(DataFormats.FileDrop, false))
        {
            paths = data.GetData(DataFormats.FileDrop, false) as string[] ?? [];
            return true;
        }
        paths = [];
        return false;
    }

    private void ComposerDragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (!TryComposerDropPaths(e.Data, out var paths)) return;
            e.Effects = paths.Length == 0 ? DragDropEffects.None :
                (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy :
                (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }
        catch (ExternalException) { e.Effects = DragDropEffects.None; e.Handled = true; }
    }

    private async void ComposerDrop(object sender, DragEventArgs e)
    {
        string[] paths;
        try { if (!TryComposerDropPaths(e.Data, out paths)) return; }
        catch (ExternalException ex) { e.Handled = true; StatusText.Text = "无法读取拖放内容：" + ex.Message; return; }
        e.Handled = true;
        await AddComposerDropAsync(paths, (e.KeyStates & DragDropKeyStates.ShiftKey) != 0);
    }

    internal async Task<bool> AddComposerDropAsync(IEnumerable<string> paths, bool shift)
    {
        if (_project is null) { StatusText.Text = "请先打开项目。"; return false; }
        var conversation = _book.Active;
        var root = ProjectRoot?.Invoke() ?? Path.GetDirectoryName(_project);
        var selected = paths.ToArray();
        if (selected.Length == 0) { StatusText.Text = "拖放内容中没有可读取的文件或文件夹。"; return false; }
        StatusText.Text = "正在读取拖放的附件…";
        try
        {
            var token = _lifetime.Token;
            var result = await Task.Run(() => ReadComposerDrop(selected, root, shift, token), token);
            if (_disposed) return false;
            if (!ReferenceEquals(_book.Active, conversation)) { StatusText.Text = "会话已切换，请在目标会话中重新拖放附件。"; return false; }
            var notices = result.Notices.Count == 0 ? "" : " " + string.Join("；", result.Notices.Take(10)) +
                (result.Notices.Count > 10 ? $"；另有 {result.Notices.Count - 10} 项未添加。" : "。");
            if (result.Files.Count == 0) { StatusText.Text = "没有可添加的文件或文件夹。" + notices; return false; }
            var entry = new AiEntry { Title = result.Files.Count == 1 ? "附件：" + result.Files[0].Name : $"附件：{result.Files.Count} 项", EntryKind = "attachment", Files = result.Files };
            if (!AddEntry(entry, focusComposer: false)) return false;
            StatusText.Text = "附件已添加到附件列表。" + notices; return true;
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText.Text = "已取消读取拖放的附件。"; return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { if (!_disposed) StatusText.Text = "无法添加拖放的附件：" + ex.Message; return false; }
    }

    private static AttachmentReadResult ReadComposerDrop(IEnumerable<string> paths, string? root, bool shift, CancellationToken token)
    {
        var files = new List<AiAttachedFile>(); var notices = new List<string>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (shift && File.Exists(fullPath) && IsProjectDropFile(root, fullPath))
                    files.Add(new(fullPath, "application/vnd.texeed.file-reference", "") { ReferenceKind = "file", ReferencePath = fullPath });
                else
                {
                    var result = ReadAttachmentFiles([fullPath], token);
                    files.AddRange(result.Files); notices.AddRange(result.Notices);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            { notices.Add($"无法添加 {path}：{ex.Message}"); }
        }
        return new(files, notices);
    }

    private static bool IsProjectDropFile(string? root, string fullPath)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
