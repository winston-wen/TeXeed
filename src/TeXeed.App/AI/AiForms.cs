using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

internal static class AiForms
{
    internal const string DraftKey = "texeedFormDraftV1";
    internal const string SubmissionKey = "texeedFormSubmissionV1";
    internal const string SubmissionsKey = "texeedFormSubmissionsV1";
    private const string LockedKey = "texeedFormLockedV1";
    internal const string ProtocolKey = "texeedFormProtocolVersion";
    internal static readonly Regex Token = new(@"\{\{(?:(?<text>text|textarea):(?<tid>[A-Za-z0-9-]{1,64})|radio:(?<rid>[A-Za-z0-9-]{1,64}):(?<value>[A-Za-z0-9-]{1,64}):(?<rlabel>[^{}\r\n:|]{1,80})|checkbox:(?<cid>[A-Za-z0-9-]{1,64}):(?<clabel>[^{}\r\n:|]{1,80})|file:(?<fid>[A-Za-z0-9-]{1,64}):(?<flabel>[^{}\r\n:|]{1,80}))\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseCustomContainers().UsePipeTables().UseEmphasisExtras().UseAutoLinks().DisableHtml().Build();
    internal sealed record Submission(string TurnId, Dictionary<string, string> Values)
    {
        public Dictionary<string, string[]>? Attachments { get; set; }
        public Dictionary<string, string>? AmbiguousLegacy { get; set; }
    }
    internal static string FieldKey(string type, string id) => type + ":" + id;
    internal static string Value(IReadOnlyDictionary<string, string> values, string type, string id, bool allowLegacy = true) =>
        values.TryGetValue(FieldKey(type, id), out var value) ? value : allowLegacy ? values.GetValueOrDefault(id, "") : "";
    internal static Dictionary<string, HashSet<string>> FieldTypes(AiTurn turn)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var m in Tokens(turn))
        {
            var (type, id) = Field(m);
            if (!result.TryGetValue(id, out var kinds)) result[id] = kinds = new(StringComparer.Ordinal);
            kinds.Add(type);
        }
        return result;
    }
    internal static bool AllowLegacy(IReadOnlyDictionary<string, HashSet<string>> types, string id) => !types.TryGetValue(id, out var kinds) || kinds.Count <= 1;
    private static (string Type, string Id) Field(Match m) => m.Groups["tid"].Success ? (m.Groups["text"].Value, m.Groups["tid"].Value) :
        m.Groups["rid"].Success ? ("radio", m.Groups["rid"].Value) : m.Groups["cid"].Success ? ("checkbox", m.Groups["cid"].Value) : ("file", m.Groups["fid"].Value);

    internal static Dictionary<string, string> Draft(AiTurn turn)
    {
        if (turn.AdditionalData?.TryGetValue(DraftKey, out var json) != true) return new(StringComparer.Ordinal);
        try { return json.Deserialize<Dictionary<string, string>>() ?? new(StringComparer.Ordinal); }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }
    internal static void SetDraft(AiTurn turn, string type, string id, string value)
    {
        var values = Draft(turn);
        if (Value(values, type, id) != value) AiFormGrading.InvalidateField(turn, type, id);
        values[FieldKey(type, id)] = value;
        var data = turn.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(turn.AdditionalData);
        data[DraftKey] = JsonSerializer.SerializeToElement(values);
        turn.AdditionalData = data;
    }
    internal static Submission? Snapshot(AiTurn? turn)
    {
        if (turn is null || !SupportsForms(turn)) return null;
        var draft = Draft(turn); var values = new Dictionary<string, string>(StringComparer.Ordinal); var found = false; var hasFile = false;
        var tokens = Tokens(turn).ToArray(); var types = FieldTypes(turn);
        foreach (var m in tokens)
        {
            found = true;
            if (m.Groups["fid"].Success) { hasFile = true; continue; }
            var (type, id) = Field(m);
            var value = Value(draft, type, id, AllowLegacy(types, id));
            values.TryAdd(FieldKey(type, id), m.Groups["cid"].Success && value.Length == 0 ? "false" : value);
        }
        var ambiguous = draft.Where(pair => !AllowLegacy(types, pair.Key) && (types[pair.Key].Contains("file") || types[pair.Key].Any(type => !draft.ContainsKey(FieldKey(type, pair.Key)))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return found ? new Submission(turn.Id, values) { Attachments = hasFile ? new(StringComparer.Ordinal) : null, AmbiguousLegacy = ambiguous.Count == 0 ? null : ambiguous } : null;
    }
    internal static Submission? FromTurn(AiTurn turn)
    {
        if (turn.AdditionalData?.ContainsKey(SubmissionsKey) == true) return AllFromTurn(turn).FirstOrDefault();
        if (turn.AdditionalData?.TryGetValue(SubmissionKey, out var json) != true) return null;
        try { return json.Deserialize<Submission>() is { Values: not null, TurnId.Length: > 0 } value ? value : null; } catch (JsonException) { return null; }
    }
    internal static IReadOnlyList<Submission> AllFromTurn(AiTurn turn)
    {
        if (turn.AdditionalData?.TryGetValue(SubmissionsKey, out var json) == true)
        {
            try { return (json.Deserialize<List<Submission?>>() ?? []).Where(value => value is { Values: not null, TurnId.Length: > 0 }).Select(value => value!).ToArray(); } catch (JsonException) { return []; }
        }
        if (turn.AdditionalData?.TryGetValue(SubmissionKey, out json) != true) return [];
        try { return json.Deserialize<Submission>() is { Values: not null, TurnId.Length: > 0 } value ? [value] : []; } catch (JsonException) { return []; }
    }
    internal static AiTurn WithSubmissions(AiTurn turn, IReadOnlyList<Submission> submissions)
    {
        var data = turn.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(turn.AdditionalData);
        data[ProtocolKey] = JsonSerializer.SerializeToElement(1);
        if (submissions.Count > 0) data[SubmissionsKey] = JsonSerializer.SerializeToElement(submissions);
        data.Remove(SubmissionKey);
        return turn with { AdditionalData = data };
    }
    internal static AiTurn WithSubmission(AiTurn turn, Submission? submission)
    {
        var data = turn.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(turn.AdditionalData);
        data[ProtocolKey] = JsonSerializer.SerializeToElement(1);
        if (submission is not null) data[SubmissionKey] = JsonSerializer.SerializeToElement(submission);
        return turn with { AdditionalData = data };
    }
    internal static bool SupportsForms(AiTurn turn) => turn.AdditionalData?.TryGetValue(ProtocolKey, out var version) == true && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var n) && n == 1;
    internal static string Describe(Submission submission) => "用户填写的回答控件（字段键使用 text: 单行、textarea: 多行、radio: 单选、checkbox: 多选加原始字段 id；空字符串表示未填写）：\n" + JsonSerializer.Serialize(submission) +
        (submission.AmbiguousLegacy is { Count: > 0 } ? "\nAmbiguousLegacy 中的旧值因多个不同类型的控件共用同一 ID 而无法确定归属，请先向用户核对，不要推断。" : "");
    internal static bool HasResponse(AiTurn turn, Submission? submission)
    {
        if (submission is null) return false;
        foreach (var m in Tokens(turn))
        {
            if (m.Groups["fid"].Success) continue;
            var (type, id) = Field(m);
            var value = Value(submission.Values, type, id);
            if (m.Groups["cid"].Success ? value == "true" : value.Length > 0) return true;
        }
        return false;
    }
    private static IEnumerable<Match> Tokens(AiTurn turn)
    {
        static IEnumerable<Match> Inlines(ContainerInline inline)
        {
            foreach (var child in inline)
                if (child is LiteralInline literal)
                    foreach (Match match in Token.Matches(literal.Content.ToString())) yield return match;
                else if (child is ContainerInline nested)
                    foreach (var match in Inlines(nested)) yield return match;
        }
        static IEnumerable<Match> Blocks(ContainerBlock block)
        {
            foreach (var child in block)
                if (child is LeafBlock { Inline: { } inline })
                    foreach (var match in Inlines(inline)) yield return match;
                else if (child is ContainerBlock nested)
                    foreach (var match in Blocks(nested)) yield return match;
        }
        var sources = turn.Segments.Count == 0 ? [turn.Answer] : turn.Segments.Where(s => s.Kind != "activity").Select(s => s.Text);
        foreach (var source in sources)
        {
            if (!source.Contains("{{", StringComparison.Ordinal)) continue;
            var prepared = new ChatMath().Prepare(source, Pipeline);
            foreach (var match in Blocks(Markdig.Markdown.Parse(prepared, Pipeline))) yield return match;
        }
    }
    internal static string HistoryQuestion(AiTurn turn)
    {
        var question = turn.Question;
        var submissions = AllFromTurn(turn);
        if (submissions.Count > 0) question += "\n\n本次提交消息标识：" + turn.Id;
        foreach (var submission in submissions) question += "\n\n" + Describe(submission);
        foreach (var entry in turn.SentAnnotations)
        {
            var attachment = AiFormAttachmentOrigins.Description(entry);
            if (attachment.Length > 0 && !question.Contains(attachment, StringComparison.Ordinal)) question += "\n\n" + attachment;
        }
        return question;
    }
    internal static void Lock(AiConversation conversation, Submission? submission)
    {
        if (submission is null) return;
        var source = conversation.Turns.FirstOrDefault(t => t.Id == submission.TurnId);
        if (source is null) return;
        var data = source.AdditionalData is null ? new Dictionary<string, JsonElement>() : new(source.AdditionalData);
        data[LockedKey] = JsonSerializer.SerializeToElement(submission);
        source.AdditionalData = data;
    }
    internal static Submission? SubmittedFor(AiConversation conversation, string turnId)
    {
        if (AiFormGrading.IsReopened(conversation, turnId)) return null;
        var source = conversation.Turns.FirstOrDefault(t => t.Id == turnId);
        if (source?.AdditionalData?.TryGetValue(LockedKey, out var json) == true)
            try { if (json.Deserialize<Submission>() is { } locked) return locked; } catch (JsonException) { }
        return conversation.Turns.SelectMany(AllFromTurn).LastOrDefault(s => s.TurnId == turnId);
    }
}
