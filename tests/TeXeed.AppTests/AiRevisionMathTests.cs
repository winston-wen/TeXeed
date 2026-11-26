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
        try { AiPanel.ApplyRevisionChanges(draft, JsonSerializer.SerializeToElement(new[] { Change(table.Id, "改表") })); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "AI could replace a protected table");
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
        foreach (var bad in new[] { @"$x$", @"公式 \eqref{missing}", @"公式 \eqref{77:eq:up}", @"公式 \eqref{eq:new", @"公式 $\eqref{eq:new}$",
            "$x^", realTex ? "$x^2^3$" : @"$\frca{x}{y}$" })
        {
            var item = bad == "$x$" ? equation.Id : draft.Rows[0].Id;
            var invalid = AiPanel.ApplyRevisionChanges(beforeReject, JsonSerializer.SerializeToElement(new[] { Change(item, bad) }));
            rejected = false;
            try { await panel.CommitRevisionAsync(edited.Id, beforeReject.Version, invalid, "ai"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && AiStorage.Load(project).Active.Turns[1].Answer == saved.Answer,
                "Broken formula or reference partially persisted: " + bad);
        }
        var remove = JsonSerializer.SerializeToElement(new object[] { Change(draft.Rows[0].Id, "独立数值 512。"), new { row = equation.Id, position = "replace", paragraphs = Array.Empty<object>() } });
        await panel.CommitRevisionAsync(edited.Id, beforeReject.Version, AiPanel.ApplyRevisionChanges(beforeReject, remove), "ai");
        Require(!conversation.Turns[1].Answer.Contains(@"\eqref") && !conversation.Turns[1].Answer.Contains("$$") &&
            conversation.Turns[1].Answer.Contains(table.Text), "Removing equations and their refs damaged other blocks");
        Console.WriteLine("PASS: AI-native Markdown / display / local and cross-turn eqref replacements, deletion, invalid-write rollback and manual protection.");
    }
}
