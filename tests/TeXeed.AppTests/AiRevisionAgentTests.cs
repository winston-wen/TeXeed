using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionAgentAsync(MainWindow window, string output, bool realTex)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        if (!realTex)
        {
            SeedRevisionFormula("x=1");
            SeedRevisionFormula(@"x=1+\delta_0");
        }
        var folder = Path.Combine(output, "agent-project");
        Directory.CreateDirectory(folder);
        var project = Path.Combine(folder, "revision-agent.tex");
        File.WriteAllText(project, "\\documentclass{article}\n\\begin{document}Revision fixture.\\end{document}");
        var seed = new AiNotebook();
        seed.Active.Permission = AiPermission.FullAccess;
        var target = new AiTurn("原题", "先写一句。再用 $x$ 作说明。\n\n多余段。");
        var other = new AiTurn("另一个问题", "另一轮的原文。");
        var related = new AiTurn("依据哪一轮？", "第1轮沿用了原先的表述。");
        seed.Active.Turns.AddRange([target, other, related]);
        var anchor = new AiAnswerAnchor(target.Id, target.Id + "-answer", 0, 4, "先写一句", AiStorage.Hash(target.Answer));
        var branch = new AiConversation { ParentConversationId = seed.Active.Id, BranchAnchor = anchor, Title = "分叉提问" };
        seed.Conversations.Add(branch);
        AiStorage.Save(project, seed);
        await CallAsync(window, "OpenAsync", project);
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var handler = new RevisionFixtureServer();
        using var panel = new AiPanel(new AiConnection(handler));
        panel.CreateToolRunner = () => window.CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        async Task Reload()
        {
            var store = await AiWorkspaceStore.LoadAsync(project);
            panel.Bind(store, seed.Active.Id);
            var models = (ComboBox)panel.Settings.FindName("Models");
            models.ItemsSource = new[] { new AiModel("fixture", "改稿测试模型") };
            models.SelectedIndex = 0;
        }
        await Reload();
        async Task Send(string text, bool authorized, params Func<string[], JsonObject>[] responses)
        {
            handler.Steps = new(responses); handler.Authorized = authorized; handler.Permission = panel.Permission; handler.Failure = null;
            ((TextBox)panel.FindName("Composer")).Text = text;
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            if (handler.Failure is { } error) throw new InvalidOperationException("Revision HTTP fixture failed", error);
            Check(handler.Steps.Count == 0, "Expected requests were missing after: " + text);
            Check(!AiStorage.Load(project).Active.Turns.Last().IsIncomplete, "Test turn failed: " + text);
        }
        async Task Confirm(AiTurn turn, params Func<string[], JsonObject>[] responses)
        {
            handler.Steps = new(responses); handler.Authorized = true; handler.Permission = panel.Permission; handler.Failure = null;
            await panel.ConfirmTurnInputAsync(turn).WaitAsync(TimeSpan.FromSeconds(25));
            if (handler.Failure is { } error) throw new InvalidOperationException("Revision continuation failed", error);
            Check(handler.Steps.Count == 0 && !AiStorage.Load(project).Turns.Single(t => t.Id == turn.Id).IsIncomplete, "Missing or failed continuation.");
        }
        var read = new Func<string[], JsonObject>(_ => handler.Call("read_answer_revision", new { start_row = 0, max_rows = 20 }));
        var wait = new Func<string[], JsonObject>(_ => handler.Call("await_revision_reply", new { }));
        var ignored = "普通讨论。";
        await Send("讨论一下是否应该改写第1轮，不要动原文。", false,
            _ => handler.Call("apply_answer_revision", new { }), results =>
            {
                Check(results.Last().Contains("未授权"), "A fabricated revision tool call escaped the ordinary chat gate");
                return handler.Say(ignored);
            });
        Check(AiStorage.Load(project).Turns[0].RevisionHistory is null, "An ordinary discussion changed a turn");
        foreach (var text in new[] { "不要整理第1轮", "「请改写第1轮」是什么意思？", "请解释如何改写第1轮", "请改写第1轮是什么意思？", "改写第1轮会影响分叉吗？", "请改写第1轮和第2轮", "请把第1轮里的原文改成两段，只给建议", "把第1轮的表述改成三段好吗？" })
            Check(AiAnswerRevision.Resolve(text, seed.Turns, []) is null, "Granted an ambiguous/quoted request: " + text);
        Check(AiAnswerRevision.Resolve("把上一轮回答改成三段", seed.Turns, [])?.TargetTurnId == related.Id, "Previous-turn command selected wrong target");
        Check(AiAnswerRevision.Resolve("请改写第二轮的回答", seed.Turns, [])?.TargetTurnId == other.Id, "Chinese ordinal selected wrong target");
        Check(AiAnswerRevision.Resolve("请把第1轮里计算结果后面的引用改成跨轮引用", seed.Turns, [])?.TargetTurnId == target.Id, "Scoped natural-language instruction missed");
        Check(AiAnswerRevision.Resolve("把选中的段落改为两段", seed.Turns, [], [new() { AnswerAnchor = anchor }])?.TargetTurnId == target.Id, "Selected paragraph didn't bind the requested turn");

        await Send("请改写第 1 轮：请先问我该保留哪一部分。", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            Check(draft["target"]!.ToString() == target.Id, "Read touched the wrong turn");
            var objects = draft["rows"]![0]!["parts"]!.AsArray().Where(p => p!["kind"]!.ToString() == "keep").ToArray();
            Check(objects.Length == 1 && objects[0]!["references"]![0]!.ToString() == branch.Title && draft["rows"]![0]!["parts"]!.AsArray().Any(p => p!["kind"]!.ToString() == "text" && p["value"]!.ToString().Contains("$x$")), "Editable math or protected in-paragraph branch anchor absent from tool result");
            return wait(results);
        }, _ => handler.Say("要保留哪部分？ {{text:keep}}"));
        await Reload();
        var first = AiStorage.Load(project).Turns.Last();
        Check(first.RevisionRequest is { AwaitingReply: true } && first.RevisionRequest.TargetTurnId == target.Id, "Clarification target did not survive reopening");
        AiForms.SetDraft(((AiNotebook)Field(panel, "_book")!).Turns.Last(), "text", "keep", "已经填过这个框");
        await Send("今天只讨论别的题。", false, _ => handler.Say(ignored));
        // An unrelated discussion should not grant writes even if there was a partially filled form.
        await Confirm(((AiNotebook)Field(panel, "_book")!).Turns.Single(t => t.Id == first.Id), wait, _ => handler.Say("保留哪个公式？ {{text:formula}}"));
        await Reload();
        var book = (AiNotebook)Field(panel, "_book")!;
        var last = book.Turns.Single(t => t.Id == first.Id);
        AiForms.SetDraft(last, "text", "formula", "保留 x，保留引用，删掉第二段。");
        string? application = null;
        JsonObject Apply(string targetId, string version, string rowId, object[] parts, string? deleted = null)
        {
            var changes = new List<object> { new { row = rowId, position = "replace", paragraphs = new[] { new { level = 0, parts } } } };
            if (deleted is not null) changes.Add(new { row = deleted, position = "replace", paragraphs = Array.Empty<object>() });
            return handler.Call("apply_answer_revision", new { target = targetId, version, changes });
        }
        string version = "", row = "", unwanted = "";
        await Confirm(last, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            Check(draft["target"]!.ToString() == target.Id, "Form reply was rebound to the latest question");
            Check(draft["task_history"]![0]!["instruction"]!.ToString().Contains("先问我") &&
                draft["task_history"]![0]!["replies"]!.AsArray().Count == 2, "The task lost its original request or confirmed replies");
            version = draft["version"]!.ToString(); row = draft["rows"]![0]!["id"]!.ToString(); unwanted = draft["rows"]![1]!["id"]!.ToString();
            return Apply(other.Id, version, row, []);
        }, results =>
        {
            Check(results.Last().Contains("目标与用户选定"), "Cross-target tool submission accepted");
            return Apply(target.Id, version, row, [new { kind = "text", value = "丢掉对象" }]);
        }, results =>
        {
            Check(results.Last().Contains("缺少原段落"), "A proposal could drop immutable content");
            var good = Apply(target.Id, version, row, [new { kind = "text", value = "新说明" }, new { kind = "keep", value = "0" },
                new { kind = "text", value = "保留 $x$。" }], unwanted);
            application = good["arguments"]!.ToString(); return good;
        }, results =>
        {
            Check(results.Last().Contains("已保存"), "Target update failed: " + results.Last());
            return handler.Call("apply_answer_revision", JsonNode.Parse(application!));
        }, results =>
        {
            Check(results.Last().Contains("已保存"), "Repeated equivalent application was not idempotent");
            return handler.Say("已改好。");
        });
        var saved = AiStorage.Load(project);
        var answer = saved.Turns[0]; var history = answer.RevisionHistory!;
        Check(history.Count == 2 && history.Last().Author == "ai" && history.Last().OperationId is not null, "Duplicate or unattributed revision history");
        Check(history.First().Sources.Values.Single() == target.Answer && answer.Answer == "新说明保留 $x$。", "Latest and original content not reliably persisted");
        Check(history.Last().Rows.Single().Links.Single() is { Offset: 3 } link && link.Id == branch.Id, "Relocated branch link was lost or duplicated");
        Check(saved.Turns[1].Answer == other.Answer && saved.Turns.Skip(1).All(t => t.RevisionHistory is null), "History was saved on a question or clarification turn");
        Check(saved.Turns.Single(t => t.Id == first.Id).RevisionRequest is { AwaitingReply: false } finished && finished.TaskId == first.RevisionRequest!.TaskId, "Task identity changed or permission stayed open");
        await Reload();
        book = (AiNotebook)Field(panel, "_book")!;
        var resume = (AiRevisionRunner)typeof(AiPanel).GetMethod("CreateRevisionRunner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, [first.RevisionRequest!, (Action<bool>)(_ => { })])!;
        var replayed = await resume("apply_answer_revision", application!, default);
        Check(replayed.Contains("已保存") && replayed.Contains(related.Id) && !replayed.Contains(other.Id) &&
            AiStorage.Load(project).Turns[0].RevisionHistory!.Count == 2, "Replay after reopening duplicated a save or lost the related-turn scan");
        await Send("继续改稿：测试任务已经结束。", false, _ => handler.Say(ignored));

        await Send("把第 2 轮回答改成一句话", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            var saveUserChange = panel.Dispatcher.Invoke(() =>
            {
                var original = book.Turns.Single(t => t.Id == other.Id);
                var current = panel.BeginRevision(original);
                return panel.CommitRevisionAsync(other.Id, current.Version, [current.Rows.Single() with { Text = "用户在等待期间保存了另一种表述。" }], "user");
            });
            saveUserChange.GetAwaiter().GetResult();
            return Apply(other.Id, draft["version"]!.ToString(), draft["rows"]![0]!["id"]!.ToString(), [new { kind = "text", value = "过时的版本。" }]);
        }, results =>
        {
            Check(results.Last().Contains("版本已变化"), "A stale AI tool write replaced a concurrent user edit");
            return handler.Say("用户已经更新此轮。");
        });
        var concurrent = AiStorage.Load(project).Turns[1];
        Check(concurrent.Answer == "用户在等待期间保存了另一种表述。" && concurrent.RevisionHistory!.Last().Author == "user", "Concurrent edit wasn't reliably saved");
        var actual = book.Turns.Single(t => t.Id == other.Id);
        actual.UserRevisionDraft = panel.BeginRevision(actual) with { Version = "previously-saved-version" };
        const string lateDraft = "模型读取以后用户接着写的内容。";
        await Send("请整理第2轮。", true, read, results =>
        {
            var draft = JsonNode.Parse(results.Last())!;
            var savedTurn = AiStorage.Load(project).Turns[1];
            Check(savedTurn.UserRevisionDraft == null && savedTurn.RevisionHistory!.Count == concurrent.RevisionHistory!.Count,
                "A matching restored draft manufactured another history entry");
            panel.Dispatcher.Invoke(() =>
            {
                var current = book.Turns.Single(t => t.Id == other.Id);
                var basis = panel.BeginRevision(current);
                current.UserRevisionDraft = basis with { Rows = [basis.Rows[0] with { Text = lateDraft }] };
            });
            return Apply(other.Id, draft["version"]!.ToString(), draft["rows"]![0]!["id"]!.ToString(), [new { kind = "text", value = "与手动草稿冲突。" }]);
        }, results =>
        {
            Check(results.Last().Contains("版本已变化"), "Concurrent unsaved user draft was clobbered");
            return read(results);
        }, results =>
        {
            Check(results.Last().Contains(lateDraft), "Rereading did not return the newly saved user draft");
            return handler.Say("已核对最新稿。");
        });
        Check(AiStorage.Load(project).Turns[1] is { Answer: lateDraft, UserRevisionDraft: null } &&
            AiStorage.Load(project).Turns[1].RevisionHistory!.Last().Author == "user", "Late edits were not preserved as user history");
        await Send("请改写第1轮：请先向我确认。", true, wait, _ => handler.Say("请回复想保留的内容。"));
        await panel.CancelTurnInputAsync(book.Turns.Last());
        await Send("继续改稿：这次已经取消。", false, _ => handler.Say(ignored));
        await Send("第1轮才是文字所在轮次，无需另加前缀。", false, _ => handler.Say("对应段落可写成一行。"));
        JsonObject WriteFirst(string result, string text, bool keep = true)
        {
            var page = JsonNode.Parse(result)!;
            var firstRow = page["rows"]![0]!;
            var parts = new List<object> { new { kind = "text", value = text } };
            if (keep) parts.AddRange(firstRow["parts"]!.AsArray().Where(p => p!["kind"]!.ToString() == "keep")
                .Select(p => new { kind = "keep", value = p!["value"]!.ToString() }));
            return Apply(page["target"]!.ToString(), page["version"]!.ToString(), firstRow["id"]!.ToString(), [.. parts]);
        }
        await Send("你倒是写过去啊", true, _ => handler.Say("建议换一种表述。"), read, r => WriteFirst(r.Last(), "写回第一个回答。"), r =>
        {
            Check(r.Last().Contains(related.Id) && !r.Last().Contains(other.Id), "Related-turn scan missed a dependency or granted an unrelated turn");
            return handler.Call("read_related_answer_revision", new { target = related.Id, start_row = 0, max_rows = 2 });
        }, r => WriteFirst(r.Last(), "第1轮采用了新的表述。"), _ => handler.Say("已写回。"));
        Check(AiStorage.Load(project).Turns[2].RevisionHistory is [{ Id: "original" }, { Author: "ai" }] &&
            AiStorage.Load(project).Turns[2].Answer == "第1轮采用了新的表述。", "Dependent answer was not saved to its own revision history");

        async Task Decide(string choice)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Window? dialog;
            while ((dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "允许修改会话正文？")) is null)
                await Task.Delay(20, timeout.Token);
            dialog.UpdateLayout();
            Check(Descendants<Expander>(dialog).Any(e => Equals(e.Header, "查看拟保存的正文")), "Approval did not show the proposed answer");
            Descendants<Button>(dialog).Single(b => Equals(b.Tag, choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        book.Active.Permission = AiPermission.AskBeforeShell;
        var previousCount = book.Turns[2].RevisionHistory!.Count;
        var denied = Decide("deny");
        await Send("请改写第3轮。", true, read, r => WriteFirst(r.Last(), "拒绝本次应用。"), r =>
        {
            Check(r.Last().Contains("拒绝"), "Refusal didn't reach the model");
            return handler.Call("apply_answer_revision", new { });
        }, r => { Check(r.Last().Contains("拒绝"), "Tool replay bypassed refusal"); return handler.Say("已保留原文。"); });
        await denied;
        Check(AiStorage.Load(project).Turns[2].RevisionHistory!.Count == previousCount, "Refusal wrote history");
        var allowed = Decide("once");
        await Send("请改写第3轮。", true, read, r => WriteFirst(r.Last(), "确认修改。"), _ => handler.Say("已修改。"));
        await allowed;
        Check(AiStorage.Load(project).Turns[2].Answer == "确认修改。", "Approved proposal wasn't saved");
        book.Active.Permission = AiPermission.ReadOnly;
        await Send("请改写第3轮。", false, _ => handler.Call("apply_answer_revision", new { }), r =>
        {
            Check(r.Last().Contains("未授权"), "Read-only permission granted a revision");
            return handler.Say("此会话为只读。");
        });
        Check(AiStorage.Load(project).Turns[2].RevisionHistory!.Count == previousCount + 1, "Read-only permission changed history");
        // A second panel can tighten permissions while this panel is waiting for
        // the shared store's ordered writer, after completing its formula checks.
        var sharedStore = (AiWorkspaceStore)Field(panel, "_store")!;
        var writerGate = (SemaphoreSlim)Field(sharedStore, "_gate")!;
        foreach (var restricted in new[] { AiPermission.AskBeforeShell, AiPermission.ReadOnly })
        {
            book.Active.Permission = AiPermission.FullAccess;
            var current = panel.BeginRevision(book.Turns[2]);
            await writerGate.WaitAsync();
            Task write;
            try
            {
                write = panel.CommitRevisionAsync(related.Id, current.Version, [current.Rows[0] with { Text = "过期权限的写入。" }], "ai");
                Check(!write.IsCompleted, "Concurrent permission fixture did not reach the pending write");
                book.Active.Permission = restricted;
            }
            finally { writerGate.Release(); }
            try { await write; throw new InvalidOperationException("A pending commit bypassed tightened permissions"); }
            catch (UnauthorizedAccessException) { }
            Check(AiStorage.Load(project).Turns[2].Answer == "确认修改。", "Permission changed while saving but the update reached disk");
        }
        var mathematical = new AiTurn("原问题", @"$$a=1\label{a}$$");
        var annotatedSource = new AiTurn("复核公式", @"$$x=1\label{selected}\tag{6}$$");
        var snapshot = new AiAnswerAnchor(annotatedSource.Id, annotatedSource.Id + "-answer", 0, 8,
            annotatedSource.Answer, AiStorage.Hash(annotatedSource.Answer))
            { PreviewKind = "formula", PreviewSource = "x=1", PreviewNumber = "6" };
        AiEntry Annotation(string question) => new() { Title = "AI 回答批注", AnswerAnchor = snapshot, Question = question };
        var note = Annotation(@"改公式6，加入我设立的 $\delta_0$。检查一下前后文，自动修改不一致的符号。");
        var renumbered = annotatedSource with { Answer = @"$$x=1\label{selected}\tag{7}$$",
            RevisionHistory = [new("original", "ai", DateTimeOffset.UtcNow, [])
                { Sources = new() { [snapshot.SegmentId] = annotatedSource.Answer } }] };
        Check(AiAnnotationRevision.MatchesSelection(note, renumbered, out var currentTag) && currentTag == "7" &&
            !AiAnnotationRevision.MatchesSelection(note, renumbered with { Answer = @"$$x=2\label{selected}\tag{6}$$" }, out _),
            "Selection binding failed after renumbering or mistook a replacement for the snapshot");
        var plain = new AiTurn("原问题", "甲句。乙句。");
        var plainNote = new AiEntry { AnswerAnchor = new(plain.Id, plain.Id + "-answer", 0, 2, "甲句", AiStorage.Hash(plain.Answer)), Question = "改写这段。" };
        var edited = plain with { Answer = "甲句。丙句。", RevisionHistory = [new("original", "ai", DateTimeOffset.UtcNow, [])
            { Sources = new() { [plain.Id + "-answer"] = plain.Answer } }] };
        Check(AiAnnotationRevision.MatchesSelection(plainNote, edited, out _) &&
            !AiAnnotationRevision.MatchesSelection(plainNote, edited with { Answer = "甲句。甲句。" }, out _), "Plain quote rebind was ambiguous or unnecessarily rejected");
        Check(AiAnswerRevision.Resolve("只讨论，不改原文。", [annotatedSource], [], [note]) == null &&
            AiAnswerRevision.Resolve("请先不要修改公式。", [annotatedSource], [], [note]) == null &&
            AiAnswerRevision.Resolve("", [annotatedSource], [], [Annotation("改公式6有什么意义？")]) == null &&
            AiAnswerRevision.Resolve("", [annotatedSource], [], [Annotation("改公式8。")]) == null &&
            AiAnswerRevision.Resolve("", [annotatedSource, plain], [], [Annotation("请修改第2轮。")]) == null &&
            AiAnswerRevision.Resolve("", [annotatedSource, plain], [], [note, plainNote]) == null, "A question, veto, conflicting object or conflicting targets enabled writes");
        const string tableSource = "| 类型 | 目标 |\n| --- | --- |\n| 搜索 | 待求值 |";
        var tableTurn = new AiTurn("比较两类问题", tableSource);
        var tableNote = new AiEntry { AnswerAnchor = new(tableTurn.Id, tableTurn.Id + "-answer", 0,
            tableSource.Length, tableSource, AiStorage.Hash(tableSource)) { PreviewTableMarkdown = tableSource } };
        foreach (var command in new[] { "修改这张表的第二列。", "请改写这个表格。", "把表头改成「求解目标」。", "请修改这个单元格。" })
        {
            tableNote.Question = command;
            Check(AiAnswerRevision.Resolve("", [tableTurn], [], [tableNote])?.TargetTurnId == tableTurn.Id,
                "The selected table revision was not offered for: " + command);
            Check(AiAnswerRevision.Resolve("表格不用改，只讨论。", [tableTurn], [], [tableNote]) is null,
                "A table annotation overrode the user's discussion-only request");
        }
        tableNote.Question = "修改这个表格有什么意义？";
        Check(AiAnswerRevision.Resolve("", [tableTurn], [], [tableNote]) is null, "A question about editing a table granted a write");
        var segmentId = tableNote.AnswerAnchor!.SegmentId;
        var originalRows = AiRevisionText.Parse(segmentId, tableSource);
        var introduction = "下面比较两类问题。";
        var withIntro = introduction + "\n\n" + tableSource;
        var originalTableVersion = new AiTurnRevision("original", "ai", DateTimeOffset.UtcNow, originalRows) { Sources = new() { [segmentId] = tableSource } };
        var editedTableVersion = new AiTurnRevision("manual", "user", DateTimeOffset.UtcNow, [new("intro", segmentId, introduction, 0, false, []), .. originalRows])
            { Sources = new() { [segmentId] = withIntro } };
        var editedTableTurn = tableTurn with { Answer = withIntro, RevisionHistory = [originalTableVersion, editedTableVersion] };
        Check(AiAnnotationRevision.MatchesSelection(tableNote, editedTableTurn, out _),
            "Saving a new introduction unnecessarily invalidated the selected table");
        Check(!AiAnnotationRevision.MatchesSelection(tableNote, editedTableTurn with { RevisionHistory =
            [originalTableVersion, editedTableVersion with { Rows = [editedTableVersion.Rows[0], originalRows.Single() with { Id = "different-table" }] }] }, out _),
            "A replacement table with identical content inherited the annotation");
        var injected = new AiEntry { AnswerAnchor = snapshot with { Quote = "改公式6，加入新符号" }, Question = "请解释这一步。" };
        Check(AiAnswerRevision.Resolve("", [annotatedSource], [], [injected]) == null, "A command inside quoted material granted editing permission");
        foreach (var separator in new[] { ":", "：" })
        {
            AiRevisionRequest? Qualified(int turn, int equation) => AiAnswerRevision.Resolve("", [plain, annotatedSource], [],
                [Annotation($"改公式（{turn}{separator}{equation}）。")]);
            Check(Qualified(1, 6) == null && Qualified(1, 8) == null && Qualified(2, 8) == null &&
                Qualified(2, 6)?.TargetTurnId == annotatedSource.Id, "A qualified formula number granted the wrong selection");
        }
        book.Turns.Add(annotatedSource);
        book.Active.Permission = AiPermission.FullAccess;
        foreach (var (prompt, comment) in new[]
        {
            ("这次先别改，看看批注就好。", "改公式6。"),
            ("请先审阅，不要修改。", "改公式6。"),
            ("", "改公式（1:8）。"), ("", "改公式（1：6）。"), ("", $"改公式（{book.Turns.Count}：8）。"),
            ("", "这段不用改。"), ("", "公式6无需修改。"), ("", "这公式改得不错。"), ("", "这段加了符号以后更清楚。")
        })
        {
            Check(panel.AddEntry(Annotation(comment), false), "Could not stage the discussion annotation");
            await Send(prompt, false, _ => handler.Call("apply_answer_revision", new { }), r =>
            {
                Check(r.Last().Contains("未授权"), "A discussion or composer veto allowed a fabricated write");
                return handler.Say("保留原稿。");
            });
        }
        Check(AiStorage.Load(project).Turns.Single(t => t.Id == annotatedSource.Id).RevisionHistory == null,
            "A statement or negation saved an unsolicited revision");
        book.Active.Permission = AiPermission.ReadOnly;
        Check(panel.AddEntry(Annotation("请修改这个公式。"), false), "Could not stage a read-only annotation");
        await Send("", false, _ => handler.Say("当前会话为只读。"));
        book.Active.Permission = AiPermission.AskBeforeShell;
        Check(panel.AddEntry(Annotation("请修改这个公式。"), false), "Could not stage an approval annotation");
        var veto = Decide("deny");
        await Send("", true, read, r => WriteFirst(r.Last(), @"$$x=2\label{selected}\tag{6}$$"), r =>
        {
            Check(r.Last().Contains("拒绝"), "Declining an annotated revision failed to stop the write");
            return handler.Say("已保留原回答。");
        });
        await veto;
        Check(AiStorage.Load(project).Turns.Single(t => t.Id == annotatedSource.Id).RevisionHistory == null,
            "Read-only or denied annotation wrote a revision");
        book.Active.Permission = AiPermission.FullAccess;
        note = await StageFormulaRevisionAnnotationAsync(window, panel, annotatedSource.Id, note.Question);
        Check(note.AnswerAnchor is { PreviewKind: "formula", PreviewNumber: "6", PreviewSource: "x=1" } &&
            ((TextBox)panel.FindName("Composer")).Text == "", "Right-click did not capture the displayed formula with an empty composer");
        await Send("", true, read, r =>
        {
            var instruction = JsonNode.Parse(r.Last())!["task_history"]![0]!["annotations"]![0]!;
            Check(instruction["instruction"]!.ToString() == note.Question && instruction["snapshot_quote"]!.ToString() == note.AnswerAnchor!.Quote &&
                instruction["current_number"]!.ToString() == "6", "The live batch lost its user instruction, quote or target number");
            return wait(r);
        }, _ => handler.Say("请确认新符号。"));
        await Reload();
        book = (AiNotebook)Field(panel, "_book")!;
        var waiting = book.Turns.Last();
        AiForms.SetDraft(waiting, "textarea", "clarification", "就按批注写回。");
        await Confirm(waiting, read, r =>
        {
            var page = JsonNode.Parse(r.Last())!;
            Check(page["target"]!.ToString() == annotatedSource.Id &&
                page["task_history"]![0]!["annotations"]![0]!["instruction"]!.ToString() == note.Question,
                "Reloading or clarification rebound the annotation to another turn");
            return WriteFirst(r.Last(), @"$$x=1+\delta_0\label{selected}\tag{6}$$");
        }, r =>
        {
            Check(r.Last().Contains("已保存"), "Annotation-driven revision failed to save: " + r.Last());
            return handler.Say("已写回。");
        });
        var result = AiStorage.Load(project);
        var noteTurn = result.Turns.Single(t => t.SentAnnotations.Any(e => e.Id == note.Id));
        var historyRows = result.Turns.Single(t => t.Id == annotatedSource.Id).RevisionHistory;
        Check(historyRows is [{ Id: "original" }, { Author: "ai" }] &&
            historyRows.Last().Rows.Single(r => r.Text.Contains(@"\delta_0")).Links.Count(l => l.Id == "turn:" + noteTurn.Id + ":0") == 1 &&
            AiAnswerAnchor.SamePayload(noteTurn.SentAnnotations.Single().AnswerAnchor, note.AnswerAnchor),
            "An annotation revision lost revision history or rewrote the quoted snapshot");
        var changed = mathematical with { Answer = @"$$a=2\label{a}$$" };
        var equationReader = new AiTurn("追问", @"这里使用公式 \eqref{2:a}。")
            { EquationReferenceTurns = new() { ["2:a"] = mathematical.Id } };
        var unchangedReader = new AiTurn("其他", @"只引用本轮同名公式 \eqref{3:a}。" + "\n\n" + @"$$c=3\label{a}$$");
        Check(AiRevisionImpact.Scan([changed, equationReader, unchangedReader], mathematical, changed, default)
            is [{ Target: var found }] && found == equationReader.Id, "A cross-turn equation stopped triggering dependency checks after an earlier turn was removed");
        Check(AiRevisionImpact.Scan([mathematical, equationReader], mathematical, mathematical, default).Count == 0, "An unchanged equation was flagged");
        var locked = new AiRevisionRow("formula", "s", "$$x=1$$", 0, true, []);
        var lockedDraft = new AiRevisionDraft("turn", "version", [locked]);
        var added = AiPanel.ApplyRevisionChanges(lockedDraft, JsonSerializer.SerializeToElement(
            new[] { new { row = "formula", position = "before", paragraphs = new[] { new { level = 1, parts = new[] { new { kind = "text", value = "公式前的标题" } } } } },
                new { row = "formula", position = "after", paragraphs = new[] { new { level = 0, parts = new[] { new { kind = "text", value = "公式后的正文。" } } } } } }));
        Check(AiRevisionText.Normalize(lockedDraft, added) is [{ Level: 1 }, { Locked: true }, { Text: "公式后的正文。" }], "Could not insert on both sides of a locked standalone formula");
        Console.WriteLine("AI revision HTTP/WPF regression passed. Verified contextual write-back, actual approval and refusal, read-only permission, related-turn review, stable cross-turn references, editable math, protected links, task recovery and history.");
    }

    private static async Task<AiEntry> StageFormulaRevisionAnnotationAsync(Window owner, AiPanel panel, string turnId, string instruction)
    {
        var host = new Window { Owner = owner, Content = panel, Width = 800, Height = 720, Left = -2800, Top = -2800,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!await panel.NavigateSameConversationTurnAsync(turnId)) throw new InvalidOperationException("Could not open the formula's turn");
            host.UpdateLayout();
            var view = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turnId).Answers.Single();
            await view.RenderTask;
            var formulaRow = Descendants<ChatFormulaRow>(view).Single();
            var rendered = Descendants<ChatFormula>(formulaRow).Single();
            await rendered.RenderTask;
            if (rendered.Source != "x=1" || rendered.Error != null || !File.Exists(rendered.SvgPath))
                throw new InvalidOperationException("The annotated formula did not render its TeX SVG: " + rendered.Error);
            // The virtual list can realize more neighbors for several frames after
            // navigation. Click the actual formula once it has stopped moving.
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            (Point Position, double Offset, double Extent, int Version)? last = null;
            var stable = System.Diagnostics.Stopwatch.StartNew();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (stable.ElapsedMilliseconds < 200 || last == null)
            {
                await Task.Delay(20, deadline.Token);
                await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var frame = (formulaRow.TranslatePoint(new(), host), scroll.VerticalOffset, scroll.ExtentHeight, view.LayoutVersion);
                if (frame != last) { stable.Restart(); last = frame; }
            }
            var layers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(panel, "_answerLayers")!;
            var popup = (System.Windows.Controls.Primitives.Popup)Field(layers[view], "_popup")!;
            formulaRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            var menu = view.ContextMenu!;
            menu.IsOpen = true;
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "批注到当前会话"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!popup.IsOpen)
            {
                var staged = panel.AnnotationEntries.LastOrDefault();
                var pin = staged?.AnswerAnchor is { } a ? view.AnnotationPoint(staged.Id, a) : null;
                throw new InvalidOperationException($"Formula context menu did not open the annotation editor (selected {view.SelectedReferenceObject()}, pin {pin}, clip {Field(layers[view], "_lastClip")}).");
            }
            Descendants<TextBox>(popup.Child).Single().Text = instruction;
            Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationConfirm")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var entry = panel.AnnotationEntries.Single(e => e.Question == instruction);
            if (popup.IsOpen || !entry.ReferenceSelected) throw new InvalidOperationException("Confirm did not stage the formula annotation");
            return entry;
        }
        finally { host.Content = null; host.Close(); }
    }

    private sealed class RevisionFixtureServer : HttpMessageHandler
    {
        internal Queue<Func<string[], JsonObject>> Steps = new();
        internal bool Authorized;
        internal AiPermission Permission;
        internal Exception? Failure;
        internal Action<JsonNode>? ObserveRequest;
        internal Func<CancellationToken, Task>? BeforeReply;
        internal Func<HttpContent>? StreamReply;
        private int _callId;
        internal JsonObject Call(string name, object? args) => new() { ["type"] = "function_call", ["name"] = name,
            ["call_id"] = "test-" + ++_callId, ["arguments"] = JsonSerializer.Serialize(args) };
        internal JsonObject Say(string text) => new() { ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new InvalidOperationException("Unexpected network endpoint");
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                ObserveRequest?.Invoke(payload);
                if (BeforeReply is not null) await BeforeReply(token);
                if (payload["tools"]!.ToJsonString().Contains("replace_source") != (Permission != AiPermission.ReadOnly) || !payload["tools"]!.ToJsonString().Contains("ask_user"))
                    throw new InvalidOperationException("The request didn't exercise the selected native workspace permission.");
                if (payload["tools"]!.ToJsonString().Contains("read_answer_revision") != Authorized ||
                    payload["instructions"]!.ToString().Contains(AiAnswerRevision.Instructions) != Authorized)
                    throw new InvalidOperationException("Revision schema and instructions disagreed with user authorization.");
                if (StreamReply is not null) return new(HttpStatusCode.OK) { Content = StreamReply() };
                var results = payload["input"]!.AsArray().Where(n => n?["type"]?.ToString() == "function_call_output")
                    .Select(n => n!["output"]!.ToString()).ToArray();
                var reply = Steps.Dequeue()(results);
                var body = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(reply) } };
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + body.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            catch (Exception ex) { Failure = ex; throw; }
        }
    }
}
