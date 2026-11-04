using System.Text;
using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal enum AiBookmarkKind { Heading, Equation, User }
internal sealed record AiBookmarkTarget(string Id, string Label, string Description, string TurnId, string SegmentId,
    AiBookmarkKind Kind, int TurnIndex, int SegmentIndex, int Position, AiAnswerAnchor? Anchor);
internal sealed record AiBookmarkSelection(string ConversationId, AiAnswerAnchor Anchor, IReadOnlyList<string> FoldKeys);
internal sealed record AiBookmarkFragment(AiBookmarkKind Kind, string Number, string Description, int Position);

public sealed partial class MarkdownView
{
    private readonly Dictionary<int, FrameworkContentElement> _bookmarkHeadings = [];
    private readonly Dictionary<int, FrameworkContentElement> _bookmarkFormulas = [];

    internal static IReadOnlyList<AiBookmarkFragment> BookmarkFragments(string source)
    {
        var math = new ChatMath();
        var prepared = math.Prepare(source, Pipeline);
        var document = Markdig.Markdown.Parse(prepared, Pipeline);
        var results = new List<AiBookmarkFragment>();
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var text = new StringBuilder();
            void Append(ContainerInline? contents)
            {
                if (contents is null) return;
                foreach (var inline in contents)
                {
                    if (inline is LiteralInline literal) text.Append(literal.Content.ToString());
                    else if (inline is CodeInline code) text.Append(code.Content);
                    else if (inline is LineBreakInline) text.Append(' ');
                    else if (inline is ContainerInline nested) Append(nested);
                }
            }
            Append(heading.Inline);
            var description = System.Text.RegularExpressions.Regex.Replace(text.ToString(), math.Prefix + @"\d+END", match =>
                math.Tokens.TryGetValue(match.Value, out var token) ? token.Reference ?? token.Source : match.Value);
            results.Add(new(AiBookmarkKind.Heading, "h" + heading.Level, description.Trim(), heading.Span.Start));
        }
        foreach (var token in math.Tokens.Values)
            if (token.Display && token.Number is { Length: > 0 })
                results.Add(new(AiBookmarkKind.Equation, "eq" + token.Number, token.Source, token.PreparedOffset));
        return results.OrderBy(item => item.Position).ToArray();
    }

    internal bool HasBookmarkSelection => !SelectionView().Selection.IsEmpty;

    internal IReadOnlyList<string> BookmarkSelectionFoldKeys()
    {
        var view = SelectionView();
        var keys = new List<string>();
        void AddParents(FrameworkContentElement? element)
        {
            for (var node = element; node is not null; node = node.Parent as FrameworkContentElement)
                if (node is Section section && _bookmarkSections.TryGetValue(section, out var key)) keys.Add(key);
        }
        AddParents(view.Selection.Start.Parent as FrameworkContentElement);
        if (!ReferenceEquals(view, this) && _tableViews.FirstOrDefault(t => ReferenceEquals(t.View, view)) is { } table)
            AddParents(table.Block);
        keys.Reverse();
        return keys;
    }

    internal void RevealBookmarkFolds(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            foreach (var (section, sectionKey) in _bookmarkSections)
                if (sectionKey == key && _revealSections.TryGetValue(section, out var reveal)) reveal();
    }

    internal void RevealAllBookmarkFolds()
    {
        foreach (var reveal in _revealSections.Values.ToArray()) reveal();
    }

    internal FrameworkContentElement? BookmarkBlock(AiBookmarkKind kind, int position)
    {
        var blocks = kind == AiBookmarkKind.Heading ? _bookmarkHeadings : _bookmarkFormulas;
        if (!blocks.TryGetValue(position, out var block)) return null;
        Reveal(block);
        return block;
    }

    internal TextPointer? BookmarkPointer(string id, AiAnswerAnchor anchor) => AnnotationPointer(id, anchor);

    internal Point? BookmarkPoint(TextPointer pointer)
    {
        if (!pointer.HasValidLayout) return null;
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return null;
        var point = rect.TopLeft;
        foreach (var table in _tableViews)
        {
            if (!pointer.IsInSameDocument(table.View.Document.ContentStart)) continue;
            if (point.X < table.Scroll.HorizontalOffset || point.X > table.Scroll.HorizontalOffset + table.Scroll.ViewportWidth)
                table.Scroll.ScrollToHorizontalOffset(Math.Max(0, point.X - 20));
            return table.View.TranslatePoint(point, this);
        }
        return point;
    }
}
