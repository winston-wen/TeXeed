using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed class AiCommentDraft
{
    public string Baseline { get; set; } = "";
    public string Text { get; set; } = "";
}
public sealed class AiCommentSelectionDraft
{
    public string Revision { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public string Quote { get; set; } = "";
}
public sealed class AiCommentDrafts
{
    public Dictionary<string, AiCommentDraft> Edits { get; set; } = [];
    public Dictionary<string, string> Replies { get; set; } = [];
    public AiCommentSelectionDraft? Selection { get; set; }
    public string NewComment { get; set; } = "";
    internal AiCommentDrafts Snapshot() => new()
    {
        Edits = Edits.ToDictionary(x => x.Key, x => new AiCommentDraft { Baseline = x.Value.Baseline, Text = x.Value.Text }),
        Replies = new(Replies),
        Selection = Selection is null ? null : new() { Revision = Selection.Revision, Start = Selection.Start, Length = Selection.Length, Quote = Selection.Quote },
        NewComment = NewComment
    };
}
