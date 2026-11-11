using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private async void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (dialog.ShowDialog() == true) await AddAttachmentsAsync(dialog.FileNames);
    }

    private async void AttachFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择附件文件夹" };
        if (dialog.ShowDialog() == true) await AddAttachmentsAsync([dialog.FolderName]);
    }

    private async void ComposerAttachmentPaste(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                e.Handled = true; await AddAttachmentsAsync(paths); return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                e.Handled = true;
                var entry = new AiEntry { Title = "粘贴的图片", EntryKind = "attachment" };
                if (!AiAnnotationImages.Add(entry, bitmap, out var error)) { StatusText.Text = error; return; }
                if (AddEntry(entry)) StatusText.Text = "图片已添加到附件。";
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { e.Handled = true; StatusText.Text = "无法读取剪贴板：" + ex.Message; }
    }

    internal async Task<bool> AddAttachmentsAsync(IEnumerable<string> paths)
    {
        var conversation = _book.Active;
        var pathSnapshot = paths.ToArray();
        StatusText.Text = "正在读取附件…";
        try
        {
            var files = await Task.Run(() => ReadAttachmentFiles(pathSnapshot));
            if (_disposed) return false;
            if (!ReferenceEquals(_book.Active, conversation)) { StatusText.Text = "会话已切换，请在目标会话中重新添加附件。"; return false; }
            if (files.Count == 0) { StatusText.Text = "没有可添加的文件。"; return false; }
            var entry = new AiEntry { Title = files.Count == 1 ? "附件：" + files[0].Name : $"附件：{files.Count} 个文件", EntryKind = "attachment", Files = files };
            if (!AddEntry(entry)) return false;
            StatusText.Text = "附件已添加到附件列表。"; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { if (!_disposed) StatusText.Text = "无法添加附件：" + ex.Message; return false; }
    }

    private static List<AiAttachedFile> ReadAttachmentFiles(IEnumerable<string> paths)
    {
            var files = new List<AiAttachedFile>(); long total = 0;
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path);
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                    foreach (var item in Directory.EnumerateFiles(root, "*", options))
                        AddFile(item, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) + "/" + Path.GetRelativePath(root, item).Replace('\\', '/'), root);
                }
                else AddFile(path, Path.GetFileName(path), null);
            }
            return files;

            void AddFile(string path, string name, string? folder)
            {
                if (files.Count >= 50) throw new InvalidOperationException("一次最多添加 50 个文件。");
                var length = new FileInfo(path).Length;
                total += length;
                if (length > 8 * 1024 * 1024 || total > 16 * 1024 * 1024) throw new InvalidOperationException("单个文件最多 8 MB，本次附件最多 16 MB。");
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", ".pdf" => "application/pdf", ".txt" or ".md" or ".tex" or ".bib" or ".sty" or ".cls" or ".json" or ".csv" or ".xml" or ".html" or ".py" or ".js" or ".cs" => "text/plain", _ => throw new InvalidOperationException($"暂不支持附件类型：{name}") };
                var bytes = new byte[checked((int)length)];
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new IOException($"读取时文件大小发生变化：{name}");
                files.Add(new(name, mime, Convert.ToBase64String(bytes)) { FolderPath = folder });
            }
    }

    internal void RemoveAttachedFile(AiEntry entry, AiAttachedFile file)
    {
        if (!_book.Entries.Contains(entry) || entry.State == "提交中") return;
        if (!entry.Files.Remove(file)) return;
        if (entry.Files.Count == 0 && entry.Images.Count == 0 && entry.AnswerAnchor is null && entry.Sources.Count == 0 && entry.PdfBlocks.Count == 0 && string.IsNullOrWhiteSpace(entry.Question))
            RemoveAnnotation(entry);
        else { UpdateAttachmentTitle(entry); Rebind(); Dirty(); }
    }

    internal void RemoveAttachmentItem(AiEntry entry, AiAttachmentItem item)
    {
        if (!_book.Entries.Contains(entry) || entry.State == "提交中") return;
        var removed = false;
        foreach (var file in item.Files) removed |= entry.Files.Remove(file);
        if (!removed) return;
        if (entry.Files.Count == 0 && entry.Images.Count == 0 && entry.AnswerAnchor is null && entry.Sources.Count == 0 && entry.PdfBlocks.Count == 0 && string.IsNullOrWhiteSpace(entry.Question))
            RemoveAnnotation(entry);
        else { UpdateAttachmentTitle(entry); Rebind(); Dirty(); }
    }

    private static void UpdateAttachmentTitle(AiEntry entry)
    {
        if (entry.HasReference || !(entry.EntryKind == "attachment" || entry.Title.StartsWith("附件：", StringComparison.Ordinal))) return;
        var items = entry.AttachmentItems;
        entry.Title = items.Count switch
        {
            0 when entry.HasAttachmentImages => "粘贴的图片",
            1 => "附件：" + items[0].DisplayPath,
            > 1 => $"附件：{items.Count} 项",
            _ => entry.Title
        };
    }

    private void RemoveAttachmentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AiAttachmentItem item, Tag: AiEntry entry }) RemoveAttachmentItem(entry, item);
    }

    private void RemoveAttachedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AiAttachedFile file, Tag: AiEntry entry }) RemoveAttachedFile(entry, file);
    }
}
