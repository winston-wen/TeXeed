using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeXeed.App.AI;
using TeXeed.Core;

namespace TeXeed.App.Controls;

/// <summary>Standalone LaTeX → DVI → SVG, coalesced and compiled away from the WPF dispatcher.</summary>
internal static class ChatTexRenderer
{
    private sealed class Request(string source, bool display, string home, string path)
    {
        internal readonly string Source = source, Home = home, Path = path;
        internal readonly bool Display = display;
        internal readonly TaskCompletionSource<ChatSvg> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DateTime FailedAt;
        internal void Fail(Exception error) { FailedAt = DateTime.UtcNow; Completion.TrySetException(error); }
    }
    private sealed class TexSyntaxException(string message) : Exception(message);
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, Request> Cache = [];
    private static readonly Queue<Request> Pending = new();
    private static readonly SemaphoreSlim Workers = new(2);
    private static bool _draining;
    // Keep the existing cache keys: batching does not change the TeX preamble or styles.
    private const string Revision = "latex-svg-v1";
    internal static Task<ChatSvg> RenderAsync(string source, bool display)
    {
        var home = Path.Combine(AiStorage.Home, "math-svg");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + "\n" + display + "\n" + source)));
        var path = Path.Combine(home, hash + ".svg");
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var existing) &&
                (!existing.Completion.Task.IsFaulted || DateTime.UtcNow - existing.FailedAt < TimeSpan.FromSeconds(30)))
                return existing.Completion.Task;
            if (Cache.Count >= 512)
                foreach (var key in Cache.Where(pair => pair.Value.Completion.Task.IsCompleted).Take(128).Select(pair => pair.Key).ToArray()) Cache.Remove(key);
            var request = new Request(source, display, home, path);
            Cache[path] = request;
            _ = Task.Run(() => LoadOrQueue(request));
            return request.Completion.Task;
        }
    }
    private static void LoadOrQueue(Request request)
    {
        try
        {
            ChatTexInput.Validate(request.Source);
            // Disk hits never wait for a TeX process slot, including while a long answer compiles.
            if (File.Exists(request.Path))
            {
                try { request.Completion.TrySetResult(ChatSvg.Read(request.Path)); return; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { File.Delete(request.Path); }
            }
            lock (CacheLock)
            {
                Pending.Enqueue(request);
                if (!_draining) { _draining = true; _ = DrainAsync(); }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { request.Fail(ex); }
    }
    private static async Task DrainAsync()
    {
        // Gather formulas created by one Markdown render/stream update, without delaying cache hits.
        await Task.Delay(15).ConfigureAwait(false);
        while (true)
        {
            await Workers.WaitAsync().ConfigureAwait(false);
            var batch = new List<Request>();
            lock (CacheLock)
            {
                if (Pending.Count == 0) { _draining = false; Workers.Release(); return; }
                var home = Pending.Peek().Home;
                var length = 0;
                while (Pending.Count > 0 && Pending.Peek().Home == home && batch.Count < 24 && length < 65536)
                {
                    var request = Pending.Dequeue(); batch.Add(request); length += request.Source.Length;
                }
            }
            _ = ProcessBatchAsync(batch);
        }
    }
    private static async Task ProcessBatchAsync(List<Request> batch)
    {
        try { await CompileIsolatedAsync(batch); }
        finally { Workers.Release(); }
    }
    private static async Task CompileIsolatedAsync(List<Request> batch)
    {
        try { await CompileBatchAsync(batch); }
        catch (TexSyntaxException) when (batch.Count > 1)
        {
            // A malformed formula must not prevent its healthy neighbours from being rendered.
            var middle = batch.Count / 2;
            await CompileIsolatedAsync(batch.GetRange(0, middle));
            await CompileIsolatedAsync(batch.GetRange(middle, batch.Count - middle));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var request in batch) request.Fail(ex);
        }
    }
    private static async Task CompileBatchAsync(List<Request> batch)
    {
        var home = batch[0].Home;
        string? work = null;
        try
        {
            Directory.CreateDirectory(home);
            var latex = CompileService.FindAuxiliaryTool("latex") ?? throw new InvalidOperationException("未找到 latex。请安装包含 amsmath、amssymb 和 dvisvgm 的 TeX Live 或 MiKTeX。");
            var svg = CompileService.FindAuxiliaryTool("dvisvgm") ?? throw new InvalidOperationException("未找到 dvisvgm。请在 TeX 发行版中安装 dvisvgm。");
            work = Path.Combine(home, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var document = new StringBuilder("\\documentclass{article}\n\\usepackage{amsmath,amssymb}\n\\pagestyle{empty}\n\\begin{document}\n");
            for (var i = 0; i < batch.Count; i++)
            {
                var request = batch[i];
                document.Append("\\begingroup\n\\setbox0=\\hbox{$")
                    .Append(request.Display ? @"\displaystyle" : @"\textstyle").Append('\n').Append(request.Source)
                    .Append("\n$}\n\\typeout{TEXEED-METRICS-").Append(i + 1)
                    .Append(": \\the\\wd0,\\the\\ht0,\\the\\dp0}\n\\shipout\\box0\n\\endgroup\n");
            }
            document.Append("\\end{document}\n");
            await File.WriteAllTextAsync(Path.Combine(work, "formula.tex"), document.ToString(), new UTF8Encoding(false));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var arguments = new List<string> { "-no-shell-escape", "-interaction=nonstopmode", "-halt-on-error", "-file-line-error" };
            if (latex.Contains("miktex", StringComparison.OrdinalIgnoreCase)) arguments.Add("--disable-installer");
            arguments.Add("formula.tex");
            var log = await RunAsync(latex, arguments, work, timeout.Token, isolateSyntax: true);
            var metrics = Regex.Matches(log, @"TEXEED-METRICS-(\d+):\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt")
                .ToDictionary(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (metrics.Count != batch.Count) throw new InvalidDataException("LaTeX 未返回全部公式尺寸。");
            await RunAsync(svg, ["--no-fonts", "--no-specials", "--exact-bbox", "--bbox=min", "--page=1-", "--cache=none", "--output=formula-%1p.svg", "formula.dvi"], work, timeout.Token);
            for (var index = 0; index < batch.Count; index++)
            {
                var request = batch[index];
                try
                {
                    var generated = Path.Combine(work, "formula-" + (index + 1) + ".svg");
                    if (new FileInfo(generated).Length > 4_000_000) throw new InvalidDataException("公式 SVG 过大。");
                    var xml = XDocument.Load(generated);
                    var root = xml.Root ?? throw new InvalidDataException("公式 SVG 为空。");
                    var metric = metrics[index + 1];
                    for (var i = 0; i < 3; i++)
                        root.SetAttributeValue("data-texeed-" + new[] { "width", "height", "depth" }[i], double.Parse(metric.Groups[i + 2].Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    var result = ChatSvg.Read(root, request.Path);
                    await File.WriteAllTextAsync(generated, xml.ToString(), new UTF8Encoding(false));
                    File.Move(generated, request.Path, overwrite: true);
                    request.Completion.TrySetResult(result);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { request.Fail(ex); }
            }
            TrimCache(home);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("LaTeX 公式排版超时，已保留源文。"); }
        finally
        {
            if (work is not null)
                try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string work, CancellationToken token, bool isolateSyntax = false)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["openin_any"] = "p";
        start.Environment["openout_any"] = "p";
        start.Environment["shell_escape"] = "f";
        start.Environment["TEXINPUTS"] = "";
        start.Environment["TEXMFOUTPUT"] = work;
        using var process = Process.Start(start) ?? throw new IOException("无法启动公式排版程序。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0)
        {
            var error = output.Split('\n').FirstOrDefault(line => line.StartsWith('!') || line.Contains("formula.tex:") || line.Contains("ERROR:")) ?? "公式语法错误或缺少宏包";
            var message = "LaTeX → SVG 失败：" + error.Trim();
            if (isolateSyntax && !output.Contains("not found", StringComparison.OrdinalIgnoreCase)) throw new TexSyntaxException(message);
            throw new InvalidOperationException(message);
        }
        return output;
    }
    private static void TrimCache(string home)
    {
        try
        {
            long size = 0; var count = 0;
            foreach (var file in new DirectoryInfo(home).EnumerateFiles("*.svg").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                size += file.Length;
                if (++count > 512 || size > 64_000_000) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
