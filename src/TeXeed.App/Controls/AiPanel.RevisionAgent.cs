using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private static readonly JsonSerializerOptions RevisionToolJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private void RequestAiRevision(AiTurn turn)
    {
        var index = _book.Turns.FindIndex(t => t.Id == turn.Id);
        if (index < 0 || turn.IsIncomplete) return;
        var prefix = $"请改写第 {index + 1} 轮：";
        if (!Composer.Text.StartsWith(prefix, StringComparison.Ordinal)) Composer.Text = prefix + Regex.Replace(Composer.Text, @"^请改写第 [0-9]+ 轮：", "");
        Composer.Focus(); Composer.CaretIndex = Composer.Text.Length;
    }

    private AiRevisionRunner CreateRevisionRunner(AiRevisionRequest request, Action<bool> setWaiting)
    {
        var conversation = _book.Active;
        return (name, arguments, token) => Dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(conversation, _book.Active))
                throw new InvalidOperationException("改稿会话已经切换。");
            var target = conversation.Turns.FirstOrDefault(t => t.Id == request.TargetTurnId && !t.IsIncomplete)
                ?? throw new InvalidOperationException("找不到授权改稿的轮次。");
            if (name == "await_revision_reply") { setWaiting(true); return "已保留改稿任务，请在本轮答复中给出澄清问题。"; }
            using var json = JsonDocument.Parse(arguments);
            var args = json.RootElement;
            var draft = BeginRevision(target);
            if (name == "read_answer_revision")
            {
                var start = args.GetProperty("start_row").GetInt32();
                var count = args.GetProperty("max_rows").GetInt32();
                if (start < 0 || start > draft.Rows.Count || count is < 1 or > 40)
                    throw new InvalidOperationException("start_row 从 0 开始，max_rows 介于 1 与 40 之间。");
                var prior = start == 0 ? conversation.Turns.Where(t => t.RevisionRequest?.TaskId == request.TaskId).ToArray() : [];
                var taskTurns = prior.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                var taskHistory = prior.Select(t => new { instruction = t.Prompt,
                    clarification = t.RevisionRequest!.AwaitingReply ? t.Answer : null,
                    replies = AiForms.AllFromTurn(t).Where(form => taskTurns.Contains(form.TurnId)).Select(form => form.Values) });
                return JsonSerializer.Serialize(new { target = target.Id, version = draft.Version, total_rows = draft.Rows.Count,
                    next_row = start + count < draft.Rows.Count ? (int?)(start + count) : null,
                    task_history = taskHistory, rows = draft.Rows.Skip(start).Take(count).Select(DescribeRevisionRow) }, RevisionToolJson);
            }
            if (args.GetProperty("target").GetString() != target.Id) throw new InvalidOperationException("提交目标与用户选定的轮次不符。");
            var version = args.GetProperty("version").GetString();
            var changes = args.GetProperty("changes");
            var operationId = request.TaskId + "/" + AiStorage.Hash(arguments);
            if (target.RevisionHistory?.Any(r => r.OperationId == operationId) == true) return "这次改稿已保存。";
            if (version != draft.Version) throw new InvalidOperationException("稿件版本已变化，请读取最新版后核对。");
            var rows = ApplyRevisionChanges(draft, changes);
            await CommitRevisionAsync(target.Id, draft.Version, rows, "ai", operationId);
            setWaiting(false);
            return "已保存到指定轮次。";
        }).Task.Unwrap();
    }

    private static object DescribeRevisionRow(AiRevisionRow row)
    {
        if (row.Locked) return new { id = row.Id, level = row.Level, locked = true, source = row.Text };
        var buffer = RevisionBuffer.From(row);
        var parts = new List<object>();
        var start = 0;
        for (var i = 0; i < buffer.Atoms.Count; i++)
        {
            var atom = buffer.Atoms[i];
            if (atom.At > start) parts.Add(new { kind = "text", value = buffer.Display[start..atom.At] });
            parts.Add(new { kind = "keep", value = i.ToString(CultureInfo.InvariantCulture), source = atom.Source, references = atom.Links.Select(l => l.Label) });
            start = atom.At + 1;
        }
        if (buffer.Display.Length > start) parts.Add(new { kind = "text", value = buffer.Display[start..] });
        return new { id = row.Id, level = row.Level, locked = false, parts };
    }

    internal static List<AiRevisionRow> ApplyRevisionChanges(AiRevisionDraft draft, JsonElement changes)
    {
        var edits = new Dictionary<(string Row, string Position), JsonElement>();
        foreach (var item in changes.EnumerateArray())
        {
            var row = item.GetProperty("row").GetString() ?? "";
            var position = item.GetProperty("position").GetString() ?? "";
            if (position is not ("replace" or "before" or "after") || !edits.TryAdd((row, position), item.GetProperty("paragraphs")))
                throw new InvalidOperationException("每段的同一位置只能列一次修改。");
        }
        var rows = new List<AiRevisionRow>();
        void Append(AiRevisionRow row)
        {
            if (!row.Locked && string.IsNullOrWhiteSpace(row.Text))
            {
                if (row.Links.Count == 0) return;
                if (rows.Count > 0)
                {
                    var previous = rows[^1];
                    rows[^1] = previous with { Links = [.. previous.Links, .. row.Links.Select(l => l with { Offset = previous.Text.Length })] };
                    return;
                }
                row = row with { Text = "", Level = 0, Links = row.Links.Select(l => l with { Offset = 0 }).ToList() };
            }
            rows.Add(row);
        }
        void ApplyAt(AiRevisionRow original, string position)
        {
            var replace = position == "replace";
            if (!edits.Remove((original.Id, position), out var paragraphs)) { if (replace) rows.Add(original); return; }
            var row = replace ? original : new AiRevisionRow(Guid.NewGuid().ToString("N"), original.SegmentId, "", 0, false, []);
            if (row.Locked) throw new InvalidOperationException("该段包含受保护的对象，无法改写。");
            var buffer = RevisionBuffer.From(row);
            if (paragraphs.GetArrayLength() == 0 && buffer.Atoms.All(a => a.Source.Length == 0))
            {
                Append(row with { Text = "", Links = row.Links.Select(l => l with { Offset = 0 }).ToList() });
                return;
            }
            var expected = 0; var first = true;
            foreach (var paragraph in paragraphs.EnumerateArray())
            {
                var level = paragraph.GetProperty("level").GetInt32();
                if (level is < 0 or > 6) throw new InvalidOperationException("段落层级须为 0 至 6。");
                var text = new StringBuilder(); var atoms = new List<RevisionAtom>();
                foreach (var part in paragraph.GetProperty("parts").EnumerateArray())
                {
                    var kind = part.GetProperty("kind").GetString();
                    var value = part.GetProperty("value").GetString() ?? throw new InvalidOperationException("请提供文字或对象编号。");
                    if (kind == "text")
                    {
                        if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("每个段落独立提交，请将换行拆成多个段落。");
                        text.Append(value);
                        continue;
                    }
                    if (kind != "keep" || expected >= buffer.Atoms.Count || value != expected.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("保护对象须按原顺序逐一保留。");
                    atoms.Add(buffer.Atoms[expected++] with { At = text.Length }); text.Append(RevisionBuffer.Marker);
                }
                var updated = first ? row with { Level = level } : new AiRevisionRow(Guid.NewGuid().ToString("N"), row.SegmentId, "", level, false, []);
                Append(new RevisionBuffer(text.ToString(), atoms).Apply(updated)); first = false;
            }
            if (expected != buffer.Atoms.Count) throw new InvalidOperationException("改稿缺少原段落中的公式或引用。");
        }
        foreach (var row in draft.Rows) { ApplyAt(row, "before"); ApplyAt(row, "replace"); ApplyAt(row, "after"); }
        if (edits.Count != 0) throw new InvalidOperationException("指定的段落不属于当前稿件。");
        return rows;
    }
}
