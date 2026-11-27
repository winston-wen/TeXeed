using System.IO;
using System.Text.Json;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiRevisionMathAsync(bool realTex)
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        if (!realTex)
        {
            var svg = Path.Combine(AppContext.BaseDirectory, "../../../../../../tests/TeXeed.AppTests/Fixtures/branch-formulas/display-a-plus-b.svg");
            File.Copy(svg, Path.Combine(AiStorage.Home, "math-svg", ChatTexRenderer.CacheFileName("a+b", true)), true);
        }
        var project = Path.Combine(AiStorage.Home, "ai-math-revision-fixture.tex");
        var store = new AiWorkspaceStore(project, new AiNotebook());
        var conversation = store.Book.Active;
        var cited = new AiTurn("上游", @"$$x\label{eq:up}$$");
        var edited = new AiTurn("改稿", "原有 $x$。\n\n" + @"$$x\label{eq:old}$$" + "\n\n| A | B |\n|---|---|\n| 1 | 2 |");
        conversation.Turns.AddRange([cited, edited]);
        using var panel = new AiPanel();
        panel.Bind(store, conversation.Id);
        var draft = panel.BeginRevision(edited);
        var equation = draft.Rows.Single(r => AiRevisionText.IsDisplayFormula(r.Text));
        var table = draft.Rows.Single(r => r.Text.StartsWith('|'));
        static object Change(string row, string text) => new { row, position = "replace", paragraphs = new[] { new { level = 0, parts = new[] { new { kind = "text", value = text } } } } };
        var inline = realTex ? @"$w_1=(b'_1)^{\mathsf T}v+h_1$" : "$A$";
        var body = "发送方算出 " + inline + @"，如公式 \eqref{eq:new} 所示，另见公式 \eqref{1:eq:up}。";
        var display = "$$\n" + (realTex ? @"\begin{aligned}w_1&=(b'_1)^{\mathsf T}v+h_1,\\w'&=b^{\mathsf T}u+h_1.\end{aligned}" : "a+b") +
            @"\label{eq:new}" + "\n$$";
        var rows = AiPanel.ApplyRevisionChanges(draft, JsonSerializer.SerializeToElement(new[] { Change(draft.Rows[0].Id, body), Change(equation.Id, display) }));
        bool rejected = false;
        var tableEdit = draft.Rows.Select(r => r.Id == table.Id ? r with { Text = r.Text.Replace("| A | B |", "| 分类 | 结果 |") } : r).ToList();
        try { AiRevisionText.Normalize(draft, tableEdit, RevisionMathScope.Inline); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "AI table editing leaked into the manual editor");
        rejected = false;
        try { AiRevisionText.Normalize(draft, rows, RevisionMathScope.Inline); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "AI permissions leaked into manual editing");
        await panel.CommitRevisionAsync(edited.Id, draft.Version, rows, "ai", "math-fixture");
        var saved = AiStorage.Load(project).Active.Turns[1];
        Require(saved.Answer.Contains(body) && saved.Answer.Contains(display) && saved.Answer.Contains(table.Text) &&
            saved.RevisionHistory!.Last().Author == "ai" && AiEquationIndex.Resolve(saved, "eq:new") is not null &&
            AiEquationIndex.Resolve(saved, "eq:old") is null, "AI did not save typesettable Markdown and actual resolvable labels");
        var beforeReject = panel.BeginRevision(conversation.Turns[1]);
        foreach (var bad in new[] { inline, @"公式 \eqref{missing}", @"公式 \eqref{77:eq:up}", @"公式 \eqref{eq:new", @"公式 $\eqref{eq:new}$",
            "$x^", realTex ? "$x^2^3$" : @"$\frca{x}{y}$" })
        {
            var item = bad == inline ? equation.Id : draft.Rows[0].Id;
            var invalid = AiPanel.ApplyRevisionChanges(beforeReject, JsonSerializer.SerializeToElement(new[] { Change(item, bad) }));
            rejected = false;
            try { await panel.CommitRevisionAsync(edited.Id, beforeReject.Version, invalid, "ai"); }
            catch (InvalidOperationException ex) when (bad != inline || ex.Message.Contains("仍引用公式")) { rejected = true; }
            Require(rejected && AiStorage.Load(project).Active.Turns[1].Answer == saved.Answer,
                "Broken formula or reference partially persisted: " + bad);
        }
        var remove = JsonSerializer.SerializeToElement(new object[] { Change(draft.Rows[0].Id, "独立数值 512。"), new { row = equation.Id, position = "replace", paragraphs = Array.Empty<object>() } });
        await panel.CommitRevisionAsync(edited.Id, beforeReject.Version, AiPanel.ApplyRevisionChanges(beforeReject, remove), "ai");
        Require(!conversation.Turns[1].Answer.Contains(@"\eqref") && !conversation.Turns[1].Answer.Contains("$$") &&
            conversation.Turns[1].Answer.Contains(table.Text), "Removing equations and their refs damaged other blocks");
        var target = new AiTurn("定义", "$$a+b\\label{keep}$$\n\n$$x$$\n\n" + @"本段引用 \eqref{eq-2}。");
        var reader = new AiTurn("提问", @"用 \eqref{3:keep}、\eqref{3:2} 和 \ref{3:eq-2}。");
        var future = new AiTurn("其他", "正文");
        conversation.Turns.AddRange([target, reader, future]);
        var baseDraft = panel.BeginRevision(target);
        var insert = new { row = baseDraft.Rows[0].Id, position = "before", paragraphs = new[] { new { level = 0, parts = new[] { new { kind = "text", value = "$$x$$" } } } } };
        await panel.CommitRevisionAsync(target.Id, baseDraft.Version,
            AiPanel.ApplyRevisionChanges(baseDraft, JsonSerializer.SerializeToElement(new[] { insert })), "ai");
        var disk = AiStorage.Load(project).Active.Turns;
        var aliases = disk[3].EquationReferenceAliases;
        Require(aliases is not null && aliases["3:2"] == "eq-3" && aliases["3:eq-2"] == "eq-3" &&
            panel.ResolveEquation(reader, "3:2", "2", out _)?.Equation is { Number: "3", Source: "x" } &&
            disk[2].EquationReferenceAliases?.Values.Single() == "eq-3" && disk[3].Answer == reader.Answer,
            "Saved numerical references silently retargeted on insertion");
        future = future with { Answer = @"新写的 \eqref{3:2} 应当引用此时的第二式。" };
        conversation.Turns[^1] = future;
        Require(panel.ResolveEquation(future, "3:2", "2", out _)?.Equation.Source == "a+b",
            "A future reader inherited another reader's historical numeric alias");
        var renumbered = conversation.Turns[2];
        var latestDraft = panel.BeginRevision(renumbered);
        var first = latestDraft.Rows[0].Id;
        var labeled = latestDraft.Rows[1].Id;
        var numbered = latestDraft.Rows[2].Id;
        static object Split(string row, params string[] texts) => new { row, position = "replace",
            paragraphs = texts.Select(text => new { level = 0, parts = new[] { new { kind = "text", value = text } } }).ToArray() };
        foreach (var changes in new object[][]
        {
            [Change(labeled, "$$a+b$$")],
            [Change(first, @"$$x\label{keep}$$")],
            [Change(first, @"$$x\label{keep}$$"), Change(labeled, "$$a+b$$")],
            [new { row = numbered, position = "replace", paragraphs = Array.Empty<object>() }],
            [Split(numbered, "$$x$$", "$$a+b$$")],
            [Split(labeled, "$$x$$", "$$a+b$$")]
        })
        {
            rejected = false;
            try { await panel.CommitRevisionAsync(target.Id, latestDraft.Version,
                AiPanel.ApplyRevisionChanges(latestDraft, JsonSerializer.SerializeToElement(changes)), "ai"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("仍引用公式")) { rejected = true; }
            Require(rejected && ReferenceEquals(conversation.Turns[2], renumbered) &&
                AiStorage.Load(project).Active.Turns[2].RevisionHistory?.Count == 2,
                "A broken, ambiguous or hijacked incoming equation link allowed a partial save");
        }
        var readerDraft = panel.BeginRevision(reader);
        await panel.CommitRevisionAsync(reader.Id, readerDraft.Version, [readerDraft.Rows[0] with { Text = reader.Answer + "已校对。" }], "ai");
        reader = conversation.Turns[3];
        var historyOfReader = reader.RevisionHistory!.Last().Id;
        var draftOnly = new AiTurn("草稿", "独立段落。");
        conversation.Turns.Add(draftOnly);
        var unsaved = panel.BeginRevision(draftOnly);
        draftOnly.UserRevisionDraft = unsaved with { Rows = [unsaved.Rows[0] with { Text = @"草稿 \eqref{3:3}。" }] };
        await panel.CommitRevisionAsync(target.Id, latestDraft.Version, AiPanel.ApplyRevisionChanges(latestDraft,
            JsonSerializer.SerializeToElement(new[] { Split(labeled, "$$x$$", @"$$a+b\label{keep}$$") })), "ai");
        var reloaded = AiStorage.Load(project).Active.Turns;
        var frozen = reloaded[3].RevisionHistory!.Single(v => v.Id == historyOfReader).EquationPreviews!.Values;
        Require(frozen.Single(f => f.Reference == "3:2") is { Caption: "3:3", Source: "x" } &&
            panel.ResolveEquation(reader, "3:2", "2", out _)?.Equation.Number == "4" &&
            panel.ResolveEquation(future, "3:2", "2", out _)?.Equation is { Number: "3", Label: "keep" } &&
            reloaded[^1].UserRevisionDraft?.Rows[0].Text == @"草稿 \eqref{3:3}。" &&
            panel.ResolveEquation(draftOnly, "3:3", "3", out _)?.Equation.Number == "4",
            "A repeated revision confused a reader history, resolved split, live numeric link or open draft");
        var forget = panel.BeginRevision(reader);
        await panel.CommitRevisionAsync(reader.Id, forget.Version, [forget.Rows[0] with { Text = "移去旧的引用。" }], "ai");
        var fresh = panel.BeginRevision(conversation.Turns[3]);
        await panel.CommitRevisionAsync(reader.Id, fresh.Version, [fresh.Rows[0] with { Text = @"现在引用 \eqref{3:2}。" }], "ai");
        Require(panel.ResolveEquation(conversation.Turns[3], "3:2", "2", out _)?.Equation.Number == "2",
            "Reintroducing the same numeral resurrected a retired alias");
        var delta = new AiTurn("定义", @"$$w=\delta$$");
        var corrected = delta with { Answer = @"$$\delta_0=w=\delta_1$$" };
        var indirect = new AiTurn("之后", @"第一个误差 $\delta$ 表示舍入。");
        var unicode = new AiTurn("另一轮", "这里也用到 δ₀ 的定义。");
        var unrelated = new AiTurn("无关", @"$\delta_{10}$ 是另一量。");
        var impacted = AiRevisionImpact.Scan([corrected, indirect, unicode, unrelated], delta, corrected, default);
        Require(impacted.Select(t => t.Target).SequenceEqual([indirect.Id, unicode.Id]), "Indirect symbol dependencies were missed or matched only a substring");
        var sign = delta with { Answer = @"$$\delta=w'-w_0$$" };
        var reversed = delta with { Answer = @"$$\delta=w_0-w'$$" };
        Require(AiRevisionImpact.Scan([reversed, indirect, unrelated], sign, reversed, default) is [{ Target: var affected }] && affected == indirect.Id,
            "Reversing a difference with identical symbol counts skipped readers of its definition");
        var branch = new AiConversation { Title = "此处追问", ParentConversationId = conversation.Id,
            BranchAnchor = new(target.Id, target.Id + "-answer", 0, 1, "摘要", "旧版") };
        var descendant = new AiConversation { Title = "下游", ParentConversationId = branch.Id, InheritedTurns = [target] };
        var outside = new AiConversation { Title = "旁支", ParentConversationId = conversation.Id };
        Require(AiRevisionImpact.Branches([conversation, branch, descendant, outside], conversation, target.Id).SequenceEqual(["此处追问", "下游"]) &&
            descendant.InheritedTurns.Single().Answer == target.Answer, "Related branch scope or immutable inheritance failed");
        Console.WriteLine("PASS: AI-native Markdown / display / local and cross-turn eqref replacements, deletion, invalid-write rollback and manual protection.");
    }
}
