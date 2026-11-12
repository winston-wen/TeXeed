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
    private sealed class FormAttachmentReadBatch { internal int Count; internal bool Failed; }
    private readonly Dictionary<AiConversation, FormAttachmentReadBatch> _pendingFormAttachmentReads = [];

    private bool HasPendingFormAttachmentReads(AiConversation conversation) => _pendingFormAttachmentReads.ContainsKey(conversation);

    private bool BlockPendingFormAttachmentRead(AiConversation conversation)
    {
        if (!HasPendingFormAttachmentReads(conversation)) return false;
        StatusText.Text = "正在读取表单附件，请等待读取完成后再发送或排队。";
        return true;
    }

    private async void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (dialog.ShowDialog() == true) await AddAttachmentsAsync(dialog.FileNames);
    }

    private void ReferenceFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要引用的文件" };
        if (dialog.ShowDialog() == true) AddFileReferences(dialog.FileNames);
    }

    internal bool AddFileReferences(IEnumerable<string> paths, AiFormAttachmentOrigin? origin = null, Func<bool>? stillAvailable = null)
    {
        try
        {
            var files = new List<AiAttachedFile>();
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到引用文件。", fullPath);
                files.Add(new(fullPath, "application/vnd.texeed.file-reference", "")
                { ReferenceKind = "file", ReferencePath = fullPath });
            }
            if (files.Count == 0) { StatusText.Text = "没有可引用的文件。"; return false; }
            if (stillAvailable?.Invoke() == false) { StatusText.Text = "会话已切换或表单已提交，请在目标会话中重新添加附件。"; return false; }
            var entry = new AiEntry { Title = files.Count == 1 ? "文件引用：" + files[0].Name : $"文件引用：{files.Count} 个文件", EntryKind = "attachment", Files = files };
            if (origin is not null) AiFormAttachmentOrigins.Set(entry, origin);
            if (!AddEntry(entry)) return false;
            StatusText.Text = "文件路径已添加到附件列表。"; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { if (!_disposed) StatusText.Text = "无法引用文件：" + ex.Message; return false; }
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

    internal async Task<bool> AddAttachmentsAsync(IEnumerable<string> paths, AiFormAttachmentOrigin? origin = null, Func<bool>? stillAvailable = null,
        Func<CancellationToken, Task>? beforeFormRead = null)
    {
        var conversation = _book.Active;
        if (origin is not null && _queueDispatching && ReferenceEquals(conversation, _queueDispatchConversation))
        { StatusText.Text = "排队消息正在发送，请稍后再添加表单附件。"; return false; }
        var book = _book;
        var pathSnapshot = paths.ToArray();
        FormAttachmentReadBatch? batch = null;
        if (origin is not null)
        {
            if (!_pendingFormAttachmentReads.TryGetValue(conversation, out batch)) _pendingFormAttachmentReads[conversation] = batch = new();
            batch.Count++;
        }
        StatusText.Text = "正在读取附件…";
        var added = false;
        try
        {
            if (origin is not null && beforeFormRead is not null) await beforeFormRead(_lifetime.Token);
            var read = await Task.Run(() => ReadAttachmentFiles(pathSnapshot, _lifetime.Token));
            if (_disposed) return false;
            if (!ReferenceEquals(_book.Active, conversation) || stillAvailable?.Invoke() == false) { StatusText.Text = "会话已切换或表单已提交，请在目标会话中重新添加附件。"; return false; }
            var files = read.Files;
            var notices = read.Notices.Count == 0 ? "" : " " + string.Join("；", read.Notices.Take(10)) + (read.Notices.Count > 10 ? $"；另有 {read.Notices.Count - 10} 项未添加。" : "。");
            if (files.Count == 0) { StatusText.Text = "没有可添加的文件。" + notices; return false; }
            var entry = new AiEntry { Title = files.Count == 1 ? "附件：" + files[0].Name : $"附件：{files.Count} 个文件", EntryKind = "attachment", Files = files };
            if (origin is not null) AiFormAttachmentOrigins.Set(entry, origin);
            if (!AddEntry(entry)) return false;
            added = true;
            StatusText.Text = "附件已添加到附件列表。" + notices; return true;
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText.Text = "已取消读取附件。"; return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { if (!_disposed) StatusText.Text = "无法添加附件：" + ex.Message; return false; }
        finally
        {
            if (batch is not null)
            {
                batch.Failed |= !added;
                if (--batch.Count == 0)
                {
                    if (!_disposed && !_followUpsClosing && ReferenceEquals(_book, book) && ReferenceEquals(book.Active, conversation))
                        await PauseQueueForFormAttachmentAsync(book, !batch.Failed);
                    if (batch.Count == 0) _pendingFormAttachmentReads.Remove(conversation);
                }
            }
        }
    }

    private sealed record AttachmentReadResult(List<AiAttachedFile> Files, List<string> Notices);

    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yaml", "yml", "toml", "ini", "cfg", "conf", "config", "env", "properties", "js", "jsx", "ts", "tsx", "mjs", "cjs", "mts", "cts", "py", "pyw", "rb", "go", "rs", "java", "kt", "kts", "scala", "c", "h", "cpp", "hpp", "cc", "cxx", "cs", "fs", "fsx", "swift", "php", "pl", "pm", "lua", "r", "jl", "ex", "exs", "erl", "hrl", "clj", "cljs", "cljc", "elm", "hs", "ml", "mli", "v", "sv", "vhd", "vhdl", "asm", "s", "html", "htm", "xhtml", "xml", "svg", "css", "scss", "sass", "less", "vue", "svelte", "astro", "sh", "bash", "zsh", "fish", "ps1", "psm1", "psd1", "bat", "cmd", "csv", "tsv", "sql", "graphql", "gql", "prisma", "md", "mdx", "markdown", "rst", "txt", "text", "rtf", "tex", "latex", "bib", "sty", "cls", "org", "adoc", "asciidoc", "makefile", "cmake", "gradle", "dockerfile", "containerfile", "vagrantfile", "rakefile", "gemfile", "podfile", "fastfile", "brewfile", "procfile", "lock", "sum", "log", "diff", "patch", "gitignore", "gitattributes", "editorconfig", "prettierrc", "eslintrc", "babelrc", "npmrc", "nvmrc", "yarnrc"
    };
    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    { "license", "readme", "changelog", "authors", "contributors", "copying" };
    private static readonly HashSet<string> TextMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/json", "application/xml", "application/javascript", "application/typescript", "application/x-javascript", "application/x-typescript", "application/x-yaml", "application/yaml", "application/x-sh", "application/x-shellscript", "application/sql", "application/graphql", "application/toml", "application/x-toml" };

    private static string? RegisteredMediaType(string extension)
    {
        if (extension.Length == 0) return null;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("." + extension);
            return key?.GetValue("Content Type") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static AttachmentReadResult ReadAttachmentFiles(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
            var files = new List<AiAttachedFile>(); var notices = new List<string>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(path))
                    {
                        var root = Path.GetFullPath(path);
                        var folder = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                        files.Add(new(folder, "application/vnd.texeed.directory-reference", "")
                        { FolderPath = folder, ReferenceKind = "directory", ReferencePath = folder });
                    }
                    else AddFile(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
                { notices.Add($"无法添加 {path}：{ex.Message}"); }
            }
            return new(files, notices);

            void AddFile(string path)
            {
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                var registered = RegisteredMediaType(ext);
                var mime = ext switch { "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "webp" => "image/webp", "gif" => "image/gif", "pdf" => "application/pdf", _ when TextFileExtensions.Contains(ext) || TextFileNames.Contains(name) || TextFileExtensions.Contains(name) => "text/plain", _ => registered?.ToLowerInvariant() switch { "image/png" => "image/png", "image/jpeg" => "image/jpeg", "image/webp" => "image/webp", "image/gif" => "image/gif", "application/pdf" => "application/pdf", var value when value is not null && (value.StartsWith("text/", StringComparison.Ordinal) || TextMediaTypes.Contains(value)) => "text/plain", _ => "" } };
                if (mime.Length == 0) { notices.Add($"不支持的文件 {name}"); return; }
                var length = new FileInfo(path).Length;
                if (length == 0) { notices.Add($"已跳过空文件 {name}"); return; }
                var (id, storedLength) = AiAttachmentBlobStore.Store(path, cancellationToken);
                if (storedLength == 0) { notices.Add($"已跳过空文件 {name}"); return; }
                files.Add(new(name, mime, "") { BlobId = id });
            }
    }

    internal void RemoveAttachedFile(AiEntry entry, AiAttachedFile file)
    {
        if (!_book.Entries.Contains(entry) || entry.State == "提交中") return;
        if (!entry.Files.Remove(file)) return;
        InvalidateAttachmentFeedback(entry);
        if (entry.Files.Count == 0 && entry.Images.Count == 0 && entry.AnswerAnchor is null && entry.Sources.Count == 0 && entry.PdfBlocks.Count == 0 && string.IsNullOrWhiteSpace(entry.Question))
            RemoveAnnotation(entry);
        else { UpdateAttachmentTitle(entry); Rebind(); Dirty(); }
        RefreshFormEnabled();
    }

    internal void RemoveAttachmentItem(AiEntry entry, AiAttachmentItem item)
    {
        if (!_book.Entries.Contains(entry) || entry.State == "提交中") return;
        var removed = false;
        foreach (var file in item.Files) removed |= entry.Files.Remove(file);
        if (!removed) return;
        InvalidateAttachmentFeedback(entry);
        if (entry.Files.Count == 0 && entry.Images.Count == 0 && entry.AnswerAnchor is null && entry.Sources.Count == 0 && entry.PdfBlocks.Count == 0 && string.IsNullOrWhiteSpace(entry.Question))
            RemoveAnnotation(entry);
        else { UpdateAttachmentTitle(entry); Rebind(); Dirty(); }
        RefreshFormEnabled();
    }

    private static void UpdateAttachmentTitle(AiEntry entry)
    {
        if (entry.HasReference || !(entry.EntryKind == "attachment" || entry.Title.StartsWith("附件：", StringComparison.Ordinal) || entry.Title.StartsWith("文件引用：", StringComparison.Ordinal))) return;
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
