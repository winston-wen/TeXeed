using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (dialog.ShowDialog() == true) AddAttachments(dialog.FileNames);
    }

    private void AttachFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择附件文件夹" };
        if (dialog.ShowDialog() == true) AddAttachments([dialog.FolderName]);
    }

    private void ComposerAttachmentPaste(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = true; AddAttachments(Clipboard.GetFileDropList().Cast<string>()); return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                e.Handled = true;
                var entry = new AiEntry { Title = "粘贴的图片" };
                if (!AiAnnotationImages.Add(entry, bitmap, out var error)) { StatusText.Text = error; return; }
                if (AddEntry(entry)) StatusText.Text = "图片已添加到引用与附件。";
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { e.Handled = true; StatusText.Text = "无法读取剪贴板：" + ex.Message; }
    }

    internal bool AddAttachments(IEnumerable<string> paths)
    {
        try
        {
            var files = new List<AiAttachedFile>(); long total = 0;
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path);
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                    foreach (var item in Directory.EnumerateFiles(root, "*", options))
                        AddFile(item, Path.GetFileName(root) + "/" + Path.GetRelativePath(root, item).Replace('\\', '/'));
                }
                else AddFile(path, Path.GetFileName(path));
            }
            if (files.Count == 0) { StatusText.Text = "没有可添加的文件。"; return false; }
            var entry = new AiEntry { Title = files.Count == 1 ? "附件：" + files[0].Name : $"附件：{files.Count} 个文件", Files = files };
            if (!AddEntry(entry)) return false;
            StatusText.Text = "附件已添加到引用与附件。"; return true;

            void AddFile(string path, string name)
            {
                if (files.Count >= 50) throw new InvalidOperationException("一次最多添加 50 个文件。");
                var length = new FileInfo(path).Length;
                total += length;
                if (length > 8 * 1024 * 1024 || total > 16 * 1024 * 1024) throw new InvalidOperationException("单个文件最多 8 MB，本次附件最多 16 MB。");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", ".pdf" => "application/pdf", ".txt" or ".md" or ".tex" or ".bib" or ".sty" or ".cls" or ".json" or ".csv" or ".xml" or ".html" or ".py" or ".js" or ".cs" => "text/plain", _ => throw new InvalidOperationException($"暂不支持附件类型：{name}") };
                files.Add(new(name, mime, Convert.ToBase64String(File.ReadAllBytes(path))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { StatusText.Text = "无法添加附件：" + ex.Message; return false; }
    }
}
