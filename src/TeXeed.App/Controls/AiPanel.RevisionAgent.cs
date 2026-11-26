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
        var requestedApproval = conversation.Permission == AiPermission.AskBeforeShell;
        var reviewed = new HashSet<string>();
        var candidates = new Dictionary<string, AiRelatedRevision>();
        var denied = false;
        void ScanVersion(AiTurn current, AiTurnRevision prior, CancellationToken token)
        {
            foreach (var related in AiRevisionImpact.Scan(conversation.Turns, AiRevisionImpact.AtVersion(current, prior), current, token))
                if (related.Target != request.TargetTurnId) candidates[related.Target] = related;
        }
        var restored = false;
        return (name, arguments, token) => Dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(conversation, _book.Active))
                throw new InvalidOperationException("改稿会话已经切换。");
            if (conversation.Permission == AiPermission.ReadOnly) throw new UnauthorizedAccessException("当前会话为只读。");
            if (denied) return "用户已拒绝本次改稿，未执行后续写入。";
            if (name == "await_revision_reply") { setWaiting(true); return "已保留改稿任务，请在本轮答复中给出澄清问题。"; }
            using var json = JsonDocument.Parse(arguments);
            var args = json.RootElement;
            if (!restored)
            {
                foreach (var saved in conversation.Turns.Where(t => t.RevisionHistory?.Count > 1))
                {
                    var history = saved.RevisionHistory!;
                    var i = history.FindIndex(r => r.OperationId?.StartsWith(request.TaskId + "/", StringComparison.Ordinal) == true);
                    if (i > 0) ScanVersion(saved, history[i - 1], token);
                }
                restored = true;
            }
            var id = name == "read_answer_revision" ? request.TargetTurnId : args.GetProperty("target").GetString();
            var operationId = name == "apply_answer_revision" ? request.TaskId + "/" + AiStorage.Hash(arguments) : null;
            var target = conversation.Turns.FirstOrDefault(t => t.Id == id && !t.IsIncomplete);
            string SavedResult() => "已保存。已扫描本会话其他已完成轮次。\n" +
                (candidates.Count == 0 ? "未发现需要复核的引用或相关段落。" :
                "请逐项复核以下相关轮次，仅在需要时连带修改。\n" + JsonSerializer.Serialize(candidates.Values.OrderBy(c => c.Turn), RevisionToolJson));
            if (operationId is not null && target?.RevisionHistory?.Any(r => r.OperationId == operationId) == true) return SavedResult();
            if (id is null || id != request.TargetTurnId && (!candidates.ContainsKey(id) ||
                name == "apply_answer_revision" && !reviewed.Contains(id)))
                throw new InvalidOperationException("提交目标与用户选定的轮次不符；连带改稿须先读取扫描列出的相关轮次。");
            if (target is null) throw new InvalidOperationException("找不到授权改稿的轮次。");
            var draft = BeginRevision(target);
            if (name is "read_answer_revision" or "read_related_answer_revision")
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
                if (start < draft.Rows.Count) reviewed.Add(target.Id);
                return JsonSerializer.Serialize(new { target = target.Id, turn = conversation.Turns.IndexOf(target) + 1, version = draft.Version, total_rows = draft.Rows.Count,
                    potentially_affected = candidates.Values.OrderBy(c => c.Turn),
                    next_row = start + count < draft.Rows.Count ? (int?)(start + count) : null,
                    task_history = taskHistory, rows = draft.Rows.Skip(start).Take(count).Select(DescribeRevisionRow) }, RevisionToolJson);
            }
            var version = args.GetProperty("version").GetString();
            var changes = args.GetProperty("changes");
            if (version != draft.Version) throw new InvalidOperationException("稿件版本已变化，请读取最新版后核对。");
            if (target.UserRevisionDraft is not null) throw new InvalidOperationException("该轮有未完成的手动整理，请先保存或取消手动整理。");
            var rows = ApplyRevisionChanges(draft, changes);
            var needsApproval = requestedApproval || conversation.Permission == AiPermission.AskBeforeShell;
            if (needsApproval)
            {
                var owner = System.Windows.Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;
                var number = conversation.Turns.IndexOf(target) + 1;
                var question = id == request.TargetTurnId ? $"AI 请求把整理稿保存到第 {number} 轮。" : $"AI 请求连带修改第 {number} 轮。{string.Join("，", candidates[id].Reasons)}。";
                var choice = await AiShellApproval.ShowAsync(owner, "允许修改会话正文？", question,
                    [("拒绝", "deny"), ("同意修改", "once")], token, AiRevisionText.Source(rows), "查看拟保存的正文");
                if (choice.Choice == "feedback") return "尚未保存。请根据用户的意见调整后重新提交：" + choice.Feedback;
                if (choice.Choice != "once") { denied = true; setWaiting(false); return "用户已拒绝本次改稿，原文已保留。"; }
                if (_disposed || conversation != _book.Active || conversation.Permission == AiPermission.ReadOnly)
                    throw new InvalidOperationException("会话或改稿权限已经变化，请重新发起改稿。");
            }
            await CommitRevisionAsync(target.Id, draft.Version, rows, "ai", operationId, token, approved: needsApproval);
            setWaiting(false);
            var updated = conversation.Turns.First(t => t.Id == id);
            if (ReferenceEquals(target, updated)) return "本轮正文已经符合此次提交，无需修改。";
            foreach (var related in AiRevisionImpact.Scan(conversation.Turns, target, updated, token))
                if (related.Target != request.TargetTurnId) candidates[related.Target] = related;
            candidates.Remove(id);
            return SavedResult();
        }).Task.Unwrap();
    }

    private static object DescribeRevisionRow(AiRevisionRow row)
    {
        if (row.Locked && !AiRevisionText.IsDisplayFormula(row.Text)) return new { id = row.Id, level = row.Level, locked = true, source = row.Text };
        var buffer = RevisionBuffer.From(row, RevisionMathScope.All);
        var parts = new List<object>();
        var start = 0;
        for (var i = 0; i < buffer.Atoms.Count; i++)
        {
            var atom = buffer.Atoms[i];
            if (atom.At > start) parts.Add(new { kind = "text", value = buffer.Display[start..atom.At] });
            parts.Add(new { kind = "keep", value = i.ToString(CultureInfo.InvariantCulture), source = atom.Source, references = atom.Links.Select(l => l.Label) });
            start = atom.End;
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
            if (row.Locked && !AiRevisionText.IsDisplayFormula(row.Text)) throw new InvalidOperationException("该段包含受保护的对象，无法改写。");
            var buffer = RevisionBuffer.From(row, RevisionMathScope.All);
            if (paragraphs.GetArrayLength() == 0 && buffer.Atoms.All(a => a.Source.Length == 0))
            {
                Append(row with { Text = "", Level = 0, Locked = false, Links = row.Links.Select(l => l with { Offset = 0 }).ToList() });
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
                        text.Append(value);
                        continue;
                    }
                    if (kind != "keep" || expected >= buffer.Atoms.Count || value != expected.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("保护对象须按原顺序逐一保留。");
                    atoms.Add(buffer.Atoms[expected++] with { At = text.Length }); text.Append(RevisionBuffer.Marker);
                }
                var updated = first ? row with { Level = level } : new AiRevisionRow(Guid.NewGuid().ToString("N"), row.SegmentId, "", level, false, []);
                updated = new RevisionBuffer(text.ToString(), atoms).Apply(updated);
                Append(updated with { Locked = AiRevisionText.IsDisplayFormula(updated.Text) });
                first = false;
            }
            if (expected != buffer.Atoms.Count) throw new InvalidOperationException("改稿缺少原段落中受保护的对象或会话引用。");
        }
        foreach (var row in draft.Rows) { ApplyAt(row, "before"); ApplyAt(row, "replace"); ApplyAt(row, "after"); }
        if (edits.Count != 0) throw new InvalidOperationException("指定的段落不属于当前稿件。");
        return rows;
    }
}
