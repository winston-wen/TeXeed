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
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, Task<ChatSvg>> Cache = [];
    private static readonly SemaphoreSlim Workers = new(2);
    private const string Revision = "latex-svg-v1";
    internal static Task<ChatSvg> RenderAsync(string source, bool display)
    {
        var home = Path.Combine(AiStorage.Home, "math-svg");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + "\n" + display + "\n" + source)));
        var path = Path.Combine(home, hash + ".svg");
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var existing) && !existing.IsFaulted) return existing;
            if (Cache.Count >= 512)
                foreach (var key in Cache.Where(pair => pair.Value.IsCompleted).Take(128).Select(pair => pair.Key).ToArray()) Cache.Remove(key);
            return Cache[path] = Task.Run(() => CompileAsync(source, display, home, path));
        }
    }
    private static async Task<ChatSvg> CompileAsync(string source, bool display, string home, string path)
    {
        ChatTexInput.Validate(source);
        await Workers.WaitAsync();
        string? work = null;
        try
        {
            Directory.CreateDirectory(home);
            if (File.Exists(path))
            {
                try { return ChatSvg.Read(path); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { File.Delete(path); }
            }
            var latex = CompileService.FindAuxiliaryTool("latex") ?? throw new InvalidOperationException("未找到 latex。请安装包含 amsmath、amssymb 和 dvisvgm 的 TeX Live 或 MiKTeX。");
            var svg = CompileService.FindAuxiliaryTool("dvisvgm") ?? throw new InvalidOperationException("未找到 dvisvgm。请在 TeX 发行版中安装 dvisvgm。");
            work = Path.Combine(home, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var document = """
                \documentclass{article}
                \usepackage{amsmath,amssymb}
                \pagestyle{empty}
                \begin{document}
                \setbox0=\hbox{$
                """ + (display ? @"\displaystyle" : @"\textstyle") + "\n" + source + "\n" + """
                $}
                \typeout{TEXEED-METRICS: \the\wd0,\the\ht0,\the\dp0}
                \shipout\box0
                \end{document}
                """;
            await File.WriteAllTextAsync(Path.Combine(work, "formula.tex"), document, new UTF8Encoding(false));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var arguments = new List<string> { "-no-shell-escape", "-interaction=nonstopmode", "-halt-on-error", "-file-line-error" };
            if (latex.Contains("miktex", StringComparison.OrdinalIgnoreCase)) arguments.Add("--disable-installer");
            arguments.Add("formula.tex");
            var log = await RunAsync(latex, arguments, work, timeout.Token);
            var metrics = Regex.Match(log, @"TEXEED-METRICS:\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt");
            if (!metrics.Success) throw new InvalidDataException("LaTeX 未返回公式尺寸。");
            await RunAsync(svg, ["--no-fonts", "--no-specials", "--exact-bbox", "--bbox=min", "--cache=none", "--output=formula.svg", "formula.dvi"], work, timeout.Token);
            var generated = Path.Combine(work, "formula.svg");
            if (new FileInfo(generated).Length > 4_000_000) throw new InvalidDataException("公式 SVG 过大。");
            var xml = XDocument.Load(generated);
            var root = xml.Root ?? throw new InvalidDataException("公式 SVG 为空。");
            for (var i = 1; i <= 3; i++)
                root.SetAttributeValue("data-texeed-" + new[] { "width", "height", "depth" }[i - 1], double.Parse(metrics.Groups[i].Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(generated, xml.ToString(), new UTF8Encoding(false));
            var result = ChatSvg.Read(generated);
            File.Move(generated, path, overwrite: true);
            TrimCache(home);
            return result with { Path = path };
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("LaTeX 公式排版超时，已保留源文。"); }
        finally
        {
            if (work is not null)
                try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Workers.Release();
        }
    }
    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string work, CancellationToken token)
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
            throw new InvalidOperationException("LaTeX → SVG 失败：" + error.Trim());
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
