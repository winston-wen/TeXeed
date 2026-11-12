using System.Text.Json;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private const string SteeringHistoryKey = "__texeed_steering_history_v1";

    private static AiTurn AttachSteeringHistory(AiTurn turn, IReadOnlyList<AiFollowUpMessage> history)
    {
        var items = history.Where(item => item.TargetTurnId == turn.Id).ToArray();
        if (items.Length == 0) return turn;
        var extra = turn.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(turn.AdditionalData);
        extra[SteeringHistoryKey] = JsonSerializer.SerializeToElement(items);
        return turn with { AdditionalData = extra };
    }

    private static AiTurn WithSteeringContext(AiTurn turn, IReadOnlyList<AiFollowUpMessage> history)
    {
        var items = turn.AdditionalData?.TryGetValue(SteeringHistoryKey, out var raw) == true
            ? raw.Deserialize<List<AiFollowUpMessage>>() ?? []
            : history.Where(item => item.TargetTurnId == turn.Id).ToList();
        if (items.Count == 0) return turn;
        return turn with
        {
            Question = turn.Question + string.Concat(items.Select(item => "\n引导：" + item.Prompt)),
            SentAnnotations = turn.SentAnnotations.Concat(items.SelectMany(item => item.Entries)).Select(entry => entry.Snapshot()).ToList()
        };
    }
}
