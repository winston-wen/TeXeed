using System.Windows.Documents;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private sealed record RevisionBlockRange(int Start, int End, int TextStart, AiRevisionRow Row);
    private readonly record struct RevisionParagraph(MarkdownView View, Paragraph Paragraph, int Start, int End, int Distance);
    private List<RevisionBlockRange>? _revisionRanges;
    private Dictionary<string, int>? _revisionLinks;
    private Dictionary<string, RevisionParagraph>? _revisionBlocks;

    internal string SetRevisionSource(string markdown, IEnumerable<AiRevisionRow> sourceRows)
    {
        var rows = sourceRows.ToArray();
        if (!rows.Any(row => row.Links.Count > 0))
        {
            _revisionRanges = null;
            _revisionLinks = null;
            _revisionBlocks = null;
            return markdown;
        }
        _revisionRanges = [];
        _revisionLinks = [];
        _revisionBlocks = [];
        var position = 0;
        foreach (var row in rows)
        {
            var markup = AiRevisionText.Markup(row);
            var start = markdown.IndexOf(markup, position, StringComparison.Ordinal);
            if (start < 0) continue;
            // Markdig omits an empty paragraph. Give a reference that has no preceding
            // prose a real layout container, then remove the temporary glyph on render.
            if (markup.Length == 0 && row.Links.Count > 0)
            {
                if (position != 0 && markdown.AsSpan(position).StartsWith(row.Before)) start += row.Before.Length;
                markdown = markdown.Insert(start, "\u2063");
                markup = "\u2063";
            }
            position = start + markup.Length;
            if (row.Links.Count == 0) continue;
            var textStart = start + (row.Level > 0 ? Math.Max(0, markup.IndexOf(row.Text, StringComparison.Ordinal)) : 0);
            _revisionRanges.Add(new(start, position, textStart, row));
            foreach (var link in row.Links) _revisionLinks.Add(link.Id, textStart + link.Offset);
        }
        return markdown;
    }

    private void TrackRevisionBlock(Markdig.Syntax.Block source, Block? block)
    {
        if (_revisionRanges is null || block is not Paragraph paragraph || source is not Markdig.Syntax.LeafBlock) return;
        var owner = _tableOwner ?? this;
        var start = _math.OriginalPosition(source.Span.Start);
        var end = _math.OriginalPosition(source.Span.End + 1);
        foreach (var range in _revisionRanges)
        {
            if (range.End <= start || range.Start >= end) continue;
            if (range.Row.Text.Length == 0 && paragraph.Inlines.FirstInline is Run { Text: "\u2063" } placeholder)
                placeholder.Text = "";
            foreach (var link in range.Row.Links)
            {
                var position = range.TextStart + link.Offset;
                // Quotes, list items and tables can render a single stored row into
                // multiple paragraphs. Positions inside Markdown delimiters use the nearest leaf.
                var distance = Math.Max(0, Math.Max(start - position, position - end));
                var blocks = owner._revisionBlocks!;
                if (!blocks.TryGetValue(link.Id, out var current) || distance < current.Distance)
                    blocks[link.Id] = new(this, paragraph, start, end, distance);
            }
        }
    }

    internal bool HasRevisionLink(string id) => _revisionLinks?.ContainsKey(id) == true;

    internal TextPointer? RevisionPointer(string id, bool reveal)
    {
        if (_revisionLinks is null || !_revisionLinks.TryGetValue(id, out var position) || !_revisionBlocks!.TryGetValue(id, out var target)) return null;
        var paragraph = target.Paragraph;
        var owner = target.View;
        Block? block = owner == this ? paragraph : _tableViews.FirstOrDefault(t => t.View == owner)?.Block;
        if (block is null) return null;
        if (!block.ElementStart.IsInSameDocument(Document.ContentStart) && reveal)
        {
            if (_branchTextBlocks is null) BuildBranchText();
            var enclosing = _branchTextBlocks?.FirstOrDefault(b => b.Block == block || block.ContentStart.IsInSameDocument(b.Block.ContentStart) &&
                b.Block.ElementStart.CompareTo(block.ContentStart) <= 0 && block.ContentEnd.CompareTo(b.Block.ElementEnd) <= 0);
            if (enclosing is not null) RevealBookmarkFolds(enclosing.FoldKeys);
        }
        if (!block.ElementStart.IsInSameDocument(Document.ContentStart)) return null;
        if (position <= target.Start) return paragraph.ContentStart;
        if (position >= target.End) return paragraph.ContentEnd;
        var source = Markdown ?? "";
        if (target.Start < 0 || target.End > source.Length) return null;
        var visible = owner.CopySource(paragraph.ContentStart, paragraph.ContentEnd);
        var offset = MapRevisionOffset(source[target.Start..target.End], visible, position - target.Start);
        if (offset <= 0) return paragraph.ContentStart;
        if (offset >= visible.Length) return paragraph.ContentEnd;
        var prefix = owner.CopySourceLength(owner.Document.ContentStart, paragraph.ContentStart);
        var pointer = owner.BranchTextPointer(prefix + offset);
        return pointer?.Paragraph == paragraph ? pointer : paragraph.ContentStart;
    }

    // Markdown punctuation is absent from the reader. Match the rendered characters
    // to their source positions so an inline link stays between the same two words.
    internal static int MapRevisionOffset(string source, string visible, int position)
    {
        if (position <= 0) return 0;
        if (position >= source.Length) return visible.Length;
        if (source == visible) return position;
        var selection = AiRevisionText.SelectionText(source);
        if (selection.Text == visible) return selection.Ends.TakeWhile(end => end <= position).Count();
        var raw = 0; var rendered = 0;
        while (raw < position && rendered < visible.Length)
        {
            if (source[raw] == visible[rendered]) { raw++; rendered++; continue; }
            var nextRaw = source.IndexOf(visible[rendered], raw + 1);
            var nextVisible = visible.IndexOf(source[raw], rendered + 1);
            if (nextRaw >= 0 && (nextVisible < 0 || nextRaw - raw <= nextVisible - rendered)) raw = nextRaw;
            else if (nextVisible >= 0) rendered = nextVisible;
            else raw++;
        }
        return rendered;
    }
}
