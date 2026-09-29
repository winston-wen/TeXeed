using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TeXeed.Core;

public sealed partial class CompileService(bool disableMiKTeXInstaller = false)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object cancellationLock = new();
    private CancellationTokenSource? current;
    private static readonly string[] EngineNames = ["latexmk", "xelatex", "pdflatex", "lualatex", "tectonic"];
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task<Toolchain> DetectToolsAsync()
    {
        var directories = ToolDirectories();
        var engines = new List<ToolInfo>();
        foreach (var name in EngineNames)
        {
            var executable = FindTool(name, directories);
            if (executable is not null) engines.Add(new(name, executable));
        }
        await Task.CompletedTask;
        return new(engines, FindTool("synctex", directories));
    }

    public void Cancel()
    {
        lock (cancellationLock) current?.Cancel();
    }

    public async Task<CompileResult> CompileAsync(TexProject project, AppSettings settings, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (cancellationLock)
        {
            current?.Cancel();
            current = cancellation;
        }
        var acquired = false;
        var log = new StringBuilder();
        var engineName = settings.Engine.ToString();
        try
        {
            await gate.WaitAsync(cancellation.Token);
            acquired = true;
            cancellation.CancelAfter(Timeout);
            var token = cancellation.Token;
            if (!IsWithin(project.Root, project.MainPath) || !File.Exists(project.MainPath))
                throw new IOException("主文件不存在，或位于项目目录之外。请重新打开主 .tex 文件。");

            var toolchain = await DetectToolsAsync();
            var engine = ResolveEngine(settings, toolchain);
            engineName = engine.Name;
            var buildDirectory = Path.Combine(project.Root, ".texeed", "build");
            var previewDirectory = Path.Combine(project.Root, ".texeed", "preview");
            EnsureOrdinaryDirectory(project.Root, buildDirectory);
            EnsureOrdinaryDirectory(project.Root, previewDirectory);
            var job = Path.GetFileNameWithoutExtension(project.MainPath);
            var generatedPdf = Path.Combine(buildDirectory, job + ".pdf");
            var generatedLog = Path.Combine(buildDirectory, job + ".log");

            // An engine that exits successfully without producing a new PDF must never
            // cause a failed or stale build to be published. Aux files stay for speed.
            foreach (var extension in new[] { ".pdf", ".log", ".synctex", ".synctex.gz", ".fls", TexPageLayout.Extension })
                File.Delete(Path.Combine(buildDirectory, job + extension));

            // \include writes auxiliary files in matching subdirectories of the outdir.
            foreach (var file in project.Files.Where(f => f.Kind == ProjectFileKind.Tex))
            {
                var parent = Path.GetDirectoryName(Path.GetRelativePath(project.Root, file.Path));
                if (!string.IsNullOrEmpty(parent) && IsWithin(buildDirectory, Path.Combine(buildDirectory, parent)))
                    EnsureOrdinaryDirectory(project.Root, Path.Combine(buildDirectory, parent));
            }

            var directories = ToolDirectories(Path.GetDirectoryName(engine.Path));
            var env = CreateEnvironment(project.Root, directories);
            // Tectonic resolves project assets relative to the primary input. Keep its original entry;
            // its SyncTeX page container supplies the typeset width without moving that search root.
            var driver = engine.Name == "tectonic" ? project.MainPath : await PrepareLayoutDriverAsync(project, buildDirectory, token);
            var arguments = BuildArguments(engine.Name, driver, buildDirectory, toolchain);
            if (disableMiKTeXInstaller && engine.Name != "tectonic")
                arguments = [.. arguments[..^1], engine.Name == "latexmk" ? "-latexoption=--disable-installer" : "--disable-installer", arguments[^1]];
            log.AppendLine($"引擎：{engine.Name}  |  {engine.Path}");
            var directEngine = engine.Name is "xelatex" or "pdflatex" or "lualatex";
            var auxiliaryState = directEngine ? await AuxiliaryStateAsync(buildDirectory, token) : "";
            var bibliographyCachePath = Path.Combine(buildDirectory, job + ".texeed-bibliography-cache");
            var bibliographyCache = directEngine ? await ReadBibliographyCacheAsync(bibliographyCachePath, token) : null;
            BibliographyCache? pendingBibliographyCache = null;
            var processedBibliographies = new HashSet<string>(StringComparer.Ordinal);
            var result = new ProcessResult(-1, "");
            var success = false;
            for (var pass = 1; pass <= (directEngine ? 4 : 1); pass++)
            {
                // Every pass must create its own final products. A zero exit after a
                // later pass silently does nothing must not publish an earlier PDF.
                if (pass > 1)
                    foreach (var extension in new[] { ".pdf", ".log", ".synctex", ".synctex.gz", ".fls", TexPageLayout.Extension })
                        File.Delete(Path.Combine(buildDirectory, job + extension));
                result = await RunProcessAsync(engine.Path, arguments, project.Root, env, token);
                AppendRun(log, pass == 1 ? engine.Name : $"{engine.Name} · 第 {pass} 遍", result);
                success = result.ExitCode == 0;
                if (!success || !directEngine) break;

                var passLog = File.Exists(generatedLog) ? await ReadLimitedAsync(generatedLog, token) : result.Output;
                var nextAuxiliaryState = await AuxiliaryStateAsync(buildDirectory, token);
                var bibliography = await DescribeBibliographyAsync(project, buildDirectory, job, engine, directories, passLog, token);
                var ranBibliography = false;
                if (bibliography is not null)
                {
                    var cached = pendingBibliographyCache ?? bibliographyCache;
                    var bbl = Path.Combine(buildDirectory, job + ".bbl");
                    var outputSignature = await FileSignatureAsync(bbl, token);
                    var explicitlyRequested = RequestsBibliography(passLog, bibliography.Tool)
                        && !processedBibliographies.Contains(bibliography.Signature);
                    var cacheMatches = cached?.Inputs == bibliography.Signature && cached.Output == outputSignature
                        && outputSignature != "missing" && (bibliography.Cacheable || processedBibliographies.Contains(bibliography.Signature));
                    if (!cacheMatches || explicitlyRequested)
                    {
                        if (pass == 4) throw new IOException("文献数据在第 4 遍后仍发生变化；请检查日志中的循环引用或文献配置。");
                        var bibliographyPath = FindTool(bibliography.Tool, directories);
                        if (bibliographyPath is null)
                            throw new IOException($"文献处理需要 {bibliography.Tool}，但未找到该程序。请安装完整的 MiKTeX / TeX Live，或使用 latexmk。");
                        File.Delete(bbl);
                        var bibArguments = bibliography.Tool == "biber"
                            ? new[] { "--input-directory", buildDirectory, "--output-directory", buildDirectory, job }
                            : new[] { job };
                        var bibResult = await RunProcessAsync(bibliographyPath, bibArguments, buildDirectory, env, token);
                        AppendRun(log, bibliography.Tool, bibResult);
                        success = bibResult.ExitCode == 0;
                        if (!success) break;
                        outputSignature = await FileSignatureAsync(bbl, token);
                        if (outputSignature == "missing" || new FileInfo(bbl).Length == 0)
                            throw new IOException($"{bibliography.Tool} 没有生成有效的 .bbl 文献文件。");
                        pendingBibliographyCache = new(bibliography.Signature, outputSignature);
                        processedBibliographies.Add(bibliography.Signature);
                        ranBibliography = true;
                    }
                    else if (pass == 1) log.AppendLine($"文献输入未改变，复用 {bibliography.Tool} 结果。");
                }
                var anotherPass = ranBibliography || auxiliaryState != nextAuxiliaryState || RequestsLatexRerun(passLog);
                auxiliaryState = nextAuxiliaryState;
                if (!anotherPass)
                {
                    log.AppendLine($"辅助文件已稳定，完成 {pass} 遍编译。");
                    break;
                }
            }

            var diagnosticLog = result.Output;
            if (File.Exists(generatedLog))
            {
                var fileLog = await ReadLimitedAsync(generatedLog, token);
                diagnosticLog = fileLog;
                log.AppendLine("\n──── TeX 日志 ────").AppendLine(fileLog);
            }

            if (!success)
                return Failure(project, log.ToString(), engineName, watch.Elapsed);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(generatedPdf) || new FileInfo(generatedPdf).Length < 5)
                throw new IOException("编译器没有生成 PDF。请查看日志并检查引擎设置。");
            await using (var pdf = File.OpenRead(generatedPdf))
            {
                var header = new byte[5];
                if (await pdf.ReadAsync(header, token) != 5 || Encoding.ASCII.GetString(header) != "%PDF-")
                    throw new IOException("编译器的输出不是有效的 PDF 文件。");
            }

            // Publish to a separate directory only after ALL passes succeed. A bad
            // edit cannot destroy the PDF or SyncTeX map currently in the preview.
            var publishedPdf = Path.Combine(previewDirectory, job + ".pdf");
            var stagingPdf = publishedPdf + ".new";
            File.Copy(generatedPdf, stagingPdf, true);
            token.ThrowIfCancellationRequested();
            File.Move(stagingPdf, publishedPdf, true);
            var synctexAvailable = false;
            foreach (var extension in new[] { ".synctex", ".synctex.gz" })
            {
                var generated = Path.Combine(buildDirectory, job + extension);
                var published = Path.Combine(previewDirectory, job + extension);
                if (File.Exists(generated))
                {
                    File.Copy(generated, published, true);
                    synctexAvailable = true;
                }
                else File.Delete(published);
            }

            if (pendingBibliographyCache is not null)
                await WriteBibliographyCacheAsync(bibliographyCachePath, pendingBibliographyCache);

            var generatedLayout = Path.Combine(buildDirectory, job + TexPageLayout.Extension);
            var publishedLayout = Path.Combine(previewDirectory, job + TexPageLayout.Extension);
            if (File.Exists(generatedLayout)) File.Copy(generatedLayout, publishedLayout, true);
            else File.Delete(publishedLayout);

            return new(true, publishedPdf, log.ToString(), ParseDiagnostics(diagnosticLog, project), watch.Elapsed, engineName, synctexAvailable);
        }
        catch (OperationCanceledException)
        {
            log.AppendLine(ct.IsCancellationRequested || !acquired || !cancellation.IsCancellationRequested
                ? "编译已取消。" : "编译已停止（取消请求或超过 2 分钟时限）。");
            return Failure(project, log.ToString(), engineName, watch.Elapsed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            log.AppendLine(exception.Message);
            return Failure(project, log.ToString(), engineName, watch.Elapsed);
        }
        finally
        {
            lock (cancellationLock)
                if (ReferenceEquals(current, cancellation)) current = null;
            if (acquired) gate.Release();
        }
    }

    private static CompileResult Failure(TexProject project, string log, string engine, TimeSpan duration)
    {
        var diagnostics = ParseDiagnostics(log, project).ToList();
        if (!diagnostics.Any(d => !d.IsWarning))
        {
            var message = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "编译失败。";
            diagnostics.Add(new(message, project.MainPath, 1));
        }
        return new(false, null, log, diagnostics, duration, engine, false);
    }

    private static ToolInfo ResolveEngine(AppSettings settings, Toolchain tools)
    {
        var requestedName = EngineName(settings.Engine);
        if (!string.IsNullOrWhiteSpace(settings.EnginePath))
        {
            var path = Environment.ExpandEnvironmentVariables(settings.EnginePath.Trim().Trim('"'));
            path = Path.IsPathRooted(path) ? path : FindTool(path, ToolDirectories()) ?? path;
            if (!File.Exists(path)) throw new IOException($"编译器路径不存在：{path}");
            if (OperatingSystem.IsWindows() && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择引擎的 .exe 文件；不支持命令行脚本。");
            var name = requestedName ?? EngineNames.FirstOrDefault(n => Path.GetFileNameWithoutExtension(path).Equals(n, StringComparison.OrdinalIgnoreCase));
            if (name is null) throw new IOException("自定义编译器路径需要同时选择对应的编译引擎。");
            return new(name, Path.GetFullPath(path));
        }
        var engine = requestedName is null ? tools.Engines.FirstOrDefault() : tools.Engines.FirstOrDefault(e => e.Name == requestedName);
        return engine ?? throw new IOException(requestedName is null
            ? "未找到 LaTeX 编译器。请安装 MiKTeX / TeX Live / Tectonic，然后在设置中选择引擎或填写 .exe 路径。"
            : $"未找到 {requestedName}。请在设置中填写该引擎的 .exe 路径。");
    }

    private static string? EngineName(TexEngine engine) => engine switch
    {
        TexEngine.Latexmk => "latexmk", TexEngine.XeLaTeX => "xelatex", TexEngine.PdfLaTeX => "pdflatex",
        TexEngine.LuaLaTeX => "lualatex", TexEngine.Tectonic => "tectonic", _ => null
    };

    private static string[] BuildArguments(string engine, string mainPath, string output, Toolchain tools)
    {
        if (engine == "tectonic")
            return ["-X", "compile", "--untrusted", "--synctex", "--keep-logs", "--keep-intermediates", "--outdir", output, mainPath];
        if (engine == "latexmk")
            return ["-norc", tools.Engines.Any(e => e.Name == "xelatex") ? "-xelatex" : "-pdf", "-synctex=1", "-interaction=nonstopmode", "-file-line-error", "-halt-on-error", "-no-shell-escape", "-outdir=" + output, mainPath];
        return ["-synctex=1", "-recorder", "-interaction=nonstopmode", "-file-line-error", "-halt-on-error", "-no-shell-escape", "-output-directory=" + output, mainPath];
    }

    public static IReadOnlyList<Diagnostic> ParseDiagnostics(string log, TexProject project)
    {
        var result = new List<Diagnostic>();
        var lines = log.Replace("\r", "").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var located = Regex.Match(line, @"^(?:error:\s*)?(.+?\.(?:tex|sty|cls|bib)):(\d+):\s*(.+)$", RegexOptions.IgnoreCase);
            if (located.Success)
            {
                var path = ResolveDiagnosticPath(located.Groups[1].Value.Trim('"', ' ', '('), project);
                var number = int.TryParse(located.Groups[2].Value, out var value) ? Math.Max(1, value) : 1;
                var message = located.Groups[3].Value;
                result.Add(new(message, path, number, message.Contains("warning", StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (line.StartsWith('!'))
            {
                var number = 1;
                for (var look = index + 1; look < Math.Min(index + 8, lines.Length); look++)
                {
                    var context = Regex.Match(lines[look], @"^l\.(\d+)");
                    if (context.Success && int.TryParse(context.Groups[1].Value, out var found)) { number = Math.Max(1, found); break; }
                }
                result.Add(new(line.TrimStart('!', ' '), project.MainPath, number));
            }
            else if (Regex.IsMatch(line, @"^(?:LaTeX|Package\s+\S+|Class\s+\S+|pdfTeX)\s+Warning:|^(?:Over|Under)full \\[hv]box|^warning:", RegexOptions.IgnoreCase))
            {
                var fullMessage = line;
                for (var look = index + 1; look < Math.Min(index + 3, lines.Length) && !Regex.IsMatch(fullMessage, @"(?:line|lines)\s+\d+"); look++)
                {
                    if (string.IsNullOrWhiteSpace(lines[look]) || Regex.IsMatch(lines[look], @"^!|Warning:|^\(")) break;
                    fullMessage += " " + lines[look].Trim();
                }
                var atLine = Regex.Match(fullMessage, @"(?:input line|at lines?|on line)\s+(\d+)", RegexOptions.IgnoreCase);
                result.Add(new(fullMessage, project.MainPath, atLine.Success && int.TryParse(atLine.Groups[1].Value, out var number) ? Math.Max(1, number) : 1, true));
            }
        }
        return result.DistinctBy(d => (d.Path.ToUpperInvariant(), d.Line, d.Message, d.IsWarning)).Take(200).ToList();
    }

    private static string ResolveDiagnosticPath(string value, TexProject project)
    {
        try
        {
            var path = Path.GetFullPath(value, project.Root);
            if (IsWithin(project.Root, path)) return path;
        }
        catch (ArgumentException) { }
        return project.MainPath;
    }

    internal static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void EnsureOrdinaryDirectory(string root, string directory)
    {
        if (!IsWithin(root, directory)) throw new IOException("编译输出目录必须位于项目内。");
        var relative = Path.GetRelativePath(root, directory);
        var currentPath = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "." or "") continue;
            currentPath = Path.Combine(currentPath, segment);
            if (Directory.Exists(currentPath) && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("编译输出目录不能是符号链接或目录联接。");
            Directory.CreateDirectory(currentPath);
        }
    }

    internal static List<string> ToolDirectories(string? preferred = null)
    {
        var directories = new List<string>();
        if (!string.IsNullOrEmpty(preferred)) directories.Add(preferred);
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('"')));
        if (OperatingSystem.IsWindows())
        {
            foreach (var baseDirectory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0))
            {
                foreach (var name in new[] { "MiKTeX", "MiKTeX 2.9", Path.Combine("Programs", "MiKTeX") })
                    foreach (var architecture in new[] { "x64", "" })
                        directories.Add(Path.Combine(baseDirectory, name, "miktex", "bin", architecture));
            }
            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            foreach (var texLive in new[] { Path.Combine(drive + Path.DirectorySeparatorChar, "texlive"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "texlive") })
            {
                try
                {
                    if (Directory.Exists(texLive))
                        foreach (var version in Directory.EnumerateDirectories(texLive).OrderByDescending(x => x))
                            foreach (var architecture in new[] { "windows", "win32" })
                                directories.Add(Path.Combine(version, "bin", architecture));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        return directories.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    internal static string? FindTool(string name, IEnumerable<string> directories)
    {
        var fileName = OperatingSystem.IsWindows() && !Path.HasExtension(name) ? name + ".exe" : name;
        foreach (var directory in directories)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(directory, fileName));
                if (File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static Dictionary<string, string> CreateEnvironment(string root, IEnumerable<string> directories) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = string.Join(Path.PathSeparator, directories),
        ["TEXINPUTS"] = root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + Path.PathSeparator + (Environment.GetEnvironmentVariable("TEXINPUTS") ?? ""),
        ["BIBINPUTS"] = root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + Path.PathSeparator + (Environment.GetEnvironmentVariable("BIBINPUTS") ?? ""),
        ["BSTINPUTS"] = root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar + Path.PathSeparator + (Environment.GetEnvironmentVariable("BSTINPUTS") ?? ""),
        ["TECTONIC_UNTRUSTED_MODE"] = "1"
    };

    private static void AppendRun(StringBuilder log, string name, ProcessResult result)
    {
        log.AppendLine($"\n──── {name}（退出码 {result.ExitCode}）────").AppendLine(result.Output);
    }

    internal sealed record ProcessResult(int ExitCode, string Output);

    internal static async Task<ProcessResult> RunProcessAsync(string executable, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string>? environment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        // SyncTeX honors these variables by executing a command. Always request data.
        start.Environment.Remove("SYNCTEX_EDITOR");
        start.Environment.Remove("SYNCTEX_VIEWER");
        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var outputTask = ReadLimitedAsync(process.StandardOutput.BaseStream);
        var errorTask = ReadLimitedAsync(process.StandardError.BaseStream);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(ct);
            return new(process.ExitCode, await outputTask + await errorTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(string path, CancellationToken token)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await ReadLimitedAsync(stream, token);
    }

    private static async Task<string> ReadLimitedAsync(Stream stream, CancellationToken token = default)
    {
        const int maximum = 4 * 1024 * 1024;
        using var captured = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        var truncated = false;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var accepted = Math.Min(read, maximum - (int)captured.Length);
            if (accepted > 0) captured.Write(buffer, 0, accepted);
            if (accepted < read) truncated = true;
        }
        var bytes = captured.ToArray();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            // Windows TeX tools may emit ANSI filenames while Tectonic emits UTF-8.
            // Decode UTF-8 first, falling back to the actual system ANSI codepage.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = OperatingSystem.IsWindows() ? Encoding.GetEncoding((int)GetACP()).GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
        return truncated ? text + "\n[日志超过 4 MB，已截断]" : text;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
