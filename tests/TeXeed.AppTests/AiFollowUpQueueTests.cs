using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private sealed class LegacyConversation
    {
        public string Id { get; set; } = "";
        public string Draft { get; set; } = "";
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }

    private static void CheckAiFollowUpQueue()
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        var conversation = new AiConversation { Draft = "keep draft" };
        conversation.AdditionalData = new() { ["other-version-data"] = JsonSerializer.SerializeToElement("preserve") };
        var entry = new AiEntry { Title = "reference", Question = "original", Files = [new("file.tex", "text/plain", "data")] };
        var queue = AiFollowUpQueue.Load(conversation);
        var first = queue.Enqueue("queue", "first", [entry]);
        entry.Question = "changed";
        entry.Files.Clear();
        var second = queue.Enqueue("queue", "second", []);
        Assert(queue.Items[0].Entries[0].Question == "original" && queue.Items[0].Entries[0].Files.Count == 1, "Queued references must be snapshots");
        queue.Store(conversation);
        Assert(conversation.Draft == "keep draft" && conversation.AdditionalData!.ContainsKey("other-version-data"), "Storing must preserve draft and extensions");
        var legacy = JsonSerializer.Deserialize<LegacyConversation>(JsonSerializer.Serialize(conversation))!;
        var restored = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(legacy))!;
        queue = AiFollowUpQueue.Load(restored);
        Assert(queue.Items.Count == 2 && queue.Items[0].Id == first.Id && restored.Draft == "keep draft", "Older round trip lost queued messages");
        Assert(queue.Move(second.Id, -1) && queue.NextQueued()!.Id == second.Id, "Queue ordering failed");
        Assert(queue.Edit(second.Id, "edited", []), "Pending edit failed");
        Assert(queue.MarkDispatching([second.Id], "turn"), "Dispatch transition failed");
        Assert(!queue.Remove(second.Id) && queue.NextQueued() is null, "In-flight work must not be removed or skipped");
        queue.Store(restored);
        var afterCrash = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(restored))!;
        queue = AiFollowUpQueue.Load(afterCrash);
        Assert(queue.RecoverInterrupted() && queue.IsPaused && queue.Items[0].State == "unknown", "Interrupted work must be unknown and paused");
        queue.Resume();
        Assert(queue.NextQueued() is null, "Unknown work must never be resent automatically");
        Assert(queue.Retry(second.Id) && queue.NextQueued()!.Id == second.Id, "Explicit retry failed");
        Assert(queue.MarkDispatching([second.Id], "turn2") && queue.MarkFailed([second.Id], "failed") && queue.IsPaused, "Failure must pause");
        queue.Resume();
        Assert(queue.NextQueued() is null, "Failed work must block following messages");
        Assert(queue.Retry(second.Id) && queue.MarkDispatching([second.Id], "turn3") && queue.Complete([second.Id]), "Queue completion failed");
        Assert(queue.NextQueued()!.Id == first.Id, "Queue must continue in order");
        var steer = queue.Enqueue("steer", "clarify", [], "turn3");
        Assert(!queue.Remove(steer.Id), "A steering message awaiting handoff cannot be deleted while the controller may send it");
        Assert(queue.MarkDispatching([steer.Id], "turn3") && queue.Complete([steer.Id]), "Steering completion failed");
        Assert(queue.History.Single().Prompt == "clarify" && queue.History[0].TargetTurnId == "turn3", "Accepted steering history must survive");
        queue.Store(afterCrash);
        Assert(AiFollowUpQueue.Load(JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(afterCrash))!).History.Count == 1, "Accepted steering history must persist");
        var ordered = AiFollowUpQueue.Load(new AiConversation());
        var a = ordered.Enqueue("queue", "a", []);
        var history = ordered.Enqueue("steer", "history", [], "turn");
        var b = ordered.Enqueue("queue", "b", []);
        Assert(ordered.MarkDispatching([history.Id], "turn") && ordered.Complete([history.Id]), "Fixture history setup failed");
        Assert(ordered.Move(b.Id, -1) && ordered.NextQueued()!.Id == b.Id, "Moving a queued item must skip invisible accepted history");
        Assert(ordered.MarkDispatching([a.Id], "turn") && !ordered.Move(b.Id, 1), "Moving must not cross an in-flight message");
        var different = new AiConversation();
        Assert(AiFollowUpQueue.Load(different).Items.Count == 0, "Queues must be conversation isolated");
        try { queue.Store(different); throw new Exception("Writing to another conversation was allowed"); }
        catch (InvalidOperationException) { }
        var empty = new AiConversation();
        var emptyQueue = AiFollowUpQueue.Load(empty);
        var pending = emptyQueue.Enqueue("queue", "keep", []);
        emptyQueue.Store(empty);
        var notebook = new AiNotebook { Conversations = [empty] };
        Assert(!notebook.IsEmptyNewConversation(empty), "A queued message must protect a new conversation");
        Assert(emptyQueue.Remove(pending.Id), "Pending removal failed");
        emptyQueue.Store(empty);
        Assert(notebook.IsEmptyNewConversation(empty), "A cleared queue must not protect a blank conversation");
        var bad = new AiConversation { AdditionalData = new() { [AiFollowUpQueue.StorageKey] = JsonSerializer.SerializeToElement("broken") } };
        try { AiFollowUpQueue.Load(bad); throw new Exception("Broken data must fail safely"); }
        catch (InvalidDataException) { }
        Assert(bad.AdditionalData[AiFollowUpQueue.StorageKey].GetString() == "broken", "Broken data must remain intact");
    }
}
