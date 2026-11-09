using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.AI;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class MainWindow
{
    private static string RenamedPath(string root, string previous, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || name.EndsWith('.') || name.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(COM|LPT)[1-9](\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("请输入有效的文件名，不包含路径分隔符或 Windows 保留名称。");
        var relative = Path.GetRelativePath(root, previous);
        if (ProjectExplorerSettings.Resolve(root, relative) is null || !File.Exists(previous)) throw new IOException("文件已不存在或不属于当前目录。");
        for (var current = previous; !current.Equals(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能重命名符号链接中的文件。");
        var next = Path.Combine(Path.GetDirectoryName(previous)!, name);
        if (!next.Equals(previous, StringComparison.OrdinalIgnoreCase) && (File.Exists(next) || Directory.Exists(next))) throw new IOException("同名文件或文件夹已存在。");
        return next;
    }
    private void ShowExplorerRename(string path)
    {
        if (_project is null || _transitioning || _closing) return;
        var input = new TextBox { Text = Path.GetFileName(path), MinWidth = 300 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var accept = new Button { Content = "确定", IsDefault = true }; var cancel = new Button { Content = "取消", IsCancel = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; actions.Children.Add(cancel); actions.Children.Add(accept);
        var content = new StackPanel { Margin = new Thickness(20) }; content.Children.Add(input); content.Children.Add(error); content.Children.Add(actions);
        var dialog = new Window { Owner = this, Title = "重命名", Content = content, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowTheme.Attach(dialog);
        accept.Click += (_, _) =>
        {
            try { RenamedPath(_project.Root, path, input.Text); dialog.DialogResult = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error.Text = ex.Message; }
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.Select(0, Path.GetFileNameWithoutExtension(input.Text).Length); };
        if (dialog.ShowDialog() == true) _ = RenameExplorerFileAsync(path, input.Text);
    }
    private async Task RenameExplorerFileAsync(string previous, string name)
    {
        if (_project is null || _transitioning || _closing) return;
        if (_aiShellRunning || _aiPanels.Values.Any(panel => panel.HasActiveOperation)) { Status("AI 正在工作，请完成或停止后再重命名文件。"); return; }
        string next;
        try { next = RenamedPath(_project.Root, previous, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Status(ex.Message); return; }
        if (next == previous) return;
        var root = _project.Root; var isMain = previous.Equals(_project.MainPath, StringComparison.OrdinalIgnoreCase);
        var priorityLocked = false; var notes = new List<string>();
        _transitioning = true; foreach (var editor in Editors) editor.IsEnabled = false;
        try
        {
            await PauseWorkAsync();
            await _priorityGate.WaitAsync(); priorityLocked = true;
            if (isMain && _aiStore is not null)
            {
                await _aiStore.SaveAsync(); var snapshot = _aiStore.Book.Snapshot();
                await Task.Run(() =>
                {
                    var target = AiStorage.NotebookPath(next);
                    if (File.Exists(target)) File.Copy(target, target + ".rename-backup-" + Guid.NewGuid().ToString("N"));
                    AiStorage.Save(next, snapshot);
                });
            }
            await Task.Run(() => File.Move(previous, next));
            _project = _project with
            {
                MainPath = isMain ? next : _project.MainPath,
                Files = _project.Files.Select(file => file.Path.Equals(previous, StringComparison.OrdinalIgnoreCase) ? file with { Path = next, RelativePath = Path.GetRelativePath(root, next) } : file).ToArray()
            };
            if (_changes.Remove(previous, out var text)) _changes[next] = text;
            _workspaceTabs.RenameSource(previous, next);
            foreach (var editor in Editors) editor.RenameFileSession(previous, next);
            if (isMain)
            {
                if (_aiStore is not null) _aiStore.Project = next;
                foreach (var panel in _aiPanels.Values) panel.RetargetProject(next);
                _settings = _settings with { RecentProjects = _settings.RecentProjects.Select(path => path.Equals(previous, StringComparison.OrdinalIgnoreCase) ? next : path).ToArray() };
                PersistSettings(); UpdateRecent();
            }
            if (_aiStore is not null)
                foreach (var conversation in _aiStore.Book.Conversations)
                    foreach (var entry in conversation.Entries)
                        entry.Sources = entry.Sources.Select(source => source.File.Replace('\\', '/').Equals(Path.GetRelativePath(root, previous).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ? source with { File = Path.GetRelativePath(root, next) } : source).ToList();
            foreach (var layer in _sourceAnnotations.Values) layer.CloseEditor(); _pdfAnnotations?.CloseEditor();
            _revision++; _pdfRevision = -1; _previewProject = null; CancelPendingNavigation();
            _priorityFiles = _priorityFiles.Select(path => path.Equals(previous, StringComparison.OrdinalIgnoreCase) ? next : path).ToArray();
            try { await Task.Run(() => ProjectExplorerSettings.Save(root, _priorityFiles)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add("优先显示设置暂未保存：" + ex.Message); }
            if (_aiStore is not null)
                try { await _aiStore.SaveAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add("会话设置暂未保存：" + ex.Message); }
            _fileNodeCache.Clear(); _displayedDirectoryEntries = null; _fileTreeQuery = null;
            _fileCatalogTask = LoadDirectoryCatalogAsync(root); await _fileCatalogTask;
            RefreshPriorityFiles(); RefreshTabs(); RefreshIndex();
            Status("已重命名：" + name + (notes.Count > 0 ? "；" + string.Join("；", notes) : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Status("重命名失败：" + ex.Message); }
        finally
        {
            if (priorityLocked) _priorityGate.Release();
            _transitioning = false; foreach (var editor in Editors) editor.IsEnabled = true; QueueExternalRefresh();
            if (_settings.LivePreview && _changes.Count > 0) ScheduleLiveBuild();
        }
    }
}
