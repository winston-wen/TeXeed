using Markdig;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal static class AiReferencePresentation
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    internal const string PlaceholderPrefix = "TEXEED_REFERENCE_";

    internal static string Markup(IReadOnlyList<AiEntry> entries) => string.Join("\n",
        entries.Select((_, i) => $"{i + 1}. [{PlaceholderPrefix}{i + 1}](#snapshot-{i + 1})"));

    internal sealed record ExcerptPart(string Text, bool Formula = false, bool Display = false, string? CopyTex = null);

    internal static bool MayContainMath(AiAnswerAnchor anchor) => anchor.PreviewParts is { } parts
        ? parts.Any(part => part.Kind == "formula")
        : MayContainMath(anchor.Quote);

    private static bool MayContainMath(string quote) => quote.Contains('$') || quote.Contains(@"\(") ||
        quote.Contains(@"\[") || quote.Contains(@"\begin{");

    // New selections record literal text and actual formula elements separately.
    // Historical selections only retain their source text; recover complete math
    // delimiters without changing the stored anchor or interpreting Markdown links.
    internal static IReadOnlyList<ExcerptPart> Excerpt(AiAnswerAnchor anchor)
    {
        if (anchor.PreviewParts is { } parts)
        {
            if (parts.Count == 0) return [new ExcerptPart(anchor.Quote)];
            Dictionary<string, (bool? Display, string? CopyTex)>? inferred = null;
            if (parts.Any(part => part.Kind == "formula" && part.Display is null) &&
                (anchor.Quote.Contains("$$") || anchor.Quote.Contains(@"\[") || anchor.Quote.Contains(@"\begin{")))
            {
                try
                {
                    var oldMath = new ChatMath(); oldMath.Prepare(anchor.Quote, Pipeline);
                    inferred = oldMath.Tokens.Values.Where(token => token.Reference is null)
                        .GroupBy(token => token.Source, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
                        {
                            var displays = group.Select(token => token.Display).Distinct().ToArray();
                            var copies = group.Select(token => token.CopyTex).Distinct(StringComparer.Ordinal).ToArray();
                            return (displays.Length == 1 ? (bool?)displays[0] : null, copies.Length == 1 ? copies[0] : null);
                        }, StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            return parts.Select(part =>
            {
                if (part.Kind != "formula") return new ExcerptPart(part.Text);
                var guessed = inferred?.GetValueOrDefault(part.Text);
                return new ExcerptPart(part.Text, true, part.Display ?? guessed?.Display ?? false, part.CopyTex ?? guessed?.CopyTex);
            }).ToArray();
        }
        var quote = anchor.Quote;
        if (!MayContainMath(quote)) return [new ExcerptPart(quote)];
        try
        {
            var math = new ChatMath();
            math.Prepare(quote, Pipeline);
            var tokens = math.Tokens.Values.Where(token => token.Reference is null && token.OriginalOffset >= 0)
                .OrderBy(token => token.OriginalOffset).ToArray();
            if (tokens.Length == 0) return [new ExcerptPart(quote)];
            var result = new List<ExcerptPart>();
            var offset = 0;
            foreach (var token in tokens)
            {
                if (token.OriginalOffset > offset) result.Add(new ExcerptPart(quote[offset..token.OriginalOffset]));
                result.Add(new ExcerptPart(token.Source, true, token.Display, token.CopyTex));
                offset = token.OriginalOffset + token.OriginalLength;
            }
            if (offset < quote.Length) result.Add(new ExcerptPart(quote[offset..]));
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return [new ExcerptPart(quote)]; }
    }

    internal static AiAnswerAnchor NormalizePreview(AiAnswerAnchor anchor)
    {
        if (anchor.PreviewKind == "formula" && !string.IsNullOrWhiteSpace(anchor.PreviewSource)) return anchor;
        if (anchor.PreviewKind is not (null or "formula")) return anchor;
        if (anchor.PreviewParts is not null) return anchor;
        var quote = anchor.Quote.Trim();
        if (quote.Length == 0) return anchor;
        try
        {
            var math = new ChatMath();
            var prepared = math.Prepare(quote, Pipeline);
            if (math.Tokens.Count != 1) return anchor;
            var item = math.Tokens.Single();
            if (prepared.Trim() != item.Key || item.Value.Reference is not null || string.IsNullOrWhiteSpace(item.Value.Source)) return anchor;
            return anchor with { PreviewKind = "formula", PreviewSource = item.Value.Source, PreviewNumber = anchor.PreviewNumber ?? item.Value.Number };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return anchor; }
    }
}
