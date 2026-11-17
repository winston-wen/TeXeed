using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TeXeed.App.Controls;

internal static class ChatMarkdown
{
    // The substitute has the same UTF-16 width as the source space, preserving all spans.
    private const char ProtectedSpace = '\uE000';
    private static readonly Regex SpacedCloser = new(@"(?<![\\*])\*\*(?![\s*])(?:(?!\*\*)[^\r\n])*?[：；，。！？、）】》」』](?<space> )\*\*(?!\*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    internal static string RestoreSpaces(string text, bool repaired) => repaired ? text.Replace(ProtectedSpace, ' ') : text;

    internal static bool LiteralAt(IReadOnlyList<SourceSpan> spans, int index)
    {
        var low = 0; var high = spans.Count - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var span = spans[mid];
            if (index < span.Start) high = mid - 1;
            else if (index > span.End) low = mid + 1;
            else return index < span.End;
        }
        return false;
    }

    internal static MarkdownDocument Parse(string source, MarkdownPipeline pipeline, out bool changed)
    {
        changed = false;
        var document = Markdig.Markdown.Parse(source, pipeline);
        if (!source.Contains(" **", StringComparison.Ordinal) || source.Contains(ProtectedSpace)) return document;
        char[]? repaired = null;
        try
        {
            foreach (var leaf in document.Descendants<LeafBlock>())
            {
                if (leaf.Inline is not { } root || leaf.Span.Start < 0 || leaf.Span.End >= source.Length) continue;
                // Siblings occur in source order; binary search avoids rescanning all
                // literal spans for every candidate in a long, link-heavy paragraph.
                var literals = root.OfType<LiteralInline>().Select(x => x.Span).ToArray();
                var length = leaf.Span.End - leaf.Span.Start + 1;
                foreach (Match match in SpacedCloser.Matches(source.Substring(leaf.Span.Start, length)))
                {
                    var opening = leaf.Span.Start + match.Index;
                    var closing = opening + match.Length - 2;
                    if (!LiteralAt(literals, opening) || !LiteralAt(literals, closing)) continue;
                    repaired ??= source.ToCharArray();
                    repaired[leaf.Span.Start + match.Groups["space"].Index] = ProtectedSpace;
                }
            }
        }
        catch (RegexMatchTimeoutException) { return document; }
        if (repaired is null) return document;
        changed = true;
        return Markdig.Markdown.Parse(new string(repaired), pipeline);
    }
}
