using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TeXeed.Core;

public sealed partial class CompileService
{
    private sealed record BibliographyCache(string Inputs, string Output);
    private sealed record BibliographyRequest(string Tool, string Signature, bool Cacheable);
    private static readonly HashSet<string> AuxiliaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aux", ".toc", ".lof", ".lot", ".out", ".nav", ".snm", ".loa", ".lol", ".thm"
    };

    private static bool RequestsLatexRerun(string log) => Regex.IsMatch(log,
        @"Rerun to get|Label\(s\) may have changed|(?:Please\s+)?(?:re)?run LaTeX|run (?:LaTeX|TeX) again|rerunfilecheck Warning:[^\r\n]*has changed",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool RequestsBibliography(string log, string tool) => Regex.IsMatch(log,
        @"(?:Please\s+)?(?:\(re\)|re)?run\s+" + Regex.Escape(tool) + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static async Task<string> AuxiliaryStateAsync(string directory, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false, MaxRecursionDepth = 64
        };
        var files = Directory.EnumerateFiles(directory, "*", options)
            .Where(path => AuxiliaryExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file) + "\0"));
            hash.AppendData(Encoding.ASCII.GetBytes(await FileSignatureAsync(file, token)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> FileSignatureAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return "missing";
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("编译辅助文件不能是符号链接：" + Path.GetFileName(path));
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static async Task<BibliographyRequest?> DescribeBibliographyAsync(TexProject project, string build,
        string job, ToolInfo engine, IEnumerable<string> directories, string passLog, CancellationToken token)
    {
        // BibTeX reads \@input recursively, but ordinary labels and page numbers do
        // not affect its output. Keep only bibliography directives in its signature.
        var control = new StringBuilder();
        var fullAux = new StringBuilder();
        var cacheable = true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(Path.Combine(build, job + ".aux"));
        while (queue.TryDequeue(out var aux))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(aux)) continue;
            if (seen.Count > 1024) throw new IOException("辅助文件依赖过多，无法安全检查编译状态。");
            if (!IsWithin(build, aux) || !IsOrdinaryInput(project.Root, aux)) { cacheable = false; continue; }
            if (!File.Exists(aux)) { cacheable = false; continue; }
            var text = await ReadLimitedAsync(aux, token);
            fullAux.AppendLine(text);
            control.AppendLine(Path.GetRelativePath(build, aux));
            // Preserve @input in its original position: unsrt-style bibliographies
            // depend on the citation order across main and included aux files.
            foreach (Match directive in Regex.Matches(text, @"\\(?:citation|bibdata|bibstyle|@input)\s*\{[^}]*\}"))
                control.AppendLine(directive.Value);
            if (new FileInfo(aux).Length > 4 * 1024 * 1024)
            {
                cacheable = false;
                control.AppendLine(await FileSignatureAsync(aux, token));
            }
            foreach (Match input in Regex.Matches(text, @"\\@input\s*\{(?<path>[^}]+)\}"))
            {
                try { queue.Enqueue(Path.GetFullPath(input.Groups["path"].Value, build)); }
                catch (ArgumentException) { cacheable = false; }
            }
        }
        var bibtexControl = control.ToString();
        var bcf = Path.Combine(build, job + ".bcf");
        var tool = Regex.IsMatch(bibtexControl, @"\\bibdata\s*\{") ? "bibtex" : null;
        if (tool is null && File.Exists(bcf))
        {
            // A preserved .bcf from an earlier biblatex document must not activate
            // Biber after the author removes biblatex or switches to BibTeX.
            var active = fullAux.ToString().Contains(@"\abx@aux@", StringComparison.Ordinal)
                || RequestsBibliography(passLog, "biber");
            var recorder = Path.Combine(build, job + ".fls");
            if (!active && File.Exists(recorder))
                active = (await ReadLimitedAsync(recorder, token)).Split('\n')
                    .Any(line => line.StartsWith("OUTPUT ", StringComparison.Ordinal)
                        && line.Trim().EndsWith(job + ".bcf", StringComparison.OrdinalIgnoreCase));
            if (active) tool = "biber";
        }
        if (tool is null) return null;

        var signature = new StringBuilder("texeed-bibliography-v1\n");
        signature.AppendLine(engine.Name).AppendLine(ToolIdentity(engine.Path));
        signature.AppendLine(tool).AppendLine(ToolIdentity(FindTool(tool, directories)));
        signature.AppendLine(Environment.GetEnvironmentVariable("BIBINPUTS"));
        signature.AppendLine(Environment.GetEnvironmentVariable("BSTINPUTS"));
        var resources = new List<string>();
        var styles = new List<string>();
        if (tool == "bibtex")
        {
            signature.Append(bibtexControl);
            foreach (Match data in Regex.Matches(bibtexControl, @"\\bibdata\s*\{(?<names>[^}]*)\}"))
                resources.AddRange(data.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (Match style in Regex.Matches(bibtexControl, @"\\bibstyle\s*\{(?<name>[^}]*)\}"))
                styles.Add(style.Groups["name"].Value.Trim());
        }
        else
        {
            signature.AppendLine(await FileSignatureAsync(bcf, token));
            var text = await ReadLimitedAsync(bcf, token);
            if (new FileInfo(bcf).Length > 4 * 1024 * 1024) cacheable = false;
            foreach (Match data in Regex.Matches(text, @"<(?:\w+:)?datasource\b[^>]*>(?<path>[^<]+)</(?:\w+:)?datasource\s*>", RegexOptions.IgnoreCase))
                resources.Add(System.Net.WebUtility.HtmlDecode(data.Groups["path"].Value.Trim()));
        }

        var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles)
        {
            var file = ResolveBibliographyInput(project, build, style, ".bst");
            if (file is not null) inputs.Add(file);
            else if (Path.IsPathRooted(style) || style.Contains('/') || style.Contains('\\')) cacheable = false;
            // Bare names such as "plain" are normally distribution styles. Their
            // names and toolchain identity already participate in the signature.
        }
        foreach (var resource in resources)
        {
            var file = ResolveBibliographyInput(project, build, resource);
            if (file is null)
            {
                // BIBINPUTS may resolve an external/remote database. Let the tool
                // handle it, but never assume an unobservable database is unchanged.
                cacheable = false;
                signature.AppendLine("unresolved:" + resource);
            }
            else inputs.Add(file);
        }
        foreach (var file in project.Files.Where(f => f.Kind == ProjectFileKind.Bibliography
            || Path.GetExtension(f.Path).Equals(".bst", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsOrdinaryInput(project.Root, file.Path)) inputs.Add(file.Path);
            else cacheable = false;
        }
        foreach (var file in inputs.OrderBy(path => path, StringComparer.Ordinal))
            signature.AppendLine(file).AppendLine(await FileSignatureAsync(file, token));
        return new(tool, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))), cacheable);
    }

    private static string ToolIdentity(string? path)
    {
        if (path is null || !File.Exists(path)) return "missing";
        var file = new FileInfo(path);
        return $"{Path.GetFullPath(path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static async Task<ProcessResult> RunBibliographyAsync(TexProject project, string build, string job,
        string tool, string executable, Dictionary<string, string> environment, CancellationToken token)
    {
        if (tool == "biber")
        {
            var relativeBuild = Path.GetRelativePath(EntryDirectory(project), build).Replace('\\', '/');
            return await RunProcessAsync(executable, ["--input-directory", relativeBuild, "--output-directory", relativeBuild, job], EntryDirectory(project), environment, token);
        }

        // BibTeX has no output-directory option. Running it beside the entry and
        // passing ../.texeed/.../job is rejected by TeX Live's openout_any=p.
        // Give local resource paths ASCII aliases beside the generated aux;
        // BibTeX cannot reliably parse absolute paths containing spaces/Unicode.
        // preserve source spelling, original bytes and timestamps for the next TeX pass.
        var restored = new List<(string Path, byte[] Bytes, DateTime Modified)>();
        var aliases = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            var pending = new Queue<string>(); pending.Enqueue(Path.Combine(build, job + ".aux"));
            var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            while (pending.TryDequeue(out var aux))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(aux)) continue;
                if (visited.Count > 256) throw new IOException("文献辅助文件过多，无法安全准备相对路径。");
                if (!IsWithin(build, aux) || !IsOrdinaryInput(project.Root, aux) || !File.Exists(aux)) continue;
                if (new FileInfo(aux).Length > 4 * 1024 * 1024) throw new IOException("文献辅助文件过大，无法安全准备相对路径。");
                var bytes = await File.ReadAllBytesAsync(aux, token);
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { continue; }
                foreach (Match input in Regex.Matches(text, @"\\@input\s*\{(?<path>[^}]+)\}"))
                    pending.Enqueue(Path.GetFullPath(input.Groups["path"].Value, build));
                var rewritten = Regex.Replace(text, @"\\(?<command>bibdata|bibstyle)\s*\{(?<names>[^}]*)\}", match =>
                {
                    var extension = match.Groups["command"].Value == "bibdata" ? ".bib" : ".bst";
                    var names = match.Groups["names"].Value.Split(',');
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = names[i].Trim();
                        var path = ResolveBibliographyInput(project, build, name, extension);
                        if (path is not null)
                        {
                            if (!aliases.TryGetValue(path, out var alias))
                            {
                                alias = Path.Combine(build, "texeed-bib-" + Guid.NewGuid().ToString("N") + extension);
                                File.Copy(path, alias);
                                aliases.Add(path, alias);
                            }
                            names[i] = Path.GetFileNameWithoutExtension(alias);
                        }
                    }
                    return "\\" + match.Groups["command"].Value + "{" + string.Join(',', names) + "}";
                });
                if (rewritten == text) continue;
                restored.Add((aux, bytes, File.GetLastWriteTimeUtc(aux)));
                await File.WriteAllTextAsync(aux, rewritten, new UTF8Encoding(false), token);
            }
            var bibEnvironment = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
            foreach (var variable in new[] { "TEXINPUTS", "BIBINPUTS", "BSTINPUTS" })
                bibEnvironment[variable] = InputSearchPath(project, variable, build);
            return await RunProcessAsync(executable, [job], build, bibEnvironment, token);
        }
        finally
        {
            foreach (var original in restored)
            {
                await File.WriteAllBytesAsync(original.Path, original.Bytes, CancellationToken.None);
                File.SetLastWriteTimeUtc(original.Path, original.Modified);
            }
            foreach (var alias in aliases.Values) File.Delete(alias);
        }
    }

    private static string? ResolveBibliographyInput(TexProject project, string build, string name, string extension = ".bib")
    {
        try
        {
            if (!Path.HasExtension(name)) name += extension;
            foreach (var directory in new[] { EntryDirectory(project), project.Root, build })
            {
                var path = Path.GetFullPath(name, directory);
                if (IsOrdinaryInput(project.Root, path) && File.Exists(path)) return path;
            }
            // An explicit unresolved path must not accidentally bind to a different
            // local database that happens to share the same basename.
            if (Path.IsPathRooted(name) || Path.GetFileName(name) != name) return null;
            var matches = project.Files.Where(file => Path.GetExtension(file.Path).Equals(extension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(file.Path).Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)
                && IsOrdinaryInput(project.Root, file.Path) && File.Exists(file.Path)).ToArray();
            if (matches.Length == 1) return matches[0].Path;
            if (extension == ".bst")
            {
                // A macro-generated \bibliographystyle is invisible to static
                // discovery; TeX's root// search may still resolve a local style.
                var options = new EnumerationOptions { RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, MaxRecursionDepth = 64 };
                var localStyles = Directory.EnumerateFiles(project.Root, name, options)
                    .Where(path => !IsWithin(Path.Combine(project.Root, ".texeed"), path)).Take(2).ToArray();
                if (localStyles.Length == 1) return localStyles[0];
            }
            return null;
        }
        catch (ArgumentException) { return null; }
    }

    private static bool IsOrdinaryInput(string root, string path)
    {
        if (!IsWithin(root, path)) return false;
        var currentPath = Path.GetFullPath(root);
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, part);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath))
                && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static async Task<BibliographyCache?> ReadBibliographyCacheAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 256) return null;
        try
        {
            var values = (await File.ReadAllTextAsync(path, token)).Split('\n', StringSplitOptions.TrimEntries);
            return values.Length == 2 && values.All(value => Regex.IsMatch(value, "^[A-F0-9]{64}$")) ? new(values[0], values[1]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteBibliographyCacheAsync(string path, BibliographyCache cache)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await File.WriteAllTextAsync(staging, cache.Inputs + "\n" + cache.Output);
            File.Move(staging, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache is optional; a valid, already-published PDF is still a success.
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
