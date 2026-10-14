using System.IO;
using System.Text;
using System.Text.Json;
using TeXeed.App.AI;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool _aiToolBusy;
    private bool _aiShellRunning;
    private CompileResult? _lastCompileResult;

    internal AiToolRunner CreateAiToolRunner()
        => CreateAiToolRunner(_aiPanel.Permission);

    internal AiToolRunner CreateAiToolRunner(AiPermission permission)
    {
        var session = _session;
        var project = _project ?? throw new InvalidOperationException("请先打开项目。");
        var readVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backupId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

        void CheckSession(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_session != session || _project?.MainPath != project.MainPath || _transitioning || _closing)
                throw new OperationCanceledException("项目已切换或正在关闭，本轮工具已停止。", token);
        }

        async Task<ProjectFile> ReadFile(string relative, CancellationToken token, bool allowBuildMetadataRead = false)
        {
            var path = ValidateAiPath(project.Root, relative, allowBuildMetadataRead);
            var current = CurrentFiles().FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (current is { IsEditable: true }) return current;
            var disk = await AiWorkspaceFiles.ReadAsync(project.Root, relative, token);
            CheckSession(token);
            // Reading from disk yields the UI thread; newly opened/edited buffers take precedence.
            return CurrentFiles().FirstOrDefault(f => f.IsEditable && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) ?? disk;
        }

        void RetainSource(ProjectFile file)
        {
            var previous = _project!.Files.FirstOrDefault(f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase));
            if (previous is null)
                _project = _project with { Files = [.. _project.Files, file] };
            else if (!previous.IsEditable)
                _project = _project with { Files = _project.Files.Select(f => f == previous ? file : f).ToArray() };
            // Unreferenced files stay in the project's saved catalog through their source tab.
            // Preserve the active chat/editor and split focus while adding that tab.
            var leftActive = _workspaceTabs.Active(TabSide.Left); var rightActive = _workspaceTabs.Active(TabSide.Right); var focus = _workspaceTabs.FocusedSide;
            _workspaceTabs.Open(file.Path);
            if (leftActive is not null) _workspaceTabs.Select(TabSide.Left, leftActive);
            if (rightActive is not null) _workspaceTabs.Select(TabSide.Right, rightActive);
            _workspaceTabs.Focus(focus);
        }

        async Task<string> Execute(string name, string arguments, CancellationToken token)
        {
            CheckSession(token);
            using var json = JsonDocument.Parse(arguments); var args = json.RootElement;
            name = name.Split('.').Last();
            if (!AiPermissionChoice.Allows(permission, name))
                throw new UnauthorizedAccessException("当前会话权限不允许此工具：" + name);
            if (name == "shell")
            {
                var command = args.GetProperty("command").GetString() ?? "";
                var workingDirectory = args.GetProperty("working_directory").GetString() ?? "";
                var timeout = args.GetProperty("timeout_seconds").GetInt32();
                if (permission == AiPermission.AskBeforeShell &&
                    !await Controls.AiShellApproval.RequestAsync(this, command, string.IsNullOrWhiteSpace(workingDirectory) ? project.Root : Path.GetFullPath(Path.Combine(project.Root, workingDirectory)), token))
                    return "操作被用户拒绝：此命令未执行，不得换写法绕过拒绝。";
                CheckSession(token);
                while (_building || _aiToolBusy || _refreshing) { await Task.Delay(50, token); CheckSession(token); }
                _aiToolBusy = true;
                _aiShellRunning = true;
                var editorStates = Editors.Select(e => (Editor: e, Enabled: e.IsEnabled)).ToArray();
                var leftActive = _workspaceTabs.Active(TabSide.Left); var rightActive = _workspaceTabs.Active(TabSide.Right); var focus = _workspaceTabs.FocusedSide;
                foreach (var state in editorStates) state.Editor.IsEnabled = false;
                try
                {
                    if (!await SaveAsync(reportErrors: false) || _changes.Count > 0)
                        throw new InvalidOperationException("当前源码尚未安全保存，Shell 未执行；请先处理保存冲突。");
                    CheckSession(token);
                    return await AiShell.ExecuteAsync(project.Root, command, workingDirectory, timeout, token);
                }
                finally
                {
                    _aiShellRunning = false;
                    try
                    {
                        if (_session == session && !_closing && !_transitioning)
                        {
                            _externalChangePending = true;
                            await ReloadExternalAsync();
                            if (leftActive is not null) _workspaceTabs.Select(TabSide.Left, leftActive);
                            if (rightActive is not null) _workspaceTabs.Select(TabSide.Right, rightActive);
                            _workspaceTabs.Focus(focus); RefreshTabs();
                            readVersions.Clear(); // Shell may have changed any source on disk.
                        }
                    }
                    finally
                    {
                        foreach (var state in editorStates) state.Editor.IsEnabled = state.Enabled;
                        _aiToolBusy = false;
                    }
                }
            }
            var files = CurrentFiles().Where(f => f.IsEditable).ToArray();
            if (name == "list_files")
            {
                var catalog = await Task.Run(() => AiWorkspaceFiles.Discover(project.Root, ".", AiWorkspaceFiles.MaximumDepth, token), token);
                CheckSession(token);
                var warnings = _project!.Warnings.Count == 0 ? "无载入警告。" : "载入警告：\n" + string.Join('\n', _project.Warnings.Take(40));
                return $"项目根目录：{project.Root}\n主文件：{Path.GetRelativePath(project.Root, project.MainPath)} · 编译引擎：{_settings.Engine}\n{warnings}\n" +
                    AiWorkspaceFiles.Format(catalog, CurrentFiles(), compact: true);
            }
            if (name == "list_directory")
            {
                var relative = args.GetProperty("path").GetString() ?? ".";
                var depth = Math.Clamp(args.GetProperty("depth").GetInt32(), 1, AiWorkspaceFiles.MaximumDepth);
                var offset = Math.Max(0, args.GetProperty("offset").GetInt32());
                var catalog = await Task.Run(() => AiWorkspaceFiles.Discover(project.Root, relative, depth, token), token);
                CheckSession(token);
                return AiWorkspaceFiles.Format(catalog, CurrentFiles(), relative, offset);
            }
            if (name == "search_source")
            {
                var query = args.GetProperty("query").GetString() ?? "";
                var result = await Task.Run(() => AiWorkspaceFiles.SearchAsync(project.Root, query, files, token), token);
                CheckSession(token);
                return result;
            }
            if (name is "read_source" or "replace_source")
            {
                var relative = args.GetProperty("file").GetString() ?? "";
                var file = await ReadFile(relative, token, allowBuildMetadataRead: name == "read_source");
                var hash = AiStorage.Hash(file.Content);
                if (name == "read_source")
                {
                    readVersions[file.Path] = hash;
                    return AiResponses.ExecuteTool(name, arguments, [file]);
                }
                if (!AiWorkspaceFiles.IsSource(file.Path) && !files.Any(f => string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("README、构建脚本和其他项目资料仅可读取；修改工具仅用于 TeX、BibTeX 和本地样式源码。");
                if (!readVersions.TryGetValue(file.Path, out var read) || read != hash || args.GetProperty("expected_sha256").GetString() != hash)
                    throw new InvalidOperationException("源码已改变或尚未读取，请重新 read_source 后修改；未覆盖当前编辑。");
                var before = args.GetProperty("old_text").GetString() ?? "";
                var after = args.GetProperty("new_text").GetString() ?? "";
                if (before.Length == 0 || before.Length + after.Length > 160_000)
                    throw new InvalidOperationException("替换需要非空的定位原文，每次修改总计最多 16 万字符，请拆分。");
                // Models normally emit LF even when the file uses CRLF. Preserve the file's newline convention.
                string Normalize(string text) => file.Content.Contains("\r\n") ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text.Replace("\r\n", "\n");
                before = Normalize(before); after = Normalize(after);
                var offset = file.Content.IndexOf(before, StringComparison.Ordinal);
                if (offset < 0 || file.Content.IndexOf(before, offset + 1, StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException("定位原文必须准确匹配且只出现一次，请扩大上下文后重试。");
                if (before == after) return "内容相同，无需修改。";
                if (!backedUp.Contains(file.Path))
                {
                    var directory = Path.Combine(AiStorage.Home, "backups", backupId);
                    Directory.CreateDirectory(directory);
                    var backup = Path.Combine(directory, AiStorage.Hash(file.Path)[..12] + "-" + file.Name);
                    await File.WriteAllTextAsync(backup, file.Content, new UTF8Encoding(false), token);
                    await File.WriteAllTextAsync(backup + ".json", JsonSerializer.Serialize(new { OriginalPath = file.Path, SourceSHA256 = hash }), token);
                    CheckSession(token);
                    if ((await ReadFile(file.RelativePath, token)).Content != file.Content)
                        throw new InvalidOperationException("保存备份期间源码发生变化，请重新读取后再修改。");
                }
                // A save already in flight owns the source catalog snapshot. Do not let
                // its completion discard a newly imported file or its pending edits.
                await _saveGate.WaitAsync(token);
                try
                {
                    CheckSession(token);
                    if ((await ReadFile(file.RelativePath, token)).Content != file.Content)
                        throw new InvalidOperationException("等待保存期间源码发生变化，请重新读取后再修改。");
                    if (!_project!.Files.Any(f => f.IsEditable && string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase))) RetainSource(file);
                    var editor = Editors.FirstOrDefault(e => e.HasFileSession(file.Path)) ?? LeftEditor;
                    editor.ApplyAiReplacement(file, offset, before.Length, after);
                    backedUp.Add(file.Path);
                    var updated = file.Content.Remove(offset, before.Length).Insert(offset, after);
                    if (_project!.Files.First(f => f.Path == file.Path).Content == updated) _changes.Remove(file.Path); else _changes[file.Path] = updated;
                    _revision++; CancelForwardSync(); _syncTimer.Stop();
                    RefreshTabs(); _indexTimer.Stop(); _indexTimer.Start();
                    if (_settings.LivePreview) ScheduleLiveBuild();
                    readVersions[file.Path] = AiStorage.Hash(updated);
                }
                finally { _saveGate.Release(); }
                return $"已修改 {file.RelativePath}（编辑器可撤销；备份 {backupId}）\nSHA256={readVersions[file.Path]}\n尚需保存或编译以验证修改。";
            }
            if (name == "create_source")
            {
                var relative = args.GetProperty("file").GetString() ?? "";
                var path = ValidateAiPath(project.Root, relative);
                if (Path.GetExtension(path).ToLowerInvariant() is not (".tex" or ".bib" or ".sty" or ".cls")) throw new InvalidOperationException("仅支持新建 TeX、BibTeX 和本地样式源码。");
                var content = args.GetProperty("content").GetString() ?? "";
                if (content.Length > 80_000) throw new InvalidOperationException("新建文件最多 8 万字符，请分步创建。");
                await _saveGate.WaitAsync(token);
                try
                {
                    CheckSession(token); ValidateAiPath(project.Root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
                    RetainSource(new ProjectFile(path, Path.GetRelativePath(project.Root, path), AiWorkspaceFiles.Kind(path), content));
                    _revision++; RefreshTabs(); RefreshIndex();
                }
                finally { _saveGate.Release(); }
                return $"已创建 {relative}，请按需在主文件中引用并编译。\nSHA256={AiStorage.Hash(content)}";
            }
            if (name is "save_project" or "compile_project")
            {
                while (_building || _aiToolBusy) { await Task.Delay(50, token); CheckSession(token); }
                _aiToolBusy = true;
                try
                {
                    CheckSession(token);
                    if (name == "save_project")
                    {
                        var saved = await SaveAsync(reportErrors: false);
                        CheckSession(token);
                        return !saved ? "保存未完成，请检查文件冲突。" : _changes.Count > 0 ? "保存期间又有新的编辑，仍有未保存内容，请再次保存。" : "已保存项目修改。";
                    }
                    var entryFile = args.TryGetProperty("entry_file", out var entryValue) ? entryValue.GetString() ?? "" : "";
                    _lastCompileResult = null;
                    _lastCompileEntryPath = null; _lastCompileRevision = -1; _lastCompileBuildEpoch = -1; _lastCompilePreviewPublished = false;
                    await BuildAsync(true, token, reportErrors: false, entryFile: entryFile);
                    CheckSession(token);
                    if (_lastCompileResult is not { } result) return "编译未执行或保存未完成，请检查文件冲突并重试。";
                    var current = _lastCompileRevision == _revision && _lastCompileBuildEpoch == _buildEpoch && _changes.Count == 0 && _lastCompileDependenciesCurrent;
                    var target = Path.GetRelativePath(project.Root, _lastCompileEntryPath ?? project.MainPath);
                    var defaultEntry = Path.GetRelativePath(project.Root, project.MainPath);
                    var alternate = !string.Equals(_lastCompileEntryPath, project.MainPath, StringComparison.OrdinalIgnoreCase);
                    var summary = result.Success && current && _lastCompilePreviewPublished
                        ? $"编译成功，入口：{target}，PDF 预览已更新。"
                        : !current || result.Success && !_lastCompilePreviewPublished ? $"编译结果已过期或未发布，入口：{target}，需要重新编译。"
                        : $"编译失败，入口：{target}，请根据诊断继续修复。";
                    var scope = alternate ? $"本次仅验证指定入口。当前默认入口仍为 {defaultEntry}；此结果不能证明默认入口能够独立编译。" : $"验证的是当前默认入口 {defaultEntry}。";
                    var preview = _pdfPath is null ? "当前尚无 PDF 预览。" : $"当前 PDF 预览：{Path.GetRelativePath(project.Root, _pdfPath)}；对应入口：{Path.GetRelativePath(project.Root, (_previewProject ?? _project!).MainPath)}。";
                    var diagnostics = string.Join('\n', result.Diagnostics.Select(d => $"{(d.IsWarning ? "警告" : "错误")} {(string.IsNullOrWhiteSpace(d.Path) ? "主文件" : Path.GetRelativePath(project.Root, d.Path))}:{d.Line} {d.Message}"));
                    var log = result.Log.Length > 16000 ? result.Log[..4000] + "\n[中间日志省略]\n" + result.Log[^12000..] : result.Log;
                    return $"{summary}\n{scope}\n{preview}\n引擎：{result.Engine}\n诊断范围：以下仅为本次编译输出；本工具未作修改前后对比，不能据此认定警告原有或与本次修改无关。\n{diagnostics}\n{log}";
                }
                finally { _aiToolBusy = false; }
            }
            throw new InvalidOperationException("未知的项目工具。");
        }
        return async (name, arguments, token) => await Dispatcher.InvokeAsync(() => Execute(name, arguments, token)).Task.Unwrap();
    }

    internal static string ValidateAiPath(string root, string relative, bool allowBuildMetadataRead = false)
    {
        var components = relative.Replace('\\', '/').Split('/');
        var metadata = allowBuildMetadataRead && (AiWorkspaceFiles.IsBuildMetadataFile(relative) || AiWorkspaceFiles.IsBuildMetadataDirectory(relative));
        if (Path.IsPathRooted(relative) || components.Any(c => c is "" or "." or ".." || c.StartsWith('.') && !metadata || c.Contains(':') || c.EndsWith(' ') || c.EndsWith('.')))
            throw new UnauthorizedAccessException("路径必须是项目内的普通相对源码路径。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var inside = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(inside) || inside.StartsWith(".." + Path.DirectorySeparatorChar)) throw new UnauthorizedAccessException("不能操作项目外的文件。");
        var current = root;
        foreach (var component in components)
        {
            var stem = component.Split('.')[0].ToUpperInvariant();
            if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                throw new UnauthorizedAccessException("文件名无效或是系统保留名称。");
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("不能通过符号链接读取或写入文件。");
        }
        return path;
    }
}
