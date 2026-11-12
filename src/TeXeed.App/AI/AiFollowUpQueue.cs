using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

// Stored in the conversation extension data so older versions retain pending work.
internal sealed class AiFollowUpMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "queue";
    public string Prompt { get; set; } = "";
    public List<AiEntry> Entries { get; set; } = [];
    public string State { get; set; } = "pending";
    public string? TargetTurnId { get; set; }
    public string? DispatchTurnId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    internal AiFollowUpMessage Snapshot() => new()
    {
        Id = Id, Kind = Kind, Prompt = Prompt, Entries = Entries.Select(e => e.Snapshot()).ToList(),
        State = State, TargetTurnId = TargetTurnId, DispatchTurnId = DispatchTurnId,
        Error = Error, CreatedAt = CreatedAt, AdditionalData = AdditionalData is null ? null : new(AdditionalData)
    };
}

internal sealed class AiFollowUpQueue
{
    internal const string StorageKey = "__texeed_followups_v1";

    private sealed class QueueData
    {
        public int Version { get; set; } = 1;
        public bool Paused { get; set; }
        public string? PauseReason { get; set; }
        public List<AiFollowUpMessage> Items { get; set; } = [];
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }

    private readonly string _conversationId;
    private readonly QueueData _data;
    private AiFollowUpQueue(string conversationId, QueueData data) { _conversationId = conversationId; _data = data; }

    internal IReadOnlyList<AiFollowUpMessage> Items => _data.Items.Where(item => item.State != "accepted").Select(item => item.Snapshot()).ToArray();
    internal IReadOnlyList<AiFollowUpMessage> History => _data.Items.Where(item => item.State == "accepted").Select(item => item.Snapshot()).ToArray();
    internal bool IsPaused => _data.Paused;
    internal string? PauseReason => _data.PauseReason;

