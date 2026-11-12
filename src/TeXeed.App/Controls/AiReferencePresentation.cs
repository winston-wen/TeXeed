using Markdig;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal static class AiReferencePresentation
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    internal const string PlaceholderPrefix = "TEXEED_REFERENCE_";

    internal static string Markup(IReadOnlyList<AiEntry> entries) => string.Join("\n",
        entries.Select((_, i) => $"{i + 1}. [{PlaceholderPrefix}{i + 1}](#snapshot-{i + 1})"));

    internal static AiAnswerAnchor NormalizePreview(AiAnswerAnchor anchor)
    {
        if (anchor.PreviewKind == "formula" && !string.IsNullOrWhiteSpace(anchor.PreviewSource)) return anchor;
        if (anchor.PreviewKind is not (null or "formula")) return anchor;
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
