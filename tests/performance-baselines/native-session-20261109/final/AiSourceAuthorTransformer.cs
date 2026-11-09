using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>Uses the session's author ranges while preserving the source editor's fixed grid.</summary>
internal sealed class AiSourceAuthorTransformer : DocumentColorizingTransformer
{
    private static readonly FontFamily? UserFont = FindUserFont();
    private IReadOnlyList<AiDocumentAuthorSpan> _spans = [];
    private readonly Dictionary<Typeface, Typeface> _typefaces = [];
    internal bool UsesBundledFont => UserFont is not null;
    internal void SetSpans(IReadOnlyList<AiDocumentAuthorSpan> spans) => _spans = spans;
    internal static Typeface UserTypeface(Typeface original, FontFamily? font) => font is null
        ? new Typeface(original.FontFamily, original.Style == FontStyles.Italic ? FontStyles.Normal : FontStyles.Italic, original.Weight, original.Stretch)
        : new Typeface(font, original.Style, original.Weight, original.Stretch);
    private Typeface CachedUserTypeface(Typeface original)
    {
        if (_typefaces.TryGetValue(original, out var cached)) return cached;
        if (_typefaces.Count >= 32) _typefaces.Clear();
        return _typefaces[original] = UserTypeface(original, UserFont);
    }
    private static FontFamily? FindUserFont()
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "AiDocument", "fonts", "review");
            if (!File.Exists(Path.Combine(directory, "LXGWWenKaiGB-Regular.ttf"))) return null;
            var family = new FontFamily(new Uri(directory + Path.DirectorySeparatorChar), "./#LXGW WenKai GB");
            return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out _) ? family : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    protected override void ColorizeLine(DocumentLine line)
    {
        var spans = _spans; var low = 0; var high = spans.Count;
        while (low < high) { var middle = low + (high - low) / 2; if ((long)spans[middle].Start + spans[middle].Length <= line.Offset) low = middle + 1; else high = middle; }
        int[]? boundaries = null;
        int Align(int offset, bool forward)
        {
            if (offset <= line.Offset || offset >= line.EndOffset) return Math.Clamp(offset, line.Offset, line.EndOffset);
            boundaries ??= StringInfo.ParseCombiningCharacters(CurrentContext.Document.GetText(line));
            var index = Array.BinarySearch(boundaries, offset - line.Offset);
            if (index >= 0) return offset;
            index = ~index;
            return line.Offset + (forward ? index < boundaries.Length ? boundaries[index] : line.Length : boundaries[Math.Max(0, index - 1)]);
        }
        for (var i = low; i < spans.Count && spans[i].Start < line.EndOffset; i++)
        {
            var span = spans[i]; if (span.Role != "user" || span.Length <= 0) continue;
            var start = Align(Math.Max(line.Offset, span.Start), true); var end = Align((int)Math.Min(line.EndOffset, (long)span.Start + span.Length), false);
            if (end <= start) continue;
            ChangeLinePart(start, end, element =>
            {
                var old = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(CachedUserTypeface(old));
            });
        }
    }
}
