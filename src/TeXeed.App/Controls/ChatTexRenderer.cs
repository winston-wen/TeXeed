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
        internal readonly bool Cjk = NeedsCjk(source);
        internal readonly bool UnicodeMath = NeedsUnicodeMath(source);
        internal readonly int[] Breaks = display ? [] : ChatInlineBreaks.Positions(source);
        internal ChatSvg? PreviousAsset;
        internal readonly TaskCompletionSource<ChatSvg> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DateTime FailedAt;
        internal void Fail(Exception error)
        {
            FailedAt = DateTime.UtcNow;
            if (PreviousAsset is { } previous) Completion.TrySetResult(previous);
            else Completion.TrySetException(error);
        }
    }
    internal sealed class TexSyntaxException(string message) : Exception(message);
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, Request> Cache = [];
    private static readonly Queue<Request> Pending = new();
    private static readonly SemaphoreSlim Workers = new(2);
    private static bool _draining;
    // Version the disk cache with the TeX preamble and rendering styles.
    private const string Revision = "latex-svg-v1";
    private const string CjkPreamble = """
        \usepackage[CJKmath=true]{xeCJK}
        \newcommand{\texeedcjkfont}[1]{%
          \setCJKmainfont{#1}\setCJKsansfont{#1}\setCJKmonofont{#1}%
          \typeout{TEXEED-CJK-FONT: #1}}
        \IfFontExistsTF{Source Han Serif SC}{\texeedcjkfont{Source Han Serif SC}}{%
          \IfFontExistsTF{Noto Serif CJK SC}{\texeedcjkfont{Noto Serif CJK SC}}{%
            \IfFontExistsTF{SimSun}{\texeedcjkfont{SimSun}}{\texeedcjkfont{FandolSong-Regular.otf}}}}
        """;
    private static bool NeedsCjk(string source) => source.EnumerateRunes().Any(r =>
        r.Value is >= 0x2E80 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF);
    // AMS blackboard fonts contain capitals but omit most lowercase letters and digits.
    private static bool NeedsUnicodeMath(string source) => Regex.IsMatch(source,
        @"\\mathbb\s*(?:\{[^{}]*[a-z0-9][^{}]*\}|[a-z0-9])", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    internal static string CacheFileName(string source, bool display) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + (NeedsCjk(source) ? "-cjk-v1" : "") + (NeedsUnicodeMath(source) ? "-unicode-v1" : "") + "\n" + display + "\n" + source))) + ".svg";
    internal static Task<ChatSvg> RenderAsync(string source, bool display)
    {
        var home = Path.Combine(AiStorage.Home, "math-svg");
        var path = Path.Combine(home, CacheFileName(source, display));
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
            try { ChatTexInput.Validate(request.Source); }
            catch (ArgumentException error) { throw new TexSyntaxException(error.Message); }
            // Disk hits never wait for a TeX process slot, including while a long answer compiles.
            if (File.Exists(request.Path))
            {
                try
                {
                    var cached = ChatSvg.Read(request.Path);
                    var needsSerif = request.Cjk && cached.CjkFont is null;
                    if (!needsSerif && (request.Breaks.Length == 0 || cached.HasBreakMetrics || cached.Bounds.Width < ChatInlineBreaks.MinimumWidthBp))
                    { request.Completion.TrySetResult(cached); return; }
                    // Rebuild older graphics in place. Keep the readable original if TeX is unavailable.
                    request.PreviousAsset = cached;
                }
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
                var cjk = Pending.Peek().Cjk;
                var unicodeMath = Pending.Peek().UnicodeMath;
                var length = 0;
                while (Pending.Count > 0 && Pending.Peek().Home == home && Pending.Peek().Cjk == cjk && Pending.Peek().UnicodeMath == unicodeMath && batch.Count < 24 && length < 65536)
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
    private static async Task CompileIsolatedAsync(List<Request> batch, bool markBreaks = true)
    {
        try { await CompileBatchAsync(batch, markBreaks); }
        catch (TexSyntaxException) when (batch.Count > 1)
        {
            // A malformed formula must not prevent its healthy neighbours from being rendered.
            var middle = batch.Count / 2;
            await CompileIsolatedAsync(batch.GetRange(0, middle));
            await CompileIsolatedAsync(batch.GetRange(middle, batch.Count - middle));
        }
        catch (TexSyntaxException) when (markBreaks && batch[0].Breaks.Length > 0)
        {
            // A position marker can interrupt a TeX macro scanning an unbraced argument.
            // Validate and render the author's unmodified formula before reporting an error.
            await CompileIsolatedAsync(batch, markBreaks: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var request in batch) request.Fail(ex);
        }
    }
    private static async Task CompileBatchAsync(List<Request> batch, bool markBreaks)
    {
        var home = batch[0].Home;
        string? work = null;
        try
        {
            Directory.CreateDirectory(home);
            var cjk = batch[0].Cjk;
            var unicodeMath = batch[0].UnicodeMath;
            var xetex = cjk || unicodeMath;
            var engine = xetex ? "xelatex" : "latex";
            var latex = CompileService.FindAuxiliaryTool(engine) ?? throw new InvalidOperationException("未找到 " + engine + "。请在 TeX Live 或 MiKTeX 中安装此引擎。");
            var svg = CompileService.FindAuxiliaryTool("dvisvgm") ?? throw new InvalidOperationException("未找到 dvisvgm。请在 TeX 发行版中安装 dvisvgm。");
            work = Path.Combine(home, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var document = new StringBuilder("\\documentclass{article}\n\\usepackage{amsmath,amssymb}\n");
            if (cjk) document.AppendLine(CjkPreamble);
            if (unicodeMath) document.Append("\\usepackage{unicode-math}\n\\setmathfont{Latin Modern Math}\n");
            document.Append("\\tracinglostchars=2\n\\pagestyle{empty}\n\\begin{document}\n")
                .AppendLine(@"\def\texeedbreak#1{\pdfsavepos\write16{TEXEED-BREAK-#1: \the\pdflastxpos}}");
            for (var i = 0; i < batch.Count; i++)
            {
                var request = batch[i];
                var breaks = markBreaks ? request.Breaks : [];
                document.Append("\\begingroup\n\\setbox0=\\hbox{");
                if (breaks.Length > 0) document.Append(@"\texeedbreak{").Append(i + 1).Append("-0}");
                document.Append('$').Append(request.Display ? @"\displaystyle" : @"\textstyle").Append('\n');
                ChatInlineBreaks.AppendMarked(document, request.Source, breaks, i + 1);
                document.Append("\n$}\n\\typeout{TEXEED-METRICS-").Append(i + 1)
                    .Append(": \\the\\wd0,\\the\\ht0,\\the\\dp0}\n\\shipout\\box0\n\\endgroup\n");
            }
            document.Append("\\end{document}\n");
            await File.WriteAllTextAsync(Path.Combine(work, "formula.tex"), document.ToString(), new UTF8Encoding(false));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var arguments = new List<string> { "-no-shell-escape", "-interaction=nonstopmode", "-halt-on-error", "-file-line-error" };
            if (xetex) arguments.Add("-no-pdf");
            if (latex.Contains("miktex", StringComparison.OrdinalIgnoreCase)) arguments.Add("--disable-installer");
            arguments.Add("formula.tex");
            var log = await RunAsync(latex, arguments, work, timeout.Token, isolateSyntax: true);
            var cjkFont = cjk ? Regex.Match(log, @"TEXEED-CJK-FONT: ([^\r\n]+)").Groups[1].Value.Trim() : null;
            if (cjk && string.IsNullOrEmpty(cjkFont)) throw new InvalidDataException("XeLaTeX 未返回公式所用的中文字体。");
            var missing = log.Split('\n').FirstOrDefault(line => line.Contains("Missing character:"));
            if (missing is not null) throw new TexSyntaxException("公式缺少字形：" + missing.Trim());
            var metrics = Regex.Matches(log, @"TEXEED-METRICS-(\d+):\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt,\s*([0-9.+-]+)pt")
                .ToDictionary(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (metrics.Count != batch.Count) throw new InvalidDataException("LaTeX 未返回全部公式尺寸。");
            var positions = Regex.Matches(log, @"TEXEED-BREAK-(\d+)-(\d+):\s*(-?\d+)")
                .ToLookup(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            await RunAsync(svg, ["--no-fonts", "--no-specials", "--exact-bbox", "--bbox=min", "--page=1-", "--cache=none", "--output=formula-%1p.svg", xetex ? "formula.xdv" : "formula.dvi"], work, timeout.Token);
            for (var index = 0; index < batch.Count; index++)
            {
                var request = batch[index];
                try
                {
                    var generated = Path.Combine(work, "formula-" + (index + 1) + ".svg");
                    if (new FileInfo(generated).Length > 4_000_000) throw new InvalidDataException("公式 SVG 过大。");
                    var xml = XDocument.Load(generated);
                    var root = xml.Root ?? throw new InvalidDataException("公式 SVG 为空。");
                    if (cjkFont is not null) root.SetAttributeValue("data-texeed-cjk-font", cjkFont);
                    var metric = metrics[index + 1];
                    for (var i = 0; i < 3; i++)
                        root.SetAttributeValue("data-texeed-" + new[] { "width", "height", "depth" }[i], double.Parse(metric.Groups[i + 2].Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    if (request.Breaks.Length > 0)
                    {
                        var marks = positions[index + 1].ToArray();
                        var cuts = Array.Empty<double>();
                        if (marks.Length == request.Breaks.Length + 1 && marks[0].Groups[2].Value == "0")
                        {
                            var origin = long.Parse(marks[0].Groups[3].Value, CultureInfo.InvariantCulture);
                            cuts = marks.Skip(1).Select(m => (long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) - origin) * (72d / 72.27 / 65536)).ToArray();
                        }
                        root.SetAttributeValue("data-texeed-breaks", string.Join(" ", cuts.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
                    }
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
