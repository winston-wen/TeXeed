using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionTableAsync(MainWindow window, string output, bool realTex, bool measureOnly)
    {
        static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var formulas = new[] { (@"\boldsymbol s", false), (@"\boldsymbol b", false), ("x=1", true) };
        foreach (var (tex, display) in formulas)
        {
            if (!realTex) SeedRevisionFormula(tex, display);
            await ChatTexRenderer.RenderAsync(tex, display);
        }
        const string before = """
            # 问题分类

            这两类问题要求的输出不同。

            | 问题 | 要求攻击者输出什么 |
            | --- | --- |
            | 搜索型 | 一个向量 |
            | 判定型 | 一个判断 |
            | 依据 | 如公式 \eqref{2:definition} 所示。{{text:why}} |
            """;
        static string Rewrite(string text) => text.Replace("这两类问题要求的输出不同。", "两种问题的求解目标不同。")
            .Replace("要求攻击者输出什么", "攻击者求解什么")
            .Replace("一个向量", @"秘密向量 $\boldsymbol s$")
            .Replace("一个判断", @"表示向量 $\boldsymbol b$ 来自真实计算还是随机生成");
        const string instruction = "修改第1轮的表格和引导句。把表头改为「攻击者求解什么」，并修改搜索型和判定型两格。";
        var original = new AiTurn("两种问题有什么区别？", before)
            { AdditionalData = new() { [AiForms.ProtocolKey] = JsonSerializer.SerializeToElement(1) } };
        AiForms.SetDraft(original, "text", "why", "已有答案");
        var definition = new AiTurn("对应的公式是什么？", @"$$x=1\label{definition}$$");
        var book = new AiNotebook();
        book.Active.Permission = AiPermission.FullAccess;
        book.Turns.AddRange([original, definition]);
        var anchor = new AiAnswerAnchor(original.Id, original.Id + "-answer",
            before.IndexOf("搜索型", StringComparison.Ordinal), 3, "搜索型", AiStorage.Hash(before));
        var branch = new AiConversation { Title = "搜索型的追问", ParentConversationId = book.Active.Id, BranchAnchor = anchor };
        book.Conversations.Add(branch);
        var dir = Directory.CreateDirectory(Path.Combine(output, "table-project"));
        var project = Path.Combine(dir.FullName, "table.tex");
        File.WriteAllText(project, @"\documentclass{article}\begin{document}Table.\end{document}");
        AiStorage.Save(project, book);
        await CallAsync(window, "OpenAsync", project);
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var handler = new RevisionFixtureServer();
        using var panel = new AiPanel(new AiConnection(handler));
        panel.CreateToolRunner = () => window.CreateAiToolRunner(panel.Permission, panel.ShellApprovals);
        async Task Reload()
        {
            var store = await AiWorkspaceStore.LoadAsync(project);
            panel.Bind(store, book.Active.Id);
            var models = (ComboBox)panel.Settings.FindName("Models");
            models.ItemsSource = new[] { new AiModel("fixture", "表格回归模型") };
            models.SelectedIndex = 0;
        }
        await Reload();
        if (measureOnly) { await MeasureTableReadAsync(window, panel, original.Id, output); return; }
        var live = (AiNotebook)Field(panel, "_book")!;
        foreach (var command in new[] { instruction, "把第1轮表格中的表头改为求解目标。" })
            Expect(AiAnswerRevision.Resolve(command, live.Turns, [])?.TargetTurnId == original.Id, "Table command missed its target: " + command);
        Expect(AiAnswerRevision.Resolve("讨论一下是否修改第1轮的表格，不要动原文。", live.Turns, []) is null,
            "Discussion about a table enabled writes");

        async Task Send(bool authorized, params Func<string[], JsonObject>[] steps)
        {
            handler.Steps = new(steps);
            handler.Authorized = authorized;
            handler.Permission = panel.Permission;
            handler.Failure = null;
            ((TextBox)panel.FindName("Composer")).Text = instruction;
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (handler.Failure is { } error) throw new InvalidOperationException("Table revision HTTP fixture failed", error);
            Expect(handler.Steps.Count == 0 && !AiStorage.Load(project).Turns.Last().IsIncomplete, "Incomplete table revision conversation");
        }
        JsonObject Read(string[] _) => handler.Call("read_answer_revision", new { start_row = 0, max_rows = 20 });
        JsonObject Apply(string[] results, bool keep = true, bool hideForm = false)
        {
            var page = JsonNode.Parse(results.Last())!;
            Expect(page["target"]!.ToString() == original.Id && page["turn"]!.GetValue<int>() == 1, "Read did not return turn 1");
            var rows = page["rows"]!.AsArray();
            var table = rows.SingleOrDefault(r => r!["kind"]?.ToString() == "table");
            Expect(table is not null && table["locked"]!.GetValue<bool>() == false, "AI saw a read-only or untyped table");
            var keeps = table!["parts"]!.AsArray().Where(p => p!["kind"]!.ToString() == "keep").ToList();
            Expect(keeps.Any(p => p!["source"]!.ToString() == "{{text:why}}") &&
                keeps.Any(p => p!["references"]!.AsArray().Any(l => l!.ToString() == branch.Title)),
                "Table cells did not expose both protected tokens");
            var changes = new List<object>();
            foreach (var row in rows.Where(r => r == table ||
                r!["parts"]?.AsArray().Any(p => p!["value"]?.ToString().Contains("这两类问题") == true) == true))
            {
                var parts = new List<object>();
                foreach (var part in row!["parts"]!.AsArray())
                {
                    var kind = part!["kind"]!.ToString();
                    if (!keep && kind == "keep") continue;
                    var hide = hideForm && part["source"]?.ToString() == "{{text:why}}";
                    if (hide) parts.Add(new { kind = "text", value = "[注](" });
                    parts.Add(new { kind, value = Rewrite(part["value"]!.ToString()) });
                    if (hide) parts.Add(new { kind = "text", value = ")" });
                }
                changes.Add(new { row = row["id"]!.ToString(), position = "replace", paragraphs = new[] { new { level = 0, parts } } });
            }
            Expect(changes.Count == 2, "Fixture did not target the introduction and the table together");
            return handler.Call("apply_answer_revision", new { target = original.Id, version = page["version"]!.ToString(), changes });
        }
        live.Active.Permission = AiPermission.ReadOnly;
        await Send(false, _ => handler.Call("apply_answer_revision", new { }), r =>
        {
            Expect(r.Last().Contains("未授权"), "Read-only table write bypassed permission");
            return handler.Say("保留原稿。");
        });
        live.Active.Permission = AiPermission.FullAccess;
        var target = live.Turns[0];
        var draft = panel.BeginRevision(target);
        Expect(draft.Rows.Single(r => r.Text.StartsWith('|')).Locked, "Table was exposed to manual editing");
        var draftRows = AiRevisionText.Clone(draft.Rows);
        draftRows[1] = draftRows[1] with { Text = "尚未闭合的 $A" };
        target.UserRevisionDraft = draft with { Rows = draftRows };
        await panel.SaveAsync();
        var composer = (TextBox)panel.FindName("Composer");
        composer.Text = instruction;
        var turnCount = live.Turns.Count;
        await panel.SubmitAsync();
        var latest = AiStorage.Load(project).Turns[0];
        Expect(latest.UserRevisionDraft?.Rows[1].Text == draftRows[1].Text && latest.RevisionHistory is null &&
            latest.Answer == before && live.Turns.Count == turnCount && composer.Text == instruction &&
            ((TextBlock)panel.FindName("StatusText")).Text.Contains("定界符"), "A failed user save lost the draft or started the AI task");
        target.UserRevisionDraft = null;
        live.Active.Permission = AiPermission.AskBeforeShell;
        async Task Deny()
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            Window? dialog;
            while ((dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "允许修改会话正文？")) == null)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("No approval prompt for a table edit");
                await Task.Delay(20);
            }
            dialog.UpdateLayout();
            Descendants<Button>(dialog).Single(b => Equals(b.Tag, "deny")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        var deny = Deny();
        await Send(true, Read, r => Apply(r), r =>
        {
            Expect(r.Last().Contains("拒绝") && AiStorage.Load(project).Turns[0].Answer == before, "Refusal allowed a table update");
            return handler.Say("原稿已保留。");
        });
        await deny;
        live.Active.Permission = AiPermission.FullAccess;
        const string manualNote = "用户手动补充的定义。";
        var manualText = before.Replace("这两类问题要求的输出不同。", "这两类问题要求的输出不同。" + manualNote);
        draftRows[1] = draftRows[1] with { Text = draft.Rows[1].Text + manualNote };
        target.UserRevisionDraft = draft with { Rows = draftRows };
        var bookmark = new AiUserBookmark { Anchor = anchor, Label = "题型" };
        live.Active.Bookmarks.Add(bookmark); // The saved draft predates this additional link.
        composer.Text = instruction;
        await panel.SaveAsync();
        turnCount = live.Turns.Count;
        using (File.OpenRead(AiStorage.NotebookPath(project)))
        {
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var blocked = AiStorage.Load(project).Turns[0];
            Expect(live.Turns.Count == turnCount && live.Turns[0].RevisionHistory is null &&
                live.Turns[0].UserRevisionDraft?.Rows[1].Text == draftRows[1].Text && composer.Text == instruction &&
                blocked.Answer == before && blocked.UserRevisionDraft?.Rows[1].Text == draftRows[1].Text &&
                handler.Failure is null, "Disk failure lost edits or allowed an AI request");
        }
        await Reload(); // The draft exists on disk, with no live editor left to finish it.
        string? operation = null;
        string? hiddenField = null;
        await Send(true, r =>
        {
            var preflight = AiStorage.Load(project).Turns[0];
            Expect(preflight.Answer == manualText && preflight.RevisionHistory is [{ Id: "original" }, { Author: "user" }] &&
                preflight.UserRevisionDraft is null, "The first model request ran before the user revision reached disk");
            return Read(r);
        }, r =>
        {
            Expect(r.Last().Contains(manualNote), "The model was given the pre-edit introduction");
            operation = Apply(r)["arguments"]!.ToString();
            hiddenField = Apply(r, hideForm: true)["arguments"]!.ToString();
            return Apply(r, keep: false);
        }, r =>
        {
            Expect(r.Last().Contains("保护") && AiStorage.Load(project).Turns[0].Answer == manualText,
                "Losing a form or branch token produced a partial write: " + r.Last());
            return handler.Call("apply_answer_revision", JsonNode.Parse(hiddenField!));
        }, r =>
        {
            Expect(!r.Last().Contains("已保存") && AiStorage.Load(project).Turns[0].Answer == manualText,
                "A retained field hidden inside a link URL escaped form validation: " + r.Last());
            return handler.Call("apply_answer_revision", JsonNode.Parse(operation!));
        }, r =>
        {
            Expect(r.Last().Contains("已保存到第 1 轮") && r.Last().Contains("已扫描"), "Incorrect save receipt: " + r.Last());
            return handler.Call("apply_answer_revision", JsonNode.Parse(operation!));
        }, r =>
        {
            Expect(r.Last().Contains("已保存到第 1 轮"), "Incorrect replay receipt: " + r.Last());
            return handler.Say("已更新表格和引导句。");
        });
        await Reload();
        var saved = AiStorage.Load(project);
        var result = saved.Turns[0];
        var history = result.RevisionHistory!;
        Expect(result.Answer == Rewrite(manualText) && history is [{ Id: "original" }, { Author: "user" }, { Author: "ai" }] &&
            history[0].Sources.Values.Single() == before && history[1].Sources.Values.Single() == manualText &&
            saved.Turns.Skip(1).All(t => t.RevisionHistory is null),
            "The saved table or its history did not survive reopening on the target turn");
        var tableRow = history[^1].Rows.Single(r => r.Text.StartsWith('|'));
        Expect(tableRow.Links.Select(l => l.Id).ToHashSet().SetEquals([branch.Id, "bookmark:" + bookmark.Id]) &&
            saved.Conversations.Single(c => c.Id == branch.Id).BranchAnchor == anchor &&
            panel.ResolveEquation(result, "2:definition", "definition", out _)?.Equation.Source == "x=1",
            "Saving changed a branch snapshot or the table's cross-turn equation");
        await CheckReopenedTableAsync(window, panel, original.Id, output);
        CheckTableStructureEdits();
        Console.WriteLine("PASS: native turn 1 table and introduction changed atomically through the model tools, duplicate write receipts, " +
            "form and branch ownership, permission, user autosave before AI, history, formulas and rendered cells after reopening.");
    }

    private static void CheckTableStructureEdits()
    {
        const string original = "组别 | 甲\n--- | ---\n一 | 1";
        const string expanded = "组别 | 甲 | 乙\n--- | --- | ---\n一 | 1 | 2\n二 | 3 | 4";
        var draft = new AiRevisionDraft("turn", "version", AiRevisionText.Parse("segment", original + "\n\n结尾。"));
        if (!draft.Rows[0].Locked) throw new Exception("The fixture did not contain a parsed table");
        object Paragraph(string source) => new { level = 0, parts = new[] { new { kind = "text", value = source } } };
        List<AiRevisionRow> Apply(AiRevisionDraft current, string id, string position, params object[] paragraphs)
        {
            var changes = JsonSerializer.SerializeToElement(new[] { new { row = id, position, paragraphs } });
            var proposal = AiPanel.ApplyRevisionChanges(current, changes);
            return AiRevisionText.Normalize(current, proposal, RevisionMathScope.All);
        }
        var changed = Apply(draft, draft.Rows[0].Id, "replace", Paragraph(expanded));
        if (!changed[0].Locked || changed[0].Id != draft.Rows[0].Id || AiRevisionText.Source(changed) != expanded + "\n\n结尾。")
            throw new Exception("Adding a table row and column lost the original owner or source");
        var afterRemoval = Apply(draft with { Rows = changed }, changed[0].Id, "replace");
        if (AiRevisionText.Source(afterRemoval).Trim() != "结尾。") throw new Exception("Removing a table changed neighboring text");
        var added = Apply(draft with { Rows = afterRemoval }, afterRemoval.Single().Id, "after", Paragraph(expanded));
        if (added.Count != 2 || !added[1].Locked || added[1].Text != expanded ||
            AiRevisionText.Parse("segment", AiRevisionText.Source(added)).Count != 2)
            throw new Exception("An inserted table did not round-trip as an independent block");
    }

    private static async Task MeasureTableReadAsync(Window owner, AiPanel panel, string id, string output)
    {
        var host = new Window { Owner = owner, Content = panel, Width = 1000, Height = 780, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            if (!await panel.NavigateSameConversationTurnAsync(id)) throw new InvalidOperationException("Missing table");
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var view = Descendants<AiTurnView>(panel).Single(t => t.Turn.Id == id).Answers.Single();
            await view.RenderTask;
            var samples = new List<object>();
            for (var i = 0; i <= 12; i++)
            {
                var runner = (AiRevisionRunner)Invoke(panel, "CreateRevisionRunner",
                    new AiRevisionRequest(id, Guid.NewGuid().ToString("N")), (Action<bool>)(_ => { }))!;
                var stamp = Stopwatch.GetTimestamp();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var read = await runner("read_answer_revision", """{"start_row":0,"max_rows":20}""", default);
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var elapsed = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                var table = JsonNode.Parse(read)!["rows"]!.AsArray().Last()!;
                if (i != 0) samples.Add(new { ms = elapsed, bytes = allocated, locked = table["locked"]!.GetValue<bool>(),
                    visibleControls = Descendants<Control>(view).Count(c => c.IsVisible) });
            }
            File.WriteAllText(Path.Combine(output, "table-read.json"), JsonSerializer.Serialize(samples));
        }
        finally { host.Content = null; host.Close(); }
    }

    private static async Task CheckReopenedTableAsync(Window owner, AiPanel panel, string id, string output)
    {
        var host = new Window { Owner = owner, Content = panel, Width = 1000, Height = 780, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        try
        {
            host.UpdateLayout();
            if (!await panel.NavigateSameConversationTurnAsync(id)) throw new Exception("Missing saved turn");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            var answer = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == id).Answers.Single();
            await answer.RenderTask;
            var view = Descendants<MarkdownView>(answer).Single(v => v.Document.Blocks.FirstBlock is Table);
            var table = (Table)view.Document.Blocks.FirstBlock!;
            var rows = table.RowGroups.Single().Rows;
            string Cell(int row, int col)
            {
                var cell = rows[row].Cells[col];
                return new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();
            }
            var formulas = Descendants<ChatFormula>(view).ToList();
            await Task.WhenAll(formulas.Select(f => f.RenderTask));
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (rows.Count != 4 || rows.Any(r => r.Cells.Count != 2) || Cell(0, 1) != "攻击者求解什么" ||
                !Cell(1, 1).Contains("秘密向量") || !Cell(2, 1).Contains("来自真实计算还是随机生成") ||
                !Cell(3, 1).Contains("(2:1)") || !formulas.Select(f => f.Source).SequenceEqual([@"\boldsymbol s", @"\boldsymbol b"]) ||
                formulas.Any(f => f.Error is not null || f.Asset is null) ||
                !Descendants<TextBox>(view).Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "why" && b.Text == "已有答案"))
                throw new Exception("Reopened table cells, their TeX or equation references did not render: " + Cell(3, 1));
            Capture(host, Path.Combine(output, "reopened-table.png"));
        }
        finally { host.Content = null; host.Close(); }
    }
}
