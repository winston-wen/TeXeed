using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed record AiInputReply(string[] SegmentIds, Dictionary<string, string> Values,
    Dictionary<string, string[]>? Attachments, DateTimeOffset At)
{
    public List<AiEntry> Files { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal static class AiTurnInput
{
    internal static AiTurn WithQuestion(AiTurn turn, IReadOnlyList<AiSegment> fresh)
    {
        var segments = turn.Segments.Count == 0 ? turn.DisplaySegments.ToList() : turn.Segments.ToList();
        var current = fresh.Where(s => s.Kind == "final").ToList();
        if (current.Count == 0 && segments.Count == 1) current.Add(segments[0]);
        var forms = AiForms.WithSubmissions(turn with { Answer = "", Segments = current, InputReplies = [], EndReason = null }, AiForms.AllFromTurn(turn));
        if (AiForms.Snapshot(forms) is null)
        {
            var fallback = new AiSegment { Kind = "final", Text = "{{textarea:clarification}}" };
            segments.Add(fallback);
            current.Add(fallback);
        }
        return turn with { Segments = segments, Answer = string.Join("\n\n", segments.Where(s => s.Kind == "final").Select(s => s.Text)),
            PendingInputSegmentIds = current.Select(s => s.Id).ToArray(), EndReason = "input", EndedAt = null, AnsweredAt = null,
            Incomplete = false, AdditionalData = forms.AdditionalData };
    }

    internal static bool Restore(AiConversation conversation)
    {
        var seenTasks = new HashSet<string>();
        var changed = false;
        for (var i = conversation.Turns.Count - 1; i >= 0; i--)
        {
            var turn = conversation.Turns[i];
            var lastTask = turn.RevisionRequest is { } request && seenTasks.Add(request.TaskId);
            if (turn.EndReason == "input-resuming")
                conversation.Turns[i] = turn with { InputAttemptId = null, EndReason = "program", EndedAt = turn.InputReplies.LastOrDefault()?.At ?? turn.SentAt };
            else if (turn.RevisionRequest is { AwaitingReply: true, Cancelled: false } && !turn.IsIncomplete && turn.EndReason == null && lastTask)
            {
                // Legacy sends could lock a clarification form even while its revision task still awaited a decision.
                var saved = AiForms.SubmittedFor(conversation, turn.Id);
                var waiting = WithQuestion(turn, turn.DisplaySegments);
                if (saved is not null)
                {
                    var draft = AiForms.Draft(waiting);
                    foreach (var pair in saved.Values) draft.TryAdd(pair.Key, pair.Value);
                    waiting.AdditionalData![AiForms.DraftKey] = JsonSerializer.SerializeToElement(draft);
                }
                conversation.Turns[i] = AppendToRevisions(conversation, turn, waiting);
            }
            else continue;
            changed = true;
        }
        return changed;
    }

    // Older clarification turns could already have an edit history and a separate
    // unfinished user draft. Append the new segments to both without rewriting history.
    internal static AiTurn AppendToRevisions(AiConversation conversation, AiTurn previous, AiTurn continued)
    {
        if (previous.RevisionHistory is not { Count: > 0 } && previous.UserRevisionDraft is null) return continued;
        var before = AiRevisionText.Segments(previous).ToList();
        var existing = before.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var segments = AiRevisionText.Segments(continued).ToList();
        var extra = segments.Where(s => !existing.Contains(s.Id)).ToList();
        if (extra.Count == 0) return continued;
        var added = extra.SelectMany(s => AiRevisionText.Parse(s.Id, s.Text)).ToList();
        if (previous.RevisionHistory is { Count: > 0 } history)
        {
            var revision = new AiTurnRevision(Guid.NewGuid().ToString("N"), "ai", DateTimeOffset.UtcNow,
                [.. history[^1].Rows, .. added])
            {
                Sources = segments.ToDictionary(s => s.Id, s => s.Text),
                EquationPreviews = AiRevisionEquationLinks.Capture(conversation, continued)
            };
            continued = continued with { RevisionHistory = [.. history, revision] };
        }
        if (previous.UserRevisionDraft is { } draft)
        {
            var oldPrefix = AiRevisionText.SourceVersion(previous, before) + "/";
            var version = draft.Version.StartsWith(oldPrefix, StringComparison.Ordinal)
                ? AiRevisionText.SourceVersion(continued, segments) + "/" + draft.Version[oldPrefix.Length..] : draft.Version;
            continued = continued with { UserRevisionDraft = draft with { Version = version, Rows = [.. draft.Rows, .. AiRevisionText.Clone(added)] } };
        }
        return continued;
    }
}
