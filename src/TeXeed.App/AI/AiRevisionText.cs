using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

public sealed record AiRevisionLink(string Id, int Offset, string Label);
public sealed record AiRevisionRow(string Id, string SegmentId, string Text, int Level, bool Locked, List<AiRevisionLink> Links)
{
    public string Before { get; init; } = "\n\n";
    public string? DocumentPrefix { get; init; }
    public string? DocumentSuffix { get; init; }
    public string? OriginalMarkup { get; init; }
    public string? OriginalText { get; init; }
    public int OriginalLevel { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}
public sealed record AiRevisionDraft(string TurnId, string Version, List<AiRevisionRow> Rows);
public sealed record AiTurnRevision(string Id, string Author, DateTimeOffset At, List<AiRevisionRow> Rows)
{
    public Dictionary<string, string> Sources { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OperationId { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal static class AiRevisionText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseCustomContainers().UsePipeTables().DisableHtml().Build();
    internal static List<AiRevisionRow> Clone(IEnumerable<AiRevisionRow> rows) => rows.Select(row => row with { Links = [.. row.Links] }).ToList();
    internal static IEnumerable<AiSegment> Segments(AiTurn turn) => turn.Segments.Count == 0
        ? [new AiSegment { Id = turn.Id + "-answer", Kind = "final", Text = turn.Answer, CreatedAt = turn.CreatedAt }]
        : turn.Segments.Where(s => s.Kind != "activity" && !s.IsTransientStatus && s.Id != "request");

    internal static string Markup(AiRevisionRow row) =>
        row.OriginalMarkup is { } raw && row.Text == row.OriginalText && row.Level == row.OriginalLevel
            ? raw : row.Level == 0 ? row.Text : new string('#', row.Level) + " " + row.Text;

    internal static string Source(IReadOnlyList<AiRevisionRow> rows, bool includeMargins = true)
    {
        var result = new StringBuilder();
        if (includeMargins && rows.Count > 0) result.Append(rows[0].DocumentPrefix);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0)
            {
                var separator = row.Before;
                var previous = rows[i - 1];
                // Two adjacent headings need only one newline. If one is converted
                // to prose the same separator can fuse both saved paragraph owners.
                if (separator.Length == 0 || row.Level == 0 && previous.Level == 0 &&
                    (row.OriginalLevel > 0 || previous.OriginalLevel > 0) && separator.Count(c => c == '\n') < 2)
                    separator = separator.Contains('\r') ? "\r\n\r\n" : "\n\n";
                result.Append(separator);
            }
            result.Append(Markup(row));
        }
        if (includeMargins && rows.Count > 0) result.Append(rows[^1].DocumentSuffix);
        return result.ToString();
    }

