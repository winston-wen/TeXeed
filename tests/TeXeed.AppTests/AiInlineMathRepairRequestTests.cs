using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiInlineMathRepairRequestAsync(string output)
    {
        new AiCredentials { UseApiKey = true, ApiKey = "isolated-inline-math-repair-test" }.Save();
        using var handler = new InlineMathRepairHandler();
        using var connection = new AiConnection(handler);
        using var panel = new AiPanel(connection);
        var project = Path.Combine(output, "inline-math-repair-transport.tex");
        await panel.SetProjectAsync(project);
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("test-edit-model", "编辑测试模型"), new AiModel("other-model", "其他模型") };
        models.SelectedIndex = 1;
        var notebook = (AiNotebook)Field(panel, "_book")!;
        notebook.Active.SelectedModelId = "test-edit-model";
        notebook.Active.SelectedReasoningEffort = "";
        notebook.Turns.Add(new("private conversation question", "private conversation answer"));
        ((TextBox)panel.FindName("Composer")).Text = "private composer draft";
        var issues = new[]
        {
            new AiRevisionFormulaIssue("first", @"\frac{a}", @"LaTeX → SVG 失败：C:\Private\formula.tex:12: Missing } inserted."),
            new AiRevisionFormulaIssue("second", @"\sqrt{y", "Runaway argument.")
        };

        if ((await panel.RepairRevisionInlineMathAsync([], CancellationToken.None)).Count != 0 || handler.Requests != 0)
            throw new Exception("Repair called the model without failed formulae.");
        var repairs = await panel.RepairRevisionInlineMathAsync(issues, CancellationToken.None);
        if (repairs.Count != 2 || repairs["first"] != @"\frac{a}{1}" || repairs["second"] != @"\sqrt{y}" || handler.Requests != 1 ||
            notebook.Turns.Count != 1 || ((TextBox)panel.FindName("Composer")).Text != "private composer draft")
            throw new Exception("Inline repair mapped the wrong result, created a turn, or displaced the composer draft.");

        foreach (var malformed in new[]
        {
            "This is not JSON.",
            """{"repairs":[{"id":"first","tex":"x"},{"id":"first","tex":"y"}]}""",
            """{"repairs":[{"id":"first","tex":"x"},{"id":"unrelated","tex":"y"}]}""",
            """{"repairs":[{"id":"first","tex":"x"},{"id":"second","tex":""}]}""",
            """{"repairs":[{"id":"first","tex":"x"},{"id":"second","tex":"y"}],"story":"changed"}"""
        })
        {
            handler.Reply = malformed;
            try { await panel.RepairRevisionInlineMathAsync(issues, CancellationToken.None); throw new Exception("Invalid inline repair response was accepted."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("有效修正", StringComparison.Ordinal)) { }
        }

        handler.ReturnToolCall = true;
        try { await panel.RepairRevisionInlineMathAsync(issues, CancellationToken.None); throw new Exception("Unrequested tool call was accepted."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("工具调用", StringComparison.Ordinal)) { }
        handler.ReturnToolCall = false;
        handler.SavingRevision = true;
        var mathHome = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg")).FullName;
        using (var svg = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.inline-axis.capital.svg")!)
        using (var cache = File.Create(Path.Combine(mathHome, ChatTexRenderer.CacheFileName("A", false)))) svg.CopyTo(cache);
        var turn = notebook.Turns.Single() with { Answer = @"保留句首 \(x\) 和 \eqref{eq:test}。" };
        notebook.Turns[0] = turn;
        var draft = panel.BeginRevision(turn);
        var invalidRow = draft.Rows.Single() with { Text = @"保留句首 \(\frca{a}{b}\) 和 \eqref{eq:test}。" };
        turn.UserRevisionDraft = draft with { Rows = [invalidRow] };
        await panel.SaveAsync();

        var callsBeforeSave = handler.Requests;
        handler.Reply = """{"repairs":[{"id":"1","tex":"\\frca{a}{b}"}]}""";
        try
        {
            await panel.CommitRevisionAsync(turn.Id, draft.Version, [invalidRow], "user");
            throw new Exception("The original invalid formula was accepted after AI repair.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("仍无法编译", StringComparison.Ordinal)) { }
        var failedSave = AiStorage.Load(project).Turns.Single();
        if (failedSave.Answer != turn.Answer || failedSave.UserRevisionDraft?.Rows.Single().Text != invalidRow.Text ||
            failedSave.RevisionHistory is { Count: > 0 })
            throw new Exception("A failed repair displaced the saved turn, draft, or edit history.");

        handler.Reply = """{"repairs":[{"id":"1","tex":"A"}]}""";
        await panel.CommitRevisionAsync(turn.Id, draft.Version, [invalidRow], "user");
        var saved = AiStorage.Load(project).Turns.Single();
        if (handler.Requests != callsBeforeSave + 2 || saved.Answer != @"保留句首 \(A\) 和 \eqref{eq:test}。" ||
            saved.UserRevisionDraft is not null || saved.RevisionHistory?.Last().Author != "user")
            throw new Exception("A repair was not checked and saved via the revision commit in the user's edit history.");
        Console.WriteLine("PASS: inline formula repair sent only failed TeX and diagnostics, rejected invalid responses and tools, and the revision commit preserved its draft until corrected TeX passed.");
    }

    private sealed class InlineMathRepairHandler : HttpMessageHandler
    {
        internal int Requests;
        internal bool ReturnToolCall;
        internal bool SavingRevision;
        internal string Reply = """{"repairs":[{"id":"second","tex":"\\sqrt{y}"},{"id":"first","tex":"\\frac{a}{1}"}]}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var input = payload["input"]!.AsArray();
            var content = JsonNode.Parse(input.Single()!["content"]!.GetValue<string>())!.AsArray();
            if (payload["model"]?.ToString() != "test-edit-model" || payload["store"]?.GetValue<bool>() != false ||
                payload["tools"]!.AsArray().Count != 0 || payload["text"]?["format"]?["type"]?.ToString() != "json_schema" ||
                payload["text"]?["format"]?["strict"]?.GetValue<bool>() != true ||
                payload["instructions"]?.ToString().Contains("块式 Markdown", StringComparison.Ordinal) != false ||
                content.Any(item => item!.AsObject().Count != 3))
                throw new Exception("Unexpected inline repair payload or inclusion of unrelated data.");
            if (SavingRevision)
            {
                if (content.Count != 1 || content[0]!["id"]!.ToString() != "1" || content[0]!["tex"]!.ToString() != @"\frca{a}{b}" ||
                    !content[0]!["error"]!.ToString().Contains(@"\frca", StringComparison.Ordinal))
                    throw new Exception("The save request did not isolate the one invalid formula.");
            }
            else if (content.Count != 2 || content[0]!["id"]!.ToString() != "first" || content[0]!["tex"]!.ToString() != @"\frac{a}" ||
                content[0]!["error"]!.ToString() != "formula.tex:12: Missing } inserted." ||
                content[1]!["id"]!.ToString() != "second" || content[1]!["tex"]!.ToString() != @"\sqrt{y" ||
                content[1]!["error"]!.ToString() != "Runaway argument.")
                throw new Exception("The formula repair request did not submit exactly the two supplied failures.");
            var output = ReturnToolCall
                ? """[{"type":"function_call","name":"compile_project","arguments":"{}","call_id":"unexpected"}]"""
                : "[]";
            var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = Reply });
            var data = "data: " + delta + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":" + output + "}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
