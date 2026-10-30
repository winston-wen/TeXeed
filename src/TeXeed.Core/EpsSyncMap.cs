using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace TeXeed.Core;

/// <summary>Recognizes a local dvips EPS together with its original DVI and SyncTeX.</summary>
public static class EpsSyncMap
{
    internal sealed record Pair(string Source, string Output, double Left, double Top, double Width, double Height, bool IsPdf);
    private static readonly ConcurrentDictionary<string, (string Stamp, Pair? Value)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindSource(string projectRoot, string epsPath) => Inspect(projectRoot, epsPath)?.Source;

    internal static Pair? Inspect(string projectRoot, string epsPath)
    {
        try
        {
            var root = Path.GetFullPath(projectRoot); var path = Path.GetFullPath(epsPath);
            var output = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".dvi");
            var source = Path.ChangeExtension(path, ".tex"); var plain = Path.ChangeExtension(path, ".synctex");
            var sync = File.Exists(plain + ".gz") ? plain + ".gz" : plain;
            var files = new[] { path, output, source, sync };
            if (files.Any(p => !File.Exists(p) || !Safe(root, p))) return null;
            var stamp = string.Join("|", files.Select(p => { var i = new FileInfo(p); return $"{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
            var key = root + "|" + path;
            if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached.Value;
            var value = InspectUncached(root, path);
            if (Cache.Count > 128) Cache.Clear();
            Cache[key] = (stamp, value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static Pair? InspectUncached(string projectRoot, string epsPath)
    {
        try
        {
            var root = Path.GetFullPath(projectRoot); var eps = Path.GetFullPath(epsPath);
            var isPdf = eps.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (!Safe(root, eps) || !isPdf && !eps.EndsWith(".eps", StringComparison.OrdinalIgnoreCase)) return null;
            var dir = Path.GetDirectoryName(eps)!; var stem = Path.GetFileNameWithoutExtension(eps);
            var dvi = isPdf ? eps : Path.Combine(dir, stem + ".dvi"); var source = Path.Combine(dir, stem + ".tex");
            var sync = File.Exists(Path.ChangeExtension(dvi, ".synctex.gz")) ? Path.ChangeExtension(dvi, ".synctex.gz") : Path.ChangeExtension(dvi, ".synctex");
            if (!new[] { eps, dvi, source, sync }.All(p => Safe(root, p) && File.Exists(p))) return null;
            if ((!isPdf && new FileInfo(eps).Length > 32 * 1024 * 1024) || new FileInfo(sync).Length > 32 * 1024 * 1024) return null;
            var sourceTime = File.GetLastWriteTimeUtc(source); var dviTime = File.GetLastWriteTimeUtc(dvi);
            if (sourceTime > dviTime || File.GetLastWriteTimeUtc(sync) < sourceTime || File.GetLastWriteTimeUtc(sync) < dviTime || File.GetLastWriteTimeUtc(eps) < dviTime) return null;
            var text = isPdf ? "" : File.ReadAllText(eps);
            double left = 0, bottom = 0, right = 0, top = 0, pageHeight = 0;
            if (!isPdf)
            {
            if (!Regex.IsMatch(text, @"(?m)^%%Creator:\s*dvips\b", RegexOptions.IgnoreCase)) return null;
            var title = Regex.Match(text, @"(?m)^%%Title:\s*(.*?)\s*$");
            if (!title.Success || !string.Equals(Path.GetFileName(title.Groups[1].Value), Path.GetFileName(dvi), StringComparison.OrdinalIgnoreCase)) return null;
            var bbox = Regex.Match(text, @"(?m)^%%BoundingBox:\s*([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s*$");
            var start = Regex.Match(text, @"TeXDict begin\s+(\d+)\s+(\d+)\s+\d+\s+\d+\s+\d+\s*\([^\r\n]*\)\s*@start");
            if (!bbox.Success || !start.Success) return null;
            double Num(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
            left = Num(bbox.Groups[1]); bottom = Num(bbox.Groups[2]); right = Num(bbox.Groups[3]); top = Num(bbox.Groups[4]);
            pageHeight = Num(start.Groups[2]) / 65781.76;
            if (right <= left || top <= bottom || pageHeight < top || left < 0 || bottom < 0) return null;
            }
            using var stream = File.OpenRead(sync);
            using var gz = sync.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : null;
            using var raw = new MemoryStream();
            var buffer = new byte[8192]; int bytes;
            var content = gz ?? (Stream)stream;
            while ((bytes = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (raw.Length + bytes > 64 * 1024 * 1024) return null;
                raw.Write(buffer, 0, bytes);
            }
            var found = false;
            foreach (var line in CompileService.DecodeToolText(raw.ToArray()).Split('\n'))
            {
                if (line.StartsWith("Input:", StringComparison.Ordinal))
                {
                    var separator = line.IndexOf(':', 6); if (separator < 0) continue;
                    var path = line[(separator + 1)..].Trim('"');
                    foreach (var baseDir in new[] { root, dir })
                    {
                        try { if (string.Equals(Path.GetFullPath(path, baseDir), source, StringComparison.OrdinalIgnoreCase)) found = true; }
                        catch (ArgumentException) { }
                    }
                }
                if (!isPdf && line.Length > 1 && line[0] == '{' && int.TryParse(line.AsSpan(1), out var page) && page > 1) return null;
            }
            return found ? new(source, dvi, left, isPdf ? 0 : pageHeight - top, right - left, top - bottom, isPdf) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidDataException or OverflowException) { return null; }
    }

    private static bool Safe(string root, string path)
    {
        if (!CompileService.IsWithin(root, path)) return false;
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
}
