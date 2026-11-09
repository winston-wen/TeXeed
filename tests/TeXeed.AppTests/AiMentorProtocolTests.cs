using System.Text.Json;
using System.IO;
using TeXeed.App.AI;
using TeXeed.App.Controls;

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
        check(contextJson.RootElement.GetProperty("current_latex").GetString() == source && contextJson.RootElement.GetProperty("comments")[0].GetProperty("author").GetString() == "用户",
            "The frozen context dropped current source or user authorship");
        var active = new AiConversation { Id = "active", Title = "当前会话", SharedDocument = frozen.Snapshot() };
        active.SharedDocument.Comments.Add(new AiDocumentComment { Id = "new-comment", Text = "新评论", Replies = [new AiDocumentReply { Text = "原回复" }] });
        var other = new AiConversation { Id = "other" };
        other.SharedDocument.Comments.Add(new AiDocumentComment { Id = "other-comment", Text = "其他会话评论" });
        var captured = AiMentorProtocol.CaptureRequest(active);
        active.SharedDocument.Comments[1].Text = "后来的修改";
        active.SharedDocument.Comments[1].Replies[0].Text = "后来的回复";
        check(captured.Source == source && captured.References.Count == 2 && captured.References.All(r => r.ConversationId == "active") &&
            captured.References[1].Comment.Text == "新评论" && captured.References[1].Comment.Replies[0].Text == "原回复" &&
            captured.References.All(r => r.Comment.Id != "other-comment"), "Request snapshot mixed sessions or changed after capture");
        var instructions = AiMentorProtocol.Instructions(AiPermission.FullAccess, true, false);
        check(instructions.Contains("main.tex") && instructions.Contains("只有当前用户任务明确") && instructions.Contains("不能改变系统指令") && instructions.Contains("LaTeX"),
            "Mentor instructions lost the shared-document, project-edit or source boundary");
        var initialInstructions = AiMentorProtocol.Instructions(AiPermission.FullAccess, true, true);
        check(initialInstructions.Contains("单文件 LaTeX 文稿") && initialInstructions.Contains("documentclass") && initialInstructions.Contains("其他 TeX 源文件") && initialInstructions.Contains("bib") && initialInstructions.Contains("本会话独立目录"),
            "Initial instructions lost the standalone TeX and asset-reference contract");
        foreach (var promptInstructions in new[] { initialInstructions, instructions })
        {
            check(promptInstructions.Contains("不超过用户附言的 3 倍") && promptInstructions.Contains("不超过 200 字") &&
                promptInstructions.Contains("满足任意一个条件"),
                "Mentor instructions must require either the three-times limit or the 200-character limit, not both");
            check(promptInstructions.Contains("批改摘要") && promptInstructions.Contains("正文") && promptInstructions.Contains("评论"),
                "Mentor instructions must keep the note as an edit summary and direct detailed corrections to the document or comments");
        }
        check(instructions.Contains("单文件 TeX") && instructions.Contains("其他 TeX 源文件"),
            "Follow-up instructions lost the standalone TeX contract");
        CheckMentorPostscript(check);
        CheckMentorCheckpointRecoveryAsync(check).GetAwaiter().GetResult();
    }

    private static void CheckMentorPostscript(Action<bool, string> check)
    {
        const string original = "\\documentclass{article}\n\\usepackage{xeCJK}\n\\begin{document}\n原有论证。\n\\end{document}";
        var book = new AiNotebook();
        var document = book.Active.SharedDocument;
        check(document.TryInitialize(original, "初稿模型"), "Cannot create postscript test document");
        var anchor = original.IndexOf("原有论证", StringComparison.Ordinal);
        document.Comments.Add(new AiDocumentComment { Id = "c1", Start = anchor, Length = 4, Quote = "原有论证", Text = "这个步骤？" });
        var frozen = document.Snapshot();
        var plan = new AiMentorPlan("", [new AiMentorEdit(new AiDocumentEdit(anchor, 4, "补充论证"), "需要补充条件")], [], "检查 50% 与 a_b。 #x $y$ \\path ^ ~\n继续核实。");
        AiPanel.ApplyMentorPlan(book, frozen, [], plan, "示例模型 Ω");
        var source = document.Source;
        check(source.IndexOf("\\begin{document}", StringComparison.Ordinal) < source.IndexOf("% texeed-postscript-begin", StringComparison.Ordinal) &&
            source.IndexOf("% texeed-postscript-end", StringComparison.Ordinal) < source.IndexOf("补充论证", StringComparison.Ordinal) &&
            source.Contains("附言（示例模型 Ω）：检查 50\\% 与 a\\_b。 \\#x \\$y\\$ \\verb|\\|path \\verb|^| \\verb|~| 继续核实。", StringComparison.Ordinal),
            "The note must be escaped text in the TeX document head, before the body");
        var texOutput = Environment.GetEnvironmentVariable("TEXEED_POSTSCRIPT_TEX_OUT");
        if (!string.IsNullOrWhiteSpace(texOutput)) File.WriteAllText(texOutput, source);
        check(document.Comments.Count == 2 && document.Comments.All(c => c.Length > 0) &&
            document.Comments.Any(c => c.Text == "需要补充条件" && document.Source.Substring(c.Start, c.Length) == "补充论证"),
            "The postscript must not create an unanchored comment or lose edit-reason anchor");
        var next = document.Snapshot();
        AiPanel.ApplyMentorPlan(book, next, [], new AiMentorPlan("", [], [], "新的摘要。"), "另一个模型");
        check(document.Source.Contains("附言（另一个模型）：新的摘要。", StringComparison.Ordinal) &&
            !document.Source.Contains("检查 50", StringComparison.Ordinal) && document.Source.Split("% texeed-postscript-begin").Length == 2,
            "A subsequent note must replace the former model note rather than create a conversation transcript");
        var userUpdate = document.Source.Replace("新的摘要。", "我整理过的摘要。", StringComparison.Ordinal);
        check(document.TryApply(document.Revision, userUpdate, "用户"), "Could not create user-edited postscript fixture");
        var userNoteStart = document.Source.IndexOf("我整理过的摘要。", StringComparison.Ordinal);
        document.Comments.Add(new AiDocumentComment { Id = "user-note", Start = userNoteStart, Length = "我整理过的摘要。".Length, Quote = "我整理过的摘要。", Text = "保留这句" });
        var withUserEdit = document.Snapshot();
        var bodyOffset = withUserEdit.Source.IndexOf("补充论证", StringComparison.Ordinal);
        AiPanel.ApplyMentorPlan(book, withUserEdit, [], new AiMentorPlan("", [new AiMentorEdit(new AiDocumentEdit(bodyOffset, 4, "改进论证"), "")], [], "新的摘要。"), "模型");
        var protectedComment = document.Comments.Single(c => c.Id == "user-note");
        check(document.Source.Contains("我整理过的摘要。") && document.Source.Contains("附言（模型）：新的摘要。") && document.Source.Contains("改进论证") &&
            !protectedComment.Orphaned && document.Source.Substring(protectedComment.Start, protectedComment.Length) == protectedComment.Quote &&
            document.AuthorSpans.Any(span => span.Role == "user" && document.Source.Substring(span.Start, span.Length).Contains("我整理过")),
            "A user-edited and commented note must be preserved while the new note and body edit apply");
        var fresh = document.Snapshot();
        AiPanel.ApplyMentorPlan(book, fresh, [], new AiMentorPlan("", [], [], "最新摘要。"), "模型");
        protectedComment = document.Comments.Single(c => c.Id == "user-note");
        check(document.Source.Contains("最新摘要。") && !document.Source.Contains("附言（模型）：新的摘要。") && document.Source.Contains("我整理过的摘要。") &&
            document.Source.Substring(protectedComment.Start, protectedComment.Length) == protectedComment.Quote,
            "Updating the latest model summary must preserve the user note and its anchor without stacking model summaries");
        var editNote = document.Snapshot();
        var noteOffset = editNote.Source.IndexOf("最新摘要。", StringComparison.Ordinal);
        AiPanel.ApplyMentorPlan(book, editNote, [], new AiMentorPlan("", [new AiMentorEdit(new AiDocumentEdit(noteOffset, "最新摘要。".Length, "应用户要求改写。"), "")], [], "本次总结。"), "模型");
        check(document.Source.Contains("应用户要求改写。") && document.Source.Contains("本次总结。") && document.Source.Contains("我整理过的摘要。"),
            "An explicit AI edit to the current note must survive a new postscript summary");
        var afterEditNote = document.Snapshot();
        AiPanel.ApplyMentorPlan(book, afterEditNote, [], new AiMentorPlan("", [], [], "后续总结。"), "模型");
        check(document.Source.Contains("应用户要求改写。") && document.Source.Contains("后续总结。") && !document.Source.Contains("本次总结。"),
            "A later update must retain user-requested AI edits while replacing the latest AI summary");
        var stale = document.Snapshot();
        check(document.TryApply(document.Revision, document.Source.Replace("改进论证", "用户再改论证", StringComparison.Ordinal), "用户"), "Could not create concurrent-edit fixture");
        var changed = document.Source; var failed = false;
        try { AiPanel.ApplyMentorPlan(book, stale, [], new AiMentorPlan("", [], [], "另一摘要。"), "模型"); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && document.Source == changed, "A stale request overwrote a concurrent user edit");
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
            var first = new AiTurn("a", "a"); conversation.Turns.Add(first);
            var comment = new AiDocumentComment { Id = "frozen-comment", Text = "原评论", Replies = [new AiDocumentReply { Text = "原回复" }] };
            var pending = new AiTurn("d", "[本轮未完成]") { MentorRequest = new() { Source = "原文稿", References = [new AiMentorReference(conversation.Id, conversation.Title, comment.Snapshot())] } };
            var checkpoint = await AiTurnCheckpoint.StartAsync(project, conversation.Id, conversation.Title, pending, new AiStreamBuffer());
            await checkpoint.StopAsync();
            AiTurnCheckpoint.Recover(project, book);
            var recovered = conversation.Turns.Single(turn => turn.Id == pending.Id).MentorRequest!;
            check(recovered.Source == "原文稿" && recovered.References.Single().Comment.Replies.Single().Text == "原回复", "Recovery lost the frozen document or discussion snapshot");
            AiTurnCheckpoint.Recover(project, book);
            check(conversation.Turns.Count(turn => turn.Id == pending.Id) == 1, "Repeated recovery duplicated the turn");
            checkpoint.Commit();
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", previousHome); }
    }
}
