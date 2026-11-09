using System.IO;

namespace TeXeed.App.AI;

/// <summary>Resolves local document images only within one conversation directory.</summary>
internal static class AiDocumentAssets
{
    internal const string Host = "texeed-project.invalid";
    private const long MaximumImageBytes = 20 * 1024 * 1024;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml"
    };

    internal static bool TryReadImage(string? root, string binding, Uri uri, out byte[] content, out string mime)
    {
        content = []; mime = "";
        try
        {
            if (string.IsNullOrEmpty(root) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != Host || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            var prefix = "/" + binding + "/";
            if (string.IsNullOrEmpty(binding) || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var relative = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
            if (relative.Length == 0 || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0')) return false;
            var segments = relative.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment.EndsWith(' ') || segment.EndsWith('.') || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))) return false;
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(basePath) || (File.GetAttributes(basePath) & FileAttributes.ReparsePoint) != 0) return false;
            var path = Path.GetFullPath(Path.Combine(basePath, Path.Combine(segments)));
            if (!path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var extension = Path.GetExtension(path);
            var candidates = extension.Length == 0 ? Types.Keys.Select(ext => path + ext) : [path];
            foreach (var candidate in candidates)
            {
                if (!Types.TryGetValue(Path.GetExtension(candidate), out var type) || !File.Exists(candidate)) continue;
                var current = basePath;
                foreach (var segment in Path.GetRelativePath(basePath, candidate).Split(Path.DirectorySeparatorChar))
                { current = Path.Combine(current, segment); if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
                using var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > MaximumImageBytes) return false;
                using var buffer = new MemoryStream((int)file.Length);
                var chunk = new byte[64 * 1024]; int count;
                while ((count = file.Read(chunk, 0, chunk.Length)) > 0)
                { if (buffer.Length + count > MaximumImageBytes) return false; buffer.Write(chunk, 0, count); }
                content = buffer.ToArray(); mime = type; return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or UriFormatException)
        { return false; }
    }
}
