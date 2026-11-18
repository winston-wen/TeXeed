using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TeXeed.App.Controls;

internal static class ChatReadingTypography
{
    internal const string HiraginoFile = "Hiragino_Sans_GB.ttc";
    internal static string LocalFontDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed", "fonts");
    internal static bool HasHiragino { get; } = HasUsableHiragino(LocalFontDirectory);
    internal static bool HasPlex { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "IBMPlexSans-Regular.otf"));
    internal static FontFamily Family { get; } = CreateFamily(LocalFontDirectory);

    internal static FontFamily CreateFamily(string hiraginoDirectory)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        var bundledUri = new Uri(Path.GetFullPath(bundled) + Path.DirectorySeparatorChar, UriKind.Absolute);
        var localUri = new Uri(Path.GetFullPath(hiraginoDirectory) + Path.DirectorySeparatorChar, UriKind.Absolute);
        return new FontFamily(bundledUri, "./#IBM Plex Sans, " + localUri.AbsoluteUri + "#Hiragino Sans GB, Microsoft YaHei");
    }

    internal static bool HasUsableHiragino(string directory)
    {
        var file = Path.Combine(directory, HiraginoFile);
        if (!File.Exists(file)) return false;
        try
        {
            var uri = new Uri(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, UriKind.Absolute);
            var family = new FontFamily(uri, "./#Hiragino Sans GB");
            var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return face.TryGetGlyphTypeface(out var glyphs) &&
                string.Equals(glyphs.FontUri.LocalPath, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) &&
                glyphs.FaceNames.Values.Contains("W3", StringComparer.OrdinalIgnoreCase) && glyphs.CharacterToGlyphMap.ContainsKey('中');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
