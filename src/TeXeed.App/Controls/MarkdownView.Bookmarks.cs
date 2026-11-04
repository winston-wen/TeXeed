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
internal sealed record AiBookmarkSelection(string ConversationId, AiAnswerAnchor Anchor, IReadOnlyList<string> FoldKeys, string? ContextBefore = null, string? ContextAfter = null);
internal sealed record AiBookmarkFragment(AiBookmarkKind Kind, string Number, string Description, int Position);

public sealed partial class MarkdownView
{
    private readonly Dictionary<int, FrameworkContentElement> _bookmarkHeadings = [];
    private readonly Dictionary<int, FrameworkContentElement> _bookmarkFormulas = [];
    private int _markerVisibleTextVersion = -1;
    private string _markerVisibleText = "";

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

    internal (string Before, string After) BookmarkSelectionContext()
    {
        var selection = SelectionView().Selection;
        var before = selection.Start.Paragraph is { } first ? SelectionView().CopySource(first.ContentStart, selection.Start) : "";
        var after = selection.End.Paragraph is { } last ? SelectionView().CopySource(selection.End, last.ContentEnd) : "";
        var selected = SelectionView().CopySource(selection.Start, selection.End);
        var left = 0; var right = selected.Length;
        while (left < right && char.IsWhiteSpace(selected[left])) left++;
        while (right > left && char.IsWhiteSpace(selected[right - 1])) right--;
        before += selected[..left]; after = selected[right..] + after;
        return (before.Length > 32 ? before[^32..] : before, after.Length > 32 ? after[..32] : after);
    }

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

    internal TextPointer? ResolveUserBookmark(AiUserBookmark bookmark)
    {
        if (bookmark.Anchor is not { } anchor || anchor.Revision != Revision ||
            bookmark.FoldKeys.Any(key => _folds?.GetValueOrDefault(key) == true)) return null;
        // Older bookmarks may omit the target's fold ancestry. A match in the
        // visible document alone cannot rule out a duplicate in a closed section.
        if (bookmark.FoldKeys.Count == 0 && bookmark.ContextBefore is null && bookmark.ContextAfter is null &&
            _folds?.Values.Any(closed => closed) == true) return null;
        var quote = anchor.Quote.TrimStart();
        if (quote.Length == 0) return null;
        if (_markerVisibleTextVersion != LayoutVersion)
        {
            _markerVisibleTextVersion = LayoutVersion;
            _markerVisibleText = CopySource(Document.ContentStart, Document.ContentEnd);
        }
        var index = _markerVisibleText.IndexOf(quote, StringComparison.Ordinal);
        if (index < 0) return null;
        if (_markerVisibleText.IndexOf(quote, index + 1, StringComparison.Ordinal) >= 0)
        {
            var before = bookmark.ContextBefore ?? "";
            var after = bookmark.ContextAfter ?? "";
            if (before.Length == 0 && after.Length == 0)
            {
                // Questions without foldable sections retain their original offsets.
                if (_bookmarkSections.Count != 0 || anchor.TextOffset < 0 || anchor.TextOffset > _markerVisibleText.Length - quote.Length ||
                    !_markerVisibleText.AsSpan(anchor.TextOffset).StartsWith(quote)) return null;
                index = anchor.TextOffset;
            }
            else
            {
                var match = -1;
                for (var candidate = index; candidate >= 0; candidate = _markerVisibleText.IndexOf(quote, candidate + 1, StringComparison.Ordinal))
                {
                    var tail = candidate + quote.Length;
                    if (before.Length > candidate || after.Length > _markerVisibleText.Length - tail ||
                        !_markerVisibleText.AsSpan(candidate - before.Length, before.Length).SequenceEqual(before) ||
                        !_markerVisibleText.AsSpan(tail, after.Length).SequenceEqual(after)) continue;
                    if (match >= 0) return null;
                    match = candidate;
                }
                if (match < 0) return null;
                index = match;
            }
        }
        return AnnotationPointer(bookmark.Id + ":visible", anchor with { TextOffset = index });
    }

    internal Point? BookmarkMarkerPoint(AiUserBookmark bookmark)
    {
        var pointer = ResolveUserBookmark(bookmark);
        if (pointer is null || !pointer.HasValidLayout) return null;
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return null;
        foreach (var table in _tableViews)
        {
            if (!pointer.IsInSameDocument(table.View.Document.ContentStart)) continue;
            return table.View.TranslatePoint(rect.TopLeft, this);
        }
        return rect.TopLeft;
    }

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