    internal static List<AiRevisionRow> Parse(string segmentId, string source)
    {
        var math = new ChatMath();
        var prepared = math.Prepare(source, Pipeline);
        var document = Markdown.Parse(prepared, Pipeline);
        var rows = new List<AiRevisionRow>();
        var previous = 0;
        void Add(Markdig.Syntax.Block block)
        {
            var start = Math.Clamp(math.OriginalPosition(block.Span.Start), previous, source.Length);
            if (start > previous)
            {
                var lineStart = Math.Max(previous, source.LastIndexOf('\n', start - 1) + 1);
                if (source.AsSpan(lineStart, start - lineStart).IndexOfAnyExcept(' ', '\t') < 0) start = lineStart;
            }
            var end = Math.Clamp(math.OriginalPosition(block.Span.End + 1), start, source.Length);
            if (end == start) return;
            var raw = source[start..end];
            var level = block is HeadingBlock heading ? heading.Level : 0;
            var text = raw;
            if (level > 0)
            {
                var prefix = Regex.Match(raw, @"^\s{0,3}#{1,6}[ \t]+");
                text = prefix.Success ? Regex.Replace(raw[prefix.Length..], @"[ \t]+#+[ \t]*$", "") : raw.Split('\n')[0].TrimEnd('\r');
            }
            var locked = block is CodeBlock or LinkReferenceDefinition or Markdig.Extensions.Tables.Table or Markdig.Extensions.CustomContainers.CustomContainer ||
                math.Tokens.Values.Any(token => token.Display && token.OriginalOffset >= start && token.OriginalOffset + token.OriginalLength == end &&
                    string.IsNullOrWhiteSpace(source[start..token.OriginalOffset]));
            var canonical = level == 0 ? text : new string('#', level) + " " + text;
            rows.Add(new(segmentId + "/" + start, segmentId, text, level, locked, [])
                { Before = source[previous..start], OriginalMarkup = raw == canonical ? null : raw,
                  OriginalText = raw == canonical ? null : text, OriginalLevel = level });
            previous = end;
        }
        // Markdig collects non-adjacent reference definitions in a synthetic group.
        // Its span is relative to the definitions, not to the document.
        var blocks = document.SelectMany(block => block is ListBlock or LinkReferenceDefinitionGroup
            ? ((ContainerBlock)block).AsEnumerable() : [block]);
        foreach (var block in blocks.OrderBy(block => block.Span.Start)) Add(block);
        if (rows.Count == 0) rows.Add(new(segmentId + "/0", segmentId, source, 0, false, []));
        else
        {
            rows[0] = rows[0] with { DocumentPrefix = rows[0].Before.Length == 0 ? null : rows[0].Before };
            if (previous < source.Length) rows[^1] = rows[^1] with { DocumentSuffix = source[previous..] };
        }
        return rows;
    }

