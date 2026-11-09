using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed record AiDocumentAuthorSpan(int Start, int Length, string Author, string Role = "user");
public sealed record AiDocumentEdit(int Start, int Length, string Text);

public sealed class AiDocumentReply
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Author { get; set; } = "用户";
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    internal AiDocumentReply Snapshot() => new() { Id = Id, Author = Author, Role = Role, Text = Text, CreatedAt = CreatedAt };
}

public sealed class AiDocumentComment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Author { get; set; } = "用户";
    public string Role { get; set; } = "user";
    public int Start { get; set; }
    public int Length { get; set; }
    public string Quote { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Orphaned { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiDocumentReply> Replies { get; set; } = [];

    internal AiDocumentComment Snapshot() => new()
    {
        Id = Id, Author = Author, Role = Role, Start = Start, Length = Length, Quote = Quote,
        Text = Text, Orphaned = Orphaned,
        CreatedAt = CreatedAt, Replies = Replies.Select(reply => reply.Snapshot()).ToList()
    };
}

/// <summary>The authoritative LaTeX source and its review data for one conversation.</summary>
public sealed class AiSharedDocument
{
    [JsonIgnore] private readonly object _gate = new();
    public string Source { get; set; } = "";
    public string InitialSource { get; set; } = "";
    public List<AiDocumentAuthorSpan> AuthorSpans { get; set; } = [];
    public List<AiDocumentComment> Comments { get; set; } = [];
    [JsonIgnore] public string Revision { get { lock (_gate) return AiStorage.Hash(Source); } }

    public bool TryInitialize(string source, string author, string role = "ai")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        if (role is not ("user" or "ai")) throw new ArgumentException("Unknown author role.", nameof(role));
        if (string.IsNullOrWhiteSpace(source) || !ValidUtf16(source)) return false;
        lock (_gate)
        {
            if (Source.Length != 0 || InitialSource.Length != 0) return false;
            Source = InitialSource = source;
            AuthorSpans = [new(0, source.Length, author, role)];
            return true;
        }
    }

    /// <summary>Applies a source update only if the source has not changed since it was read.</summary>
    public bool TryApply(string expectedRevision, string newSource, string author, string role = "user")
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(newSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        if (role is not ("user" or "ai")) throw new ArgumentException("Unknown author role.", nameof(role));
        lock (_gate)
        {
            if (!ValidUtf16(Source) || !ValidUtf16(newSource)) return false;
            if (!string.Equals(expectedRevision, AiStorage.Hash(Source), StringComparison.Ordinal)) return false;
            var old = Source;
            if (old == newSource) return true;
            var start = 0;
            while (start < old.Length && start < newSource.Length && old[start] == newSource[start]) start++;
            if (start > 0 && ((start < old.Length && char.IsLowSurrogate(old[start])) || (start < newSource.Length && char.IsLowSurrogate(newSource[start])))) start--;
            var oldEnd = old.Length; var newEnd = newSource.Length;
            while (oldEnd > start && newEnd > start && old[oldEnd - 1] == newSource[newEnd - 1]) { oldEnd--; newEnd--; }
            if (oldEnd < old.Length && char.IsLowSurrogate(old[oldEnd])) oldEnd++;
            if (newEnd < newSource.Length && char.IsLowSurrogate(newSource[newEnd])) newEnd++;
            var added = newEnd - start;
            var delta = added - (oldEnd - start);
            var spans = new List<AiDocumentAuthorSpan>();
            foreach (var span in AuthorSpans)
            {
                var end = checked(span.Start + span.Length);
                if (span.Start < start) spans.Add(new(span.Start, Math.Min(end, start) - span.Start, span.Author, span.Role));
                if (end > oldEnd)
                {
                    var after = Math.Max(span.Start, oldEnd);
                    spans.Add(new(after + delta, end - after, span.Author, span.Role));
                }
            }
            if (added > 0) spans.Add(new(start, added, author, role));
            AuthorSpans = MergeSpans(spans);
            foreach (var comment in Comments.Where(c => !c.Orphaned))
            {
                var end = comment.Start + comment.Length;
                if (oldEnd <= comment.Start) comment.Start += delta;
                else if (start >= end) continue;
                else if (start <= comment.Start && oldEnd >= end) comment.Orphaned = true;
                else
                {
                    var updatedStart = Math.Min(comment.Start, start);
                    var updatedEnd = Math.Max(updatedStart, end + delta);
                    comment.Start = updatedStart;
                    comment.Length = updatedEnd - updatedStart;
                    if (comment.Length == 0) comment.Orphaned = true;
                }
            }
            Source = newSource;
            return true;
        }
    }

    /// <summary>Applies non-overlapping edits against one source revision as a single update.</summary>
    public bool TryApplyEdits(string expectedRevision, IReadOnlyList<AiDocumentEdit> edits, string author, string role = "user")
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        if (role is not ("user" or "ai")) throw new ArgumentException("Unknown author role.", nameof(role));
        lock (_gate)
        {
            if (!ValidUtf16(Source)) return false;
            if (!string.Equals(expectedRevision, AiStorage.Hash(Source), StringComparison.Ordinal)) return false;
            var ordered = edits.OrderBy(edit => edit.Start).ToArray();
            var previousEnd = -1;
            var previousStart = -1;
            foreach (var edit in ordered)
            {
                if (edit.Text is null || !ValidUtf16(edit.Text) || edit.Start < 0 || edit.Length < 0 || edit.Start > Source.Length || edit.Length > Source.Length - edit.Start)
                    return false;
                var end = edit.Start + edit.Length;
                if (edit.Start < previousEnd || edit.Start == previousStart) return false;
                if (edit.Start > 0 && edit.Start < Source.Length && char.IsLowSurrogate(Source[edit.Start]) && char.IsHighSurrogate(Source[edit.Start - 1])) return false;
                if (end > 0 && end < Source.Length && char.IsLowSurrogate(Source[end]) && char.IsHighSurrogate(Source[end - 1])) return false;
                previousStart = edit.Start; previousEnd = end;
            }
            var candidate = Snapshot();
            foreach (var edit in ordered.Reverse())
            {
                var updated = candidate.Source[..edit.Start] + edit.Text + candidate.Source[(edit.Start + edit.Length)..];
                if (!candidate.TryApply(candidate.Revision, updated, author, role)) return false;
            }
            Source = candidate.Source;
            AuthorSpans = candidate.AuthorSpans;
            for (var i = 0; i < Comments.Count; i++)
            { Comments[i].Start = candidate.Comments[i].Start; Comments[i].Length = candidate.Comments[i].Length; Comments[i].Orphaned = candidate.Comments[i].Orphaned; }
            return true;
        }
    }

    private static List<AiDocumentAuthorSpan> MergeSpans(IEnumerable<AiDocumentAuthorSpan> input)
    {
        var output = new List<AiDocumentAuthorSpan>();
        foreach (var span in input.Where(s => s.Length > 0).OrderBy(s => s.Start))
        {
            if (output.Count > 0 && output[^1].Start + output[^1].Length == span.Start && output[^1].Author == span.Author && output[^1].Role == span.Role)
                output[^1] = output[^1] with { Length = output[^1].Length + span.Length };
            else output.Add(span);
        }
        return output;
    }

    private static bool ValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    public AiSharedDocument Snapshot()
    {
        lock (_gate) return new() { Source = Source, InitialSource = InitialSource, AuthorSpans = [.. AuthorSpans], Comments = Comments.Select(c => c.Snapshot()).ToList() };
    }

    internal bool TryRestore(AiSharedDocument expected, AiSharedDocument previous)
    {
        lock (_gate)
        {
            if (System.Text.Json.JsonSerializer.Serialize(Snapshot()) != System.Text.Json.JsonSerializer.Serialize(expected)) return false;
            var restored = previous.Snapshot();
            if (!ValidUtf16(restored.Source) || !ValidUtf16(restored.InitialSource)) return false;
            Source = restored.Source; InitialSource = restored.InitialSource;
            AuthorSpans = restored.AuthorSpans; Comments = restored.Comments;
            return true;
        }
    }
}
