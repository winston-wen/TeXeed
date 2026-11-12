using System.Text.Json;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiFormGrading()
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static AiTurn Source(string name) => AiForms.WithSubmission(new AiTurn(name, "# 练习\n\n{{text:a}} {{checkbox:a:选项}}"), null);
        static string Grade(string source, string submission, string result = "correct") =>
            "解答已核对。\n\n```texeed-grade\n" + JsonSerializer.Serialize(new { forms = new[] { new { turn = source, submission, fields = new Dictionary<string, object> { ["text:a"] = new { result, comment = "请核对 `x` 的计算" }, ["checkbox:a"] = new { result = "uncertain", comment = "条件不足" } } } } }) + "\n```";
        var source = Source("表单一"); AiForms.SetDraft(source, "text", "a", "83"); AiForms.SetDraft(source, "checkbox", "a", "true");
        var conversation = new AiConversation(); conversation.Turns.Add(source);
        var snapshot = AiForms.Snapshot(source) ?? throw new Exception("缺少表单快照");
        var submitted = AiForms.WithSubmissions(new AiTurn("检查答案", "") { SentAt = DateTimeOffset.Now }, [snapshot]);
        var reply = submitted with { Answer = Grade(source.Id, submitted.Id), AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        conversation.Turns.Add(reply); AiForms.Lock(conversation, snapshot);
        Require(AiForms.SubmittedFor(conversation, source.Id) is not null, "发送后应锁定");
        Require(AiFormGrading.Apply(conversation, reply), "有效判题应被接受");
        Require(AiFormGrading.IsApplied(reply) && !AiFormGrading.DisplayText(reply.Answer).Contains("texeed-grade"), "只隐藏已验证协议");
        Require(AiForms.SubmittedFor(conversation, source.Id) is null, "判题后应重开");
        Require(AiFormGrading.GradeFor(conversation, source, "text", "a")?.Result == "correct", "原字段缺少判定");
        Require(AiFormGrading.GradeFor(conversation, source, "checkbox", "a")?.Result == "uncertain", "同名不同类型字段混淆");
        AiForms.SetDraft(source, "text", "a", "84");
        Require(AiFormGrading.GradeFor(conversation, source, "text", "a") is null && AiFormGrading.GradeFor(conversation, source, "checkbox", "a") is not null, "编辑后只清除对应字段的当前标记");
        AiForms.SetDraft(source, "text", "a", "83");
        Require(AiFormGrading.GradeFor(conversation, source, "text", "a") is null, "改回原值不应恢复旧标记");
        var renewed = AiForms.Snapshot(source)!;
        var next = AiForms.WithSubmissions(new AiTurn("再检查", "") { SentAt = DateTimeOffset.Now }, [renewed]);
        conversation.Turns.Add(next); AiForms.Lock(conversation, renewed);
        Require(AiForms.SubmittedFor(conversation, source.Id) is not null, "再次提交应锁定");
        var stale = new AiTurn("后续问题", Grade(source.Id, submitted.Id)) { AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        conversation.Turns.Add(stale);
        Require(!AiFormGrading.Apply(conversation, stale), "旧快照不得覆盖较新提交");
        var later = new AiTurn("批改刚才那张表", Grade(source.Id, next.Id, "incorrect")) { AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        conversation.Turns.Add(later);
        Require(AiFormGrading.Apply(conversation, later), "后续消息应能批改最近提交");
        Require(AiFormGrading.GradeFor(conversation, source, "text", "a")?.Result == "incorrect", "再次判题缺失");
        AiForms.SetDraft(source, "text", "a", "用户未发送的新修改");
        var repeated = new AiTurn("再解释", Grade(source.Id, next.Id)) { AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        conversation.Turns.Add(repeated);
        Require(AiFormGrading.Apply(conversation, repeated), "可再次评估相同已提交快照");
        Require(AiForms.Value(AiForms.Draft(source), "text", "a") == "用户未发送的新修改", "判题不得覆盖已编辑草稿");
        Require(AiFormGrading.GradeFor(conversation, source, "text", "a") is null, "旧判题不得标到新草稿");
        var serialized = JsonSerializer.Serialize(conversation); var restored = JsonSerializer.Deserialize<AiConversation>(serialized)!;
        var restoredSource = restored.Turns.Single(t => t.Id == source.Id);
        Require(AiFormGrading.IsReopened(restored, source.Id) && AiForms.Value(AiForms.Draft(restoredSource), "text", "a") == "用户未发送的新修改", "重开丢失草稿");
        var pending = AiForms.WithSubmissions(new AiTurn("再次发送", "[本轮未完成]") { SentAt = DateTimeOffset.Now, Incomplete = true }, [AiForms.Snapshot(restoredSource)!]);
        restored.Turns.Add(pending);
        Require(!AiFormGrading.IsReopened(restored, source.Id), "恢复已落盘的待完成轮次后应锁定");
        restored.Turns.Remove(pending); restored.Turns.RemoveAll(t => t.Id == repeated.Id);
        Require(AiFormGrading.IsReopened(restored, source.Id) && AiForms.Value(AiForms.Draft(restoredSource), "text", "a") == "用户未发送的新修改", "删除最新判题后恢复此前有效状态并保留草稿");
        restored.Turns.RemoveAll(t => t.Id == later.Id || t.Id == reply.Id);
        Require(!AiFormGrading.IsReopened(restored, source.Id) && AiForms.Value(AiForms.Draft(restoredSource), "text", "a") == "用户未发送的新修改", "删除所有判题回复后应锁定并保留草稿");
        var badSource = Source("旧表单"); var badBook = new AiConversation(); badBook.Turns.Add(badSource);
        var badSnapshot = AiForms.Snapshot(badSource)!; var badSent = AiForms.WithSubmissions(new AiTurn("提交", "") { SentAt = DateTimeOffset.Now }, [badSnapshot]);
        var badReply = badSent with { Answer = Grade(badSource.Id, "不存在"), AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        badBook.Turns.Add(badReply); AiForms.Lock(badBook, badSnapshot);
        Require(!AiFormGrading.Apply(badBook, badReply) && !AiFormGrading.IsApplied(badReply) && AiForms.SubmittedFor(badBook, badSource.Id) is not null, "无效来源不得判题或解锁");
        var partial = badReply with { Answer = Grade(badSource.Id, badReply.Id), AnsweredAt = null, Incomplete = true };
        Require(!AiFormGrading.Apply(badBook, partial), "中断的回答不得解锁");
        var denied = badSent with { Answer = Grade(badSource.Id, badSent.Id), AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        badBook.Turns[^1] = denied;
        Require(!AiFormGrading.Apply(badBook, denied, new HashSet<string>(), out _) && AiForms.SubmittedFor(badBook, badSource.Id) is not null, "AI 未获得完整源题时不得显示伪判");
        var a = Source("表单甲"); var b = Source("表单乙");
        AiForms.SetDraft(a, "text", "a", "甲"); AiForms.SetDraft(b, "text", "a", "乙");
        var multi = new AiConversation(); multi.Turns.Add(a); multi.Turns.Add(b);
        var snapshots = new[] { AiForms.Snapshot(a)!, AiForms.Snapshot(b)! };
        var sent = AiForms.WithSubmissions(new AiTurn("批改两份", "") { SentAt = DateTimeOffset.Now }, snapshots);
        var json = JsonSerializer.Serialize(new { forms = new[] { a, b }.Select(s => new { turn = s.Id, submission = sent.Id, fields = new Dictionary<string, object> { ["text:a"] = new { result = "correct", comment = "已核对" } } }) });
        var graded = sent with { Answer = "结果\n\n```texeed-grade\n" + json + "\n```", AnsweredAt = DateTimeOffset.Now, Incomplete = false };
        multi.Turns.Add(graded); foreach (var s in snapshots) AiForms.Lock(multi, s);
        Require(AiFormGrading.Apply(multi, graded) && AiFormGrading.IsReopened(multi, a.Id) && AiFormGrading.IsReopened(multi, b.Id), "多份表单应分别判定并重开");
        Require(AiForms.AllFromTurn(graded).Count == 2, "多份提交未保留");
        var manifest = AiFormGrading.ContextManifest(multi.Turns, 24000);
        Require(manifest.Contains(a.Id) && manifest.Contains(b.Id) && manifest.Contains(sent.Id) && manifest.Contains("甲") && manifest.Contains("乙"), "压缩后判题资料缺少来源、提交或答案");
        var scoped = AiFormGrading.ContextManifest(multi.Turns, 24000, new HashSet<string> { b.Id });
        Require(!scoped.Contains(a.Id) && scoped.Contains(b.Id), "分支不能把父会话表单当成本会话可判题来源");
        var first = Source("首次提交且题目已压缩"); var firstSnapshot = AiForms.Snapshot(first)!;
        var firstManifest = AiFormGrading.ContextManifest([first], 24000, new HashSet<string> { first.Id }, [firstSnapshot], "pending-id");
        Require(firstManifest.Contains(first.Id) && firstManifest.Contains("pending-id") && firstManifest.Contains("首次提交且题目已压缩"), "首次提交也应携带源题及提交标识");
        var huge = AiForms.WithSubmission(new AiTurn("超长源题", new string('长', 30000) + "\n\n{{text:a}}"), null);
        var hugeManifest = AiFormGrading.ContextManifest([huge], 2048, new HashSet<string> { huge.Id }, [AiForms.Snapshot(huge)!], "large-pending");
        Require(hugeManifest.Contains("不要判对错"), "源题超预算时不得默默提供不完整信息");
        var many = Enumerable.Range(0, 5).Select(i => Source("第 " + i + " 张")).ToArray();
        var manySnapshots = many.Select(s => AiForms.Snapshot(s)!).ToArray();
        var manyContext = AiFormGrading.BuildContext(many, 24000, many.Select(s => s.Id).ToHashSet(), manySnapshots, "batch");
        Require(manyContext.CompleteSources.Count == 5 && many.All(s => manyContext.Text.Contains(s.Id)), "当前多份表单不得静默截掉后三份");
        var bounded = AiFormGrading.BuildContext([huge], 2048, new HashSet<string> { huge.Id }, [AiForms.Snapshot(huge)!], "large-pending");
        Require(!bounded.CompleteSources.Contains(huge.Id), "不完整源题不得进入可判题白名单");
        multi.Turns.Remove(graded);
        Require(!AiFormGrading.IsReopened(multi, a.Id) && AiFormGrading.GradeFor(multi, a, "text", "a") is null, "删除来源提交后不得保留孤立判定");
        Console.WriteLine("PASS form grading: validated snapshots, delayed grading, re-edit, crash, deletion and history.");
    }
}