    internal static IReadOnlyList<(int Start, int Length)> ProtectedSpans(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['$', '\\', '{', '!', '|', '~', (char)96]) < 0) return [];
        var math = new ChatMath();
        math.Prepare(text, Pipeline);
        var spans = math.Tokens.Values.Select(token => (Start: token.OriginalOffset, Length: token.OriginalLength)).ToList();
        spans.AddRange(AiForms.Token.Matches(text).Select(m => (m.Index, m.Length)));
        var parsed = Markdown.Parse(text, Pipeline);
        foreach (var item in parsed.Descendants().Where(x => x is LinkInline { IsImage: true } or CodeInline or CodeBlock or Markdig.Extensions.Tables.Table))
            spans.Add((item.Span.Start, item.Span.Length));
        var result = new List<(int Start, int Length)>();
        foreach (var span in spans.OrderBy(x => x.Start).ThenByDescending(x => x.Length))
        {
            if (span.Length <= 0 || span.Start < 0 || span.Start + span.Length > text.Length) continue;
            if (result.Count != 0 && span.Start < result[^1].Start + result[^1].Length) continue;
            result.Add(span);
        }
        return result;
    }

    // Old excerpts contain displayed text. Markdig spans let us recover the original
    // Markdown offset even when a selection crosses emphasis, a link or inline TeX.
    internal static (string Text, List<int> Starts, List<int> Ends) SelectionText(string raw)
    {
        var math = new ChatMath();
        var prepared = math.Prepare(raw, Pipeline);
        var parsed = Markdown.Parse(prepared, Pipeline);
        var text = new StringBuilder(); var starts = new List<int>(); var ends = new List<int>();
        void Append(string value, int start, int end, bool atomic)
        {
            text.Append(value);
            for (var i = 0; i < value.Length; i++)
            {
                starts.Add(atomic ? start : Math.Min(start + i, end));
                ends.Add(atomic ? end : Math.Min(start + i + 1, end));
            }
        }
        void Literal(LiteralInline literal)
        {
            var value = literal.Content.ToString();
            var position = 0;
            while (position < value.Length)
            {
                var next = value.IndexOf(math.Prefix, position, StringComparison.Ordinal);
                if (next < 0) next = value.Length;
                if (next > position)
                {
                    var start = math.OriginalPosition(literal.Content.Start + position);
                    Append(value[position..next], start, Math.Min(raw.Length, start + next - position), false);
                }
                if (next == value.Length) break;
                var end = value.IndexOf("END", next + math.Prefix.Length, StringComparison.Ordinal);
                if (end < 0 || !math.Tokens.TryGetValue(value[next..(end + 3)], out var token)) break;
                var visible = token.Reference != null ? token.Reference : token.Display ? "$$" + token.Source +
                    (token.Label is null ? "" : @"\label{" + token.Label + "}") + @"\tag{" + token.Number + "}$$" : "$" + token.Source + "$";
                Append(visible, token.OriginalOffset, token.OriginalOffset + token.OriginalLength, true);
                position = end + 3;
            }
        }
        void Visit(ContainerInline container)
        {
            foreach (var element in container)
            {
                var start = math.OriginalPosition(element.Span.Start);
                var end = math.OriginalPosition(element.Span.End + 1);
                if (element is LiteralInline literal) Literal(literal);
                else if (element is CodeInline code) Append(code.Content, start + code.DelimiterCount, end, false);
                else if (element is LineBreakInline) Append("\n", start, end, true);
                else if (element is ContainerInline nested) Visit(nested);
            }
        }
        foreach (var leaf in parsed.Descendants<LeafBlock>())
            if (leaf.Inline is { } inline) Visit(inline);
        return (text.ToString(), starts, ends);
    }

    internal static List<AiRevisionRow> Normalize(AiRevisionDraft draft, IReadOnlyList<AiRevisionRow> proposed)
    {
        var previous = draft.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var segments = draft.Rows.Select(row => row.SegmentId).Distinct().ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<AiRevisionRow>();
        var lastSegment = -1;
        foreach (var row in proposed)
        {
            var index = segments.IndexOf(row.SegmentId);
            if (index < 0 || index < lastSegment || row.Level is < 0 or > 6 || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id))
                throw new InvalidOperationException("段落的标识、层级或所属回答片段有误。");
            lastSegment = index;
            previous.TryGetValue(row.Id, out var old);
            if (row.Locked != (old?.Locked ?? false) || row.Locked && (old!.Text != row.Text || old.Level != row.Level || old.SegmentId != row.SegmentId))
                throw new InvalidOperationException("该段落中的公式、表格或代码须完整保留。");
            if (row.Links.Any(link => link.Offset < 0 || link.Offset > row.Text.Length) ||
                !row.Links.Select(link => link.Offset).SequenceEqual(row.Links.Select(link => link.Offset).Order()))
                throw new InvalidOperationException("引用的位置超出了段落，或与正文次序不符。");
            rows.Add(old is null ? row with { Links = [.. row.Links], Before = "\n\n", OriginalMarkup = null, OriginalText = null } :
                old with { SegmentId = row.SegmentId, Text = row.Text, Level = row.Level, Links = [.. row.Links] });
        }
        static IEnumerable<string> Atoms(IEnumerable<AiRevisionRow> list) => list.SelectMany(r => r.Locked
            ? [r.Id + "\n" + r.Text] : ProtectedSpans(r.Text).Select(s => r.Text.Substring(s.Start, s.Length)));
        if (!Atoms(draft.Rows).SequenceEqual(Atoms(rows))) throw new InvalidOperationException("修改须保全公式、表格和其他内嵌对象。");
        var links = draft.Rows.SelectMany(r => r.Links).ToDictionary(l => l.Id, StringComparer.Ordinal);
        if (!links.Keys.SequenceEqual(rows.SelectMany(r => r.Links).Select(l => l.Id)))
            throw new InvalidOperationException("修改须保全每条引用的唯一归属和先后次序。");
        foreach (var row in rows)
            for (var i = 0; i < row.Links.Count; i++) row.Links[i] = links[row.Links[i].Id] with { Offset = row.Links[i].Offset };
        return rows;
    }
}
