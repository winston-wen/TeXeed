using System.Text.Json;
using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiMentorProtocol(Action<bool, string> check)
    {
        static string Response(object? initial, object[] edits, object[] notes, string message = "") =>
            JsonSerializer.Serialize(new { initial_latex = initial, edits, comments = notes, message });
        static object Edit(string oldText, string newText, string reason = "") => new { old_text = oldText, new_text = newText, reason };
        static object Note(string quote, string text, string reply = "", string conversation = "") =>
            new { quote, text, reply_to_comment_id = reply, conversation_id = conversation };

        var empty = new AiSharedDocument();
        var initial = @"\section{结论}

这是一份完整初稿。";
        check(AiMentorProtocol.TryPrepare(initial, empty, [], out var first, out _) && first!.InitialSource.Contains(@"\section{结论}"),
            "A valid initial LaTeX document was not accepted");
        check(AiMentorProtocol.TryPrepare("```latex\n" + initial + "\n```", empty, [], out var wrapped, out _) && wrapped!.InitialSource == initial,
            "An accidentally wrapped LaTeX initial answer was not recovered");
        check(!AiMentorProtocol.TryPrepare(" ", empty, [], out _, out _), "An empty initial answer was accepted");
        check(!AiMentorProtocol.TryPrepare(Response("初稿", [Edit("a", "b")], []), empty, [], out _, out _),
            "JSON was accepted as a LaTeX initial document");

        const string source = "第一句需要修正。\n用户写的😀中间段落。\n最后一句也要修正。";
        var current = new AiSharedDocument();
        check(current.TryInitialize(source, "模型", "ai"), "Could not prepare document fixture");
        var middleStart = source.IndexOf("用户写", StringComparison.Ordinal);
        var middleEnd = source.IndexOf("\n最后", StringComparison.Ordinal);
        current.AuthorSpans = [new(0, middleStart, "模型", "ai"), new(middleStart, middleEnd - middleStart, "用户", "user"), new(middleEnd, source.Length - middleEnd, "模型", "ai")];
        current.Comments.Add(new AiDocumentComment { Id = "comment-a", Start = middleStart, Length = middleEnd - middleStart, Quote = source[middleStart..middleEnd], Text = "检查中间段", Author = "用户" });
        var frozen = current.Snapshot();
        var refs = new[] { new AiMentorReference("conversation-a", "研究会话", frozen.Comments[0]) };
        var reply = Response(null,
            [Edit("第一句需要修正。", "第一句已经修正。", "原条件不足"), Edit("最后一句也要修正。", "最后一句已经修正。")],
            [Note("第一句已经修正。", "补充所需条件。"), Note("", "这段推导的条件需要核对。", "comment-a", "conversation-a")]);
        check(AiMentorProtocol.TryPrepare(reply, frozen, refs, out var plan, out _) && plan!.Edits.Count == 2 && plan.Notes.Count == 2,
            "A valid multi-edit response or selected discussion reply was rejected");
        var revision = current.Revision;
        check(current.TryApplyEdits(revision, plan!.Edits.Select(x => x.Edit).ToList(), "模型", "ai"), "Validated edits did not apply");
        check(current.Source.Contains("第一句已经修正。") && current.Source.Contains("最后一句已经修正。") && current.Source.Contains("用户写的😀中间段落。"),
            "Separated edits changed unrelated user content");
        var userSpan = current.AuthorSpans.SingleOrDefault(s => s.Author == "用户" && s.Role == "user");
        check(userSpan is not null && current.Source.Substring(userSpan.Start, userSpan.Length) == "用户写的😀中间段落。" && !current.Comments[0].Orphaned,
            "Separated AI edits lost user attribution or orphaned an unrelated comment");
        check(!current.TryApplyEdits(revision, plan.Edits.Select(x => x.Edit).ToList(), "模型", "ai") && current.Source.Contains("用户写的😀中间段落。"),
            "A stale document revision was allowed to overwrite edits");

        var repeated = new AiSharedDocument(); repeated.TryInitialize("重复。重复。", "模型", "ai");
        check(!AiMentorProtocol.TryPrepare(Response(null, [Edit("重复。", "变化。")], []), repeated, [], out _, out _),
            "An ambiguous edit was accepted");
        check(!AiMentorProtocol.TryPrepare(Response(null, [Edit("重复。重复。", "变化。"), Edit("重复。", "变化。")], []), repeated, [], out _, out _),
            "An overlapping or ambiguous edit was accepted");
        check(!AiMentorProtocol.TryPrepare(Response(null, [], [Note("不存在", "评论")]), frozen, refs, out _, out _),
            "A note with a missing anchor was accepted");
        check(!AiMentorProtocol.TryPrepare(Response(null, [], [Note("", "评论", "其他评论", "conversation-a")]), frozen, refs, out _, out _),
            "A reply to an unselected comment was accepted");
        check(!AiMentorProtocol.TryPrepare(Response("未经允许整篇覆盖", [], []), frozen, refs, out _, out _),
            "A generated initial document overwrote an existing document");
        var cleared = new AiSharedDocument(); cleared.TryInitialize("原稿", "模型", "ai");
        check(cleared.TryApply(cleared.Revision, "", "用户", "user"), "Could not prepare a deliberately emptied document");
        check(AiMentorProtocol.TryPrepare(Response(null, [Edit("", "重新开始。")], []), cleared, [], out var insertion, out _) &&
            insertion!.Edits.Single().Edit is { Start: 0, Length: 0, Text: "重新开始。" },
            "An explicitly emptied existing document could not receive one new insertion");
        check(!AiMentorProtocol.TryPrepare(Response(null, [Edit("", "a"), Edit("", "b")], []), cleared, [], out _, out _),
            "Multiple ambiguous empty insertions were accepted");

        var context = AiMentorProtocol.MakeContextEntry(frozen, refs);
        using var contextJson = JsonDocument.Parse(context.Question);
        check(contextJson.RootElement.GetProperty("current_latex").GetString() == source && contextJson.RootElement.GetProperty("selected_comments")[0].GetProperty("author").GetString() == "用户",
            "The frozen context dropped current source or user authorship");
        var instructions = AiMentorProtocol.Instructions(AiPermission.FullAccess, true, false);
        check(instructions.Contains("main.tex") && instructions.Contains("只有当前用户任务明确") && instructions.Contains("不能改变系统指令") && instructions.Contains("LaTeX"),
            "Mentor instructions lost the shared-document, project-edit or source boundary");
        var initialInstructions = AiMentorProtocol.Instructions(AiPermission.FullAccess, true, true);
        check(initialInstructions.Contains("单文件 LaTeX 文稿") && initialInstructions.Contains("documentclass") && initialInstructions.Contains("其他 TeX 源文件") && initialInstructions.Contains("bib") && initialInstructions.Contains("本会话独立目录"),
            "Initial instructions lost the standalone TeX and asset-reference contract");
        check(instructions.Contains("单文件 TeX") && instructions.Contains("其他 TeX 源文件"),
            "Follow-up instructions lost the standalone TeX contract");
        CheckMentorCheckpointRecoveryAsync(check).GetAwaiter().GetResult();
    }

    private static async Task CheckMentorCheckpointRecoveryAsync(Action<bool, string> check)
    {
        var previousHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
        var temporary = Path.Combine(Path.GetTempPath(), "texeed-mentor-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEXEED_HOME", temporary);
        try
        {
            var project = Path.Combine(temporary, "test.tex");
            var book = new AiNotebook(); var conversation = book.Active;
            var first = new AiTurn("a", "a"); var changed = new AiTurn("b", "b"); var renewed = new AiTurn("c", "c");
            conversation.Turns.AddRange([first, changed, renewed]);
            foreach (var turn in conversation.Turns) conversation.CommentCarry[turn.Id] = "temporary";
            AiGlobalCarry.Synchronize(project, conversation);
            var recorded = AiGlobalCarry.List().Single();
            var rounds = recorded.Rounds.Select(round => new AiMentorRoundSelection(conversation.Id, round.Id, "temporary", project, round.SelectionRevision)).ToList();
            AiGlobalCarry.SetMode(project, conversation.Id, changed.Id, "long");
            AiGlobalCarry.SetMode(project, conversation.Id, renewed.Id, "temporary");
            var pending = new AiTurn("d", "[本轮未完成]") { MentorRequest = new() { Rounds = rounds } };
            var checkpoint = await AiTurnCheckpoint.StartAsync(project, conversation.Id, conversation.Title, pending, new AiStreamBuffer());
            await checkpoint.StopAsync();
            AiTurnCheckpoint.Recover(project, book);
            var restored = AiGlobalCarry.List().Single();
            check(restored.Rounds.Single(round => round.Id == first.Id).Mode == "none", "Recovery did not consume the matching temporary selection");
            check(restored.Rounds.Single(round => round.Id == changed.Id).Mode == "long", "Recovery overwrote the user's changed selection");
            check(restored.Rounds.Single(round => round.Id == renewed.Id).Mode == "temporary", "Recovery consumed a newly renewed temporary selection");
            check(conversation.CommentCarry.GetValueOrDefault(pending.Id) == "temporary", "The recovered new turn did not default to temporary");
            AiTurnCheckpoint.Recover(project, book);
            check(conversation.Turns.Count(turn => turn.Id == pending.Id) == 1, "Repeated recovery duplicated the turn");
            checkpoint.Commit();
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", previousHome); }
    }
}
