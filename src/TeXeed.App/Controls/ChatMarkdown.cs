using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TeXeed.App.Controls;

internal static class ChatMarkdown
{
    internal static readonly MarkdownPipeline ReadingPipeline = new MarkdownPipelineBuilder()
        .UseCustomContainers().UsePipeTables().UseEmphasisExtras().UseCjkFriendlyEmphasis().UseAutoLinks().DisableHtml().Build();
    // The substitute has the same UTF-16 width as the source space, preserving all spans.
    private const char ProtectedSpace = '\uE000';
    private const string ClosingPunctuation = "：；，。！？、）】》」』";
    private static readonly Regex SpacedCloser = new(@"(?<![\\*])\*\*(?![\s*])(?:(?!\*\*)[^\r\n])*?[" + ClosingPunctuation + @"](?<space> )\*\*(?!\*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

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

    internal static MarkdownDocument Parse(string source, MarkdownPipeline pipeline, out bool changed, bool revision = false)
    {
        changed = false;
        var document = Markdig.Markdown.Parse(source, pipeline);
        var spacedCloser = source.Contains(" **", StringComparison.Ordinal);
        var spacedOpening = revision && source.Contains("** ", StringComparison.Ordinal);
        if ((!spacedCloser && !spacedOpening) || source.Contains(ProtectedSpace)) return document;
        char[]? repaired = null;
        try
        {
            foreach (var leaf in document.Descendants<LeafBlock>())
            {
                if (leaf.Inline is not { } root || leaf.Span.Start < 0 || leaf.Span.End >= source.Length) continue;
                // Siblings occur in source order; binary search avoids rescanning all
                // literal spans for every candidate in a long, link-heavy paragraph.
                if (spacedOpening) repaired = RepairRevisionEmphasis(source, root, repaired);
                if (!spacedCloser) continue;
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

    private static char[]? RepairRevisionEmphasis(string source, ContainerInline root, char[]? repaired)
    {
        var opening = -1;
        bool CanOpen(int index) => index + 3 < source.Length && source[index + 2] == ' ' && !char.IsWhiteSpace(source[index + 3]);
        bool TryClose(int index)
        {
            if (opening < 0 || index <= opening + 3) return false;
            var spaced = source[index - 1] == ' ' && ClosingPunctuation.Contains(source[index - 2]);
            if (!spaced && char.IsWhiteSpace(source[index - 1])) return false;
            if (source.AsSpan(opening + 3, index - opening - 3).IndexOfAny('\r', '\n') >= 0)
            {
                opening = -1;
                return false;
            }
            repaired ??= source.ToCharArray();
            repaired[opening + 2] = ProtectedSpace;
            if (spaced) repaired[index - 1] = ProtectedSpace;
            opening = -1;
            return true;
        }

        foreach (var child in root)
        {
            var span = child.Span;
            if (opening >= 0 && child is EmphasisInline { DelimiterChar: '*', DelimiterCount: 2 } &&
                CanOpen(span.End - 1) && !char.IsWhiteSpace(source[span.Start - 1]) && TryClose(span.Start))
            {
                // In "** 一**，** 二**", the first parse attaches the middle pairs
                // to the comma. Each belongs to its neighbouring phrase.
                opening = span.End - 1;
                continue;
            }
            if (child is not LiteralInline) continue; // Code and links own their source spans.
            var text = source.AsSpan(span.Start, span.Length);
            var pos = 0;
            while (pos < text.Length - 1)
            {
                var next = text[pos..].IndexOf("**");
                if (next < 0) break;
                var index = span.Start + pos + next;
                pos += next + 2;
                if (index > 0 && source[index - 1] == '*' || index + 2 < source.Length && source[index + 2] == '*') continue;
                var backslashes = 0;
                while (index > backslashes && source[index - backslashes - 1] == '\\') backslashes++;
                if (backslashes % 2 != 0) continue;

                if (!TryClose(index)) opening = CanOpen(index) ? index : -1;
            }
        }
        return repaired;
    }
}
