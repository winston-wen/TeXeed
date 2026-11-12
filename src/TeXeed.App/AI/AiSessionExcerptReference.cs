using System.Text.Json;

namespace TeXeed.App.AI;

// An excerpt keeps its immutable text anchor while explicitly identifying the source conversation.
internal sealed record AiSessionExcerptReference(int Version, string SourceConversationId, string? ContextBefore, string? ContextAfter);

internal static class AiSessionExcerptReferences
{
    internal const string MetadataKey = "__texeed_session_excerpt_v1";
    internal const string OriginLockKey = "__texeed_branch_origin_lock_v1";

    internal static AiEntry Create(string sourceConversationId, AiAnswerAnchor anchor, string? before, string? after, bool protectOrigin = false)
    {
        if (string.IsNullOrWhiteSpace(sourceConversationId)) throw new ArgumentException("来源会话不能为空。", nameof(sourceConversationId));
        ArgumentNullException.ThrowIfNull(anchor);
        var entry = new AiEntry { Title = protectOrigin ? "追问出处" : "会话引用", AnswerAnchor = anchor };
        SetMetadata(entry, sourceConversationId, before, after, protectOrigin);
        return entry;
    }

    internal static void SetMetadata(AiEntry entry, string sourceConversationId, string? before, string? after, bool protectOrigin)
    {
        if (string.IsNullOrWhiteSpace(sourceConversationId)) throw new ArgumentException("来源会话不能为空。", nameof(sourceConversationId));
        var extra = entry.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(entry.AdditionalData);
        if (!extra.ContainsKey(MetadataKey))
            extra[MetadataKey] = JsonSerializer.SerializeToElement(new AiSessionExcerptReference(1, sourceConversationId, before, after));
        if (protectOrigin && !extra.ContainsKey(OriginLockKey)) extra[OriginLockKey] = JsonSerializer.SerializeToElement(true);
        entry.AdditionalData = extra;
    }

    internal static bool IsProtected(AiEntry entry) => entry.AdditionalData?.ContainsKey(OriginLockKey) == true;

    private static bool SameAnchor(AiAnswerAnchor left, AiAnswerAnchor right) =>
        left.TurnId == right.TurnId && left.SegmentId == right.SegmentId && left.Offset == right.Offset &&
        left.Length == right.Length && left.Quote == right.Quote && left.Revision == right.Revision;

    internal static bool IsBranchOrigin(AiConversation conversation, AiEntry entry) =>
        IsProtected(entry) || conversation.ParentConversationId is not null && conversation.BranchAnchor is { } anchor &&
        entry.Title == "追问出处" && entry.AnswerAnchor is { } actual && SameAnchor(actual, anchor);

    internal static bool TryRead(AiEntry entry, out AiSessionExcerptReference? reference)
    {
        reference = null;
        if (entry.AdditionalData?.TryGetValue(MetadataKey, out var raw) != true) return false;
        try
        {
            reference = raw.Deserialize<AiSessionExcerptReference>();
            return reference is { Version: 1 } && !string.IsNullOrWhiteSpace(reference.SourceConversationId);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return false; }
    }

    internal static bool TryResolve(AiNotebook book, AiEntry entry, out AiConversation? source, out string? before, out string? after)
    {
        source = null; before = null; after = null;
        if (entry.AnswerAnchor is not { } anchor) return false;
        bool Contains(AiConversation conversation) => conversation.Turns.Any(turn => turn.Id == anchor.TurnId &&
            turn.DisplaySegments.Any(segment => segment.Id == anchor.SegmentId));
        if (entry.AdditionalData?.ContainsKey(MetadataKey) == true)
        {
            if (!TryRead(entry, out var reference)) return false;
            var matches = book.Conversations.Where(conversation => conversation.Id == reference!.SourceConversationId).ToArray();
            if (matches.Length != 1 || !Contains(matches[0])) return false;
            source = matches[0];
            before = reference!.ContextBefore; after = reference.ContextAfter;
            return true;
        }
        var candidates = book.Conversations.Where(Contains).ToArray();
        if (candidates.Length != 1) return false;
        source = candidates[0];
        var active = book.Active;
        if (active.BranchAnchor is { } branch && branch.TurnId == anchor.TurnId && branch.SegmentId == anchor.SegmentId &&
            branch.Revision == anchor.Revision && branch.Quote == anchor.Quote)
        { before = active.BranchContextBefore; after = active.BranchContextAfter; }
        return true;
    }

    // Older branches did not retain their source in the pending list after the first send.
    internal static bool EnsureBranchOrigin(AiConversation conversation)
    {
        if (conversation.ParentConversationId is not { Length: > 0 } parent || conversation.BranchAnchor is not { } anchor) return false;
        var origin = conversation.Entries.FirstOrDefault(entry => entry.AnswerAnchor is { } actual &&
            SameAnchor(actual, anchor) && IsBranchOrigin(conversation, entry));
        if (origin is null)
        {
            origin = Create(parent, TeXeed.App.Controls.AiReferencePresentation.NormalizePreview(anchor), conversation.BranchContextBefore, conversation.BranchContextAfter, protectOrigin: true);
            if (conversation.Turns.Count > 0 || conversation.LastSentAt is not null) origin.ReferenceSelected = false;
            conversation.Entries.Insert(0, origin);
            return true;
        }
        if (IsProtected(origin) && origin.AdditionalData?.ContainsKey(MetadataKey) == true) return false;
        SetMetadata(origin, parent, conversation.BranchContextBefore, conversation.BranchContextAfter, protectOrigin: true);
        return true;
    }
}