    internal static AiFollowUpQueue Load(AiConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.AdditionalData?.TryGetValue(StorageKey, out var raw) != true)
            return new(conversation.Id, new());
        try
        {
            var data = raw.Deserialize<QueueData>() ?? throw new InvalidDataException("排队数据为空。");
            if (data.Version != 1 || data.Items is null || data.Items.Any(i => i is null ||
                    string.IsNullOrWhiteSpace(i.Id) || i.Kind is null || i.State is null || i.Prompt is null ||
                    i.Entries is null || i.Entries.Any(e => e is null)) ||
                data.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != data.Items.Count)
                throw new InvalidDataException("排队数据格式不受支持或存在重复消息。 ");
            return new(conversation.Id, data);
        }
        catch (JsonException ex) { throw new InvalidDataException("无法读取排队数据。", ex); }
        catch (NotSupportedException ex) { throw new InvalidDataException("无法读取排队数据。", ex); }
    }

    internal AiFollowUpMessage Enqueue(string kind, string prompt, IReadOnlyList<AiEntry> entries, string? targetTurnId = null)
    {
        if (kind is not ("queue" or "steer")) throw new ArgumentException("未知消息类型。", nameof(kind));
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(prompt) && entries.Count == 0) throw new ArgumentException("消息不能为空。", nameof(prompt));
        if (kind == "steer" && string.IsNullOrWhiteSpace(targetTurnId)) throw new ArgumentException("引导必须关联正在运行的轮次。", nameof(targetTurnId));
        var item = new AiFollowUpMessage { Kind = kind, Prompt = prompt, Entries = entries.Select(e => e.Snapshot()).ToList(), TargetTurnId = targetTurnId };
        _data.Items.Add(item);
        return item.Snapshot();
    }

    internal bool Edit(string id, string prompt, IReadOnlyList<AiEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(prompt) && entries.Count == 0) return false;
        var item = _data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.State is not ("pending" or "failed")) return false;
        item.Prompt = prompt;
        item.Entries = entries.Select(e => e.Snapshot()).ToList();
        return true;
    }

    internal bool Remove(string id)
    {
        var item = _data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.State == "dispatching" || item.Kind == "steer" && item.State == "pending") return false;
        return _data.Items.Remove(item);
    }

    internal bool Move(string id, int delta)
    {
        var index = _data.Items.FindIndex(i => i.Id == id);
        if (index < 0 || delta is not (-1 or 1) || _data.Items[index].Kind != "queue" || _data.Items[index].State != "pending") return false;
        var other = index + delta;
        while (other >= 0 && other < _data.Items.Count && _data.Items[other].State == "accepted") other += delta;
        if (other < 0 || other >= _data.Items.Count || _data.Items[other].Kind != "queue" || _data.Items[other].State != "pending") return false;
        (_data.Items[index], _data.Items[other]) = (_data.Items[other], _data.Items[index]);
        return true;
    }

    internal bool MarkDispatching(IReadOnlyList<string> ids, string? turnId)
    {
        var items = FindExact(ids);
        if (items is null || items.Any(i => i.State != "pending")) return false;
        foreach (var item in items) { item.State = "dispatching"; item.DispatchTurnId = turnId; item.Error = null; }
        return true;
    }

    internal bool Complete(IReadOnlyList<string> ids)
    {
        var items = FindExact(ids);
        if (items is null || items.Any(i => i.State != "dispatching")) return false;
        foreach (var item in items)
        {
            if (item.Kind == "steer") { item.State = "accepted"; item.Error = null; }
            else _data.Items.Remove(item);
        }
        return true;
    }

    internal bool MarkUnknown(IReadOnlyList<string> ids, string reason) => MarkProblem(ids, "unknown", reason);
    internal bool MarkFailed(IReadOnlyList<string> ids, string reason) => MarkProblem(ids, "failed", reason);

    private bool MarkProblem(IReadOnlyList<string> ids, string state, string reason)
    {
        var items = FindExact(ids);
        if (items is null || items.Any(i => i.State != "dispatching")) return false;
        foreach (var item in items) { item.State = state; item.Error = reason; }
        Pause(reason);
        return true;
    }

    internal void Pause(string reason) { _data.Paused = true; _data.PauseReason = reason; }
    internal void Resume() { _data.Paused = false; _data.PauseReason = null; }

    // Retry is a user decision: an unknown result must never be sent again automatically.
    internal bool Retry(string id)
    {
        var item = _data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.State is not ("failed" or "unknown")) return false;
        item.State = "pending"; item.Error = null; item.DispatchTurnId = null;
        return true;
    }

    internal bool RetryAsQueue(string id)
    {
        var item = _data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.Kind != "steer" || item.State is not ("failed" or "unknown")) return false;
        item.Kind = "queue"; item.State = "pending"; item.Error = null; item.DispatchTurnId = null;
        return true;
    }

    internal AiFollowUpMessage? ConvertToSteer(string id, string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return null;
        var item = _data.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.Kind != "queue" || item.State != "pending") return null;
        item.Kind = "steer"; item.TargetTurnId = turnId;
        return item.Snapshot();
    }

    internal bool MarkUnsubmittedSteers(string turnId)
    {
        var pending = _data.Items.Where(i => i.Kind == "steer" && (i.TargetTurnId ?? "") == turnId && i.State == "pending").ToArray();
        if (pending.Length == 0) return false;
        const string reason = "原轮次已结束，引导尚未发送。";
        foreach (var item in pending) { item.State = "failed"; item.Error = reason; }
        Pause(reason);
        return true;
    }

    internal bool RecoverInterrupted()
    {
        var interrupted = _data.Items.Where(i => i.State == "dispatching").ToArray();
        if (interrupted.Length == 0) return false;
        const string reason = "上次发送结果未知，请确认后再重试。";
        foreach (var item in interrupted) { item.State = "unknown"; item.Error = reason; }
        Pause(reason);
        return true;
    }

    internal bool RecoverWithoutActiveTurn()
    {
        var changed = RecoverInterrupted();
        foreach (var target in _data.Items.Where(i => i.Kind == "steer" && i.State == "pending")
                     .Select(i => i.TargetTurnId ?? "").Distinct(StringComparer.Ordinal).ToArray())
            changed |= MarkUnsubmittedSteers(target);
        if (!_data.Paused && _data.Items.Any(i => i.Kind == "queue" && i.State == "pending"))
        {
            Pause("队列已恢复，请选择是否继续发送。");
            changed = true;
        }
        return changed;
    }

    internal AiFollowUpMessage? NextQueued()
    {
        if (_data.Paused || _data.Items.Any(i => i.Kind is not ("queue" or "steer") || i.State is not ("pending" or "accepted"))) return null;
        var first = _data.Items.FirstOrDefault(i => i.Kind == "queue");
        return first?.State == "pending" ? first.Snapshot() : null;
    }

    internal void Store(AiConversation conversation)
    {
        if (conversation.Id != _conversationId) throw new InvalidOperationException("不能把排队消息写入其他会话。");
        var extensions = conversation.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(conversation.AdditionalData);
        if (_data.Items.Count == 0 && !_data.Paused && _data.AdditionalData is not { Count: > 0 }) extensions.Remove(StorageKey);
        else extensions[StorageKey] = JsonSerializer.SerializeToElement(_data);
        conversation.AdditionalData = extensions.Count == 0 ? null : extensions;
    }

    private List<AiFollowUpMessage>? FindExact(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return null;
        var items = ids.Select(id => _data.Items.FirstOrDefault(i => i.Id == id)).ToList();
        return items.Any(i => i is null) ? null : items.Select(i => i!).ToList();
    }
}
