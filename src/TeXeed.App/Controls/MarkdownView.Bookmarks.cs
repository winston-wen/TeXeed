using System.Text;
using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;
using Block = System.Windows.Documents.Block;

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
        var before = SelectionView().CopySource(SelectionView().Document.ContentStart, selection.Start);
        var after = SelectionView().CopySource(selection.End, SelectionView().Document.ContentEnd);
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

    private sealed record BranchTextBlock(Block Block, int Start, int Length, string[] FoldKeys);
    private string? _branchFullText;
    private List<BranchTextBlock>? _branchTextBlocks;
    private void InvalidateBranchText() { _branchFullText = null; _branchTextBlocks = null; }

    // Reconstruct the text of detached fold bodies without opening them in the reader.
    internal TextPointer? ResolveBranchAnchor(AiAnswerAnchor anchor, string? before, string? after, bool reveal = true)
    {
        if (anchor.Revision != Revision || string.IsNullOrWhiteSpace(anchor.Quote)) return null;
        if (_branchFullText is null || _branchTextBlocks is null) BuildBranchText();
        var source = _branchFullText!;
        var blocks = _branchTextBlocks!;
        var quote = anchor.Quote.Trim();
        var candidates = new List<int>();
        for (var index = source.IndexOf(quote, StringComparison.Ordinal); index >= 0; index = source.IndexOf(quote, index + 1, StringComparison.Ordinal)) candidates.Add(index);
        if (candidates.Count == 0) return null;
        int match;
        if (candidates.Count == 1) match = candidates[0];
        else
        {
            var left = before ?? ""; var right = after ?? "";
            // Fold labels are presentation text; they can change when unrelated sections open.
            var useLeft = left.Length > 0 && !left.Contains("（已折叠）", StringComparison.Ordinal);
            var useRight = right.Length > 0 && !right.Contains("（已折叠）", StringComparison.Ordinal);
            if (!useLeft && !useRight) return null;
            var matches = candidates.Where(index =>
                (!useLeft || index >= left.Length && source.AsSpan(index - left.Length, left.Length).SequenceEqual(left)) &&
                (!useRight || index + quote.Length + right.Length <= source.Length && source.AsSpan(index + quote.Length, right.Length).SequenceEqual(right))).ToArray();
            if (matches.Length != 1) return null;
            match = matches[0];
        }
        var target = blocks.FirstOrDefault(block => match >= block.Start && match < block.Start + block.Length);
        if (target is null) return null;
        if (reveal) RevealBookmarkFolds(target.FoldKeys);
        else if (target.FoldKeys.Any(key => _folds?.GetValueOrDefault(key) == true)) return null;
        if (!target.Block.ElementStart.IsInSameDocument(Document.ContentStart)) return null;
        var offset = CopySource(Document.ContentStart, target.Block.ElementStart).Length + match - target.Start;
        return AnnotationPointer("branch-source:" + anchor.TurnId + ":" + anchor.SegmentId + ":" + offset,
            anchor with { TextOffset = offset });
    }

    private void BuildBranchText()
    {
        var text = new StringBuilder();
        var blocks = new List<BranchTextBlock>();
        var parents = new List<string>();
        void Append(Block block)
        {
            if (block is Section section)
            {
                if (_foldBodies.TryGetValue(section, out var body))
                {
                    foreach (var child in section.Blocks.Where(item => !ReferenceEquals(item, body))) Append(child);
                    parents.Add(_bookmarkSections[body]);
                    Append(body);
                    parents.RemoveAt(parents.Count - 1);
                }
                else foreach (var child in section.Blocks) Append(child);
                return;
            }
            var start = text.Length;
            if (block is Paragraph paragraph && _foldHints.TryGetValue(paragraph, out var hint) && hint.Parent is not null)
            {
                text.Append(CopySource(block.ElementStart, hint.ElementStart));
                text.Append(CopySource(hint.ElementEnd, block.ElementEnd));
            }
            else text.Append(CopySource(block.ElementStart, block.ElementEnd));
            if (text.Length > start) blocks.Add(new(block, start, text.Length - start, [.. parents]));
        }
        foreach (var block in Document.Blocks) Append(block);
        _branchFullText = text.ToString();
        _branchTextBlocks = blocks;
    }

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
