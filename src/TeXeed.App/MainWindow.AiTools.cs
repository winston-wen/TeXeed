using System.IO;
using System.Text;
using System.Text.Json;
using TeXeed.App.AI;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool _aiToolBusy;
    private CompileResult? _lastCompileResult;

    internal AiToolRunner CreateAiToolRunner()
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

        async Task<string> Execute(string name, string arguments, CancellationToken token)
        {
            CheckSession(token);
            using var json = JsonDocument.Parse(arguments); var args = json.RootElement;
            name = name.Split('.').Last();
            var files = CurrentFiles().Where(f => f.IsEditable).ToArray();
            if (name == "list_files")
                return $"主文件：{Path.GetRelativePath(project.Root, project.MainPath)} · 编译引擎：{_settings.Engine}\n" + AiResponses.ExecuteTool(name, arguments, files);
            if (name == "search_source") return AiResponses.ExecuteTool(name, arguments, files);
            if (name is "read_source" or "replace_source")
            {
                var relative = args.GetProperty("file").GetString() ?? "";
                var file = files.FirstOrDefault(f => string.Equals(f.RelativePath.Replace('\\', '/'), relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("文件不在当前项目的可编辑源码中，请先 list_files。");
                ValidateAiPath(project.Root, file.RelativePath);
                var hash = AiStorage.Hash(file.Content);
                if (name == "read_source")
                {
                    readVersions[file.Path] = hash;
                    return AiResponses.ExecuteTool(name, arguments, files);
                }
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
                    if (CurrentFiles().First(f => f.Path == file.Path).Content != file.Content)
                        throw new InvalidOperationException("保存备份期间源码发生变化，请重新读取后再修改。");
                    backedUp.Add(file.Path);
                }
                CheckSession(token);
                var editor = Editors.FirstOrDefault(e => e.HasFileSession(file.Path)) ?? LeftEditor;
                editor.ApplyAiReplacement(file, offset, before.Length, after);
                var updated = file.Content.Remove(offset, before.Length).Insert(offset, after);
                if (_project!.Files.First(f => f.Path == file.Path).Content == updated) _changes.Remove(file.Path); else _changes[file.Path] = updated;
                _revision++; CancelForwardSync(); _syncTimer.Stop();
                RefreshTabs(); _indexTimer.Stop(); _indexTimer.Start();
                if (_settings.LivePreview) ScheduleLiveBuild();
                readVersions[file.Path] = AiStorage.Hash(updated);
                return $"已修改 {file.RelativePath}（编辑器可撤销；备份 {backupId}）\nSHA256={readVersions[file.Path]}\n尚需保存或编译以验证修改。";
            }
            if (name == "create_source")
            {
                var relative = args.GetProperty("file").GetString() ?? "";
                var path = ValidateAiPath(project.Root, relative);
                if (Path.GetExtension(path).ToLowerInvariant() is not (".tex" or ".bib" or ".sty" or ".cls")) throw new InvalidOperationException("仅支持新建 TeX、BibTeX 和本地样式源码。");
                var content = args.GetProperty("content").GetString() ?? "";
                if (content.Length > 80_000) throw new InvalidOperationException("新建文件最多 8 万字符，请分步创建。");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
                var kind = Path.GetExtension(path).ToLowerInvariant() switch { ".bib" => ProjectFileKind.Bibliography, ".sty" or ".cls" => ProjectFileKind.Style, _ => ProjectFileKind.Tex };
                _project = _project! with { Files = [.. _project!.Files, new ProjectFile(path, Path.GetRelativePath(project.Root, path), kind, content)] };
                // Keep unreferenced new sources in the project catalog after saving, without hiding chat.
                var active = _workspaceTabs.Active(TabSide.Left); var focus = _workspaceTabs.FocusedSide;
                _workspaceTabs.Open(path);
                if (active is not null) _workspaceTabs.Select(TabSide.Left, active);
                _workspaceTabs.Focus(focus);
                _revision++; RefreshTabs(); RefreshIndex();
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
                    var revision = _revision;
                    _lastCompileResult = null;
                    await BuildAsync(true, token, reportErrors: false);
                    CheckSession(token);
                    if (_lastCompileResult is not { } result) return "编译未执行或保存未完成，请检查文件冲突并重试。";
                    var current = revision == _revision && _changes.Count == 0;
                    var diagnostics = string.Join('\n', result.Diagnostics.Select(d => $"{(d.IsWarning ? "警告" : "错误")} {(string.IsNullOrWhiteSpace(d.Path) ? "主文件" : Path.GetRelativePath(project.Root, d.Path))}:{d.Line} {d.Message}"));
                    var log = result.Log.Length > 16000 ? result.Log[..4000] + "\n[中间日志省略]\n" + result.Log[^12000..] : result.Log;
                    return $"{(result.Success && current ? "编译成功，PDF 预览已更新。" : !current ? "编译期间源码已改变，结果已过期，需要重新编译。" : "编译失败，请根据诊断继续修复。") }\n引擎：{result.Engine}\n{diagnostics}\n{log}";
                }
                finally { _aiToolBusy = false; }
            }
            throw new InvalidOperationException("未知的项目工具。");
        }
        return async (name, arguments, token) => await Dispatcher.InvokeAsync(() => Execute(name, arguments, token)).Task.Unwrap();
    }

    internal static string ValidateAiPath(string root, string relative)
    {
        var components = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || components.Any(c => c is "" or "." or ".." || c.StartsWith('.') || c.Contains(':') || c.EndsWith(' ') || c.EndsWith('.')))
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
