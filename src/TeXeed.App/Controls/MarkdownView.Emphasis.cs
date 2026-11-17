using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    internal static readonly object StrongEmphasisMarker = new();

    private static void ApplyChatEmphasis(Span span)
    {
        span.Tag = StrongEmphasisMarker;
        span.SetResourceReference(TextElement.ForegroundProperty, "ChatEmphasis");
    }

    private static void RestoreLinkEmphasisColor(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Span span)
            {
                if (ReferenceEquals(span.Tag, StrongEmphasisMarker))
                    span.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                RestoreLinkEmphasisColor(span.Inlines);
            }
    }
}
