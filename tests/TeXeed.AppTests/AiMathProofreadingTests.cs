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
    private static async Task CheckAiMathProofreadingAsync(MainWindow window, string output)
    {
        new AiCredentials { UseApiKey = true, ApiKey = "isolated-math-test-not-a-real-key" }.Save();
        const string bad = @"原结论保留。$$\frac{a}$$";
        const string good = @"原结论保留。$$\frac{a}{b}$$";
        foreach (var answers in new[] { new[] { bad, @"原结论保留。$$\sqrt{$$", good }, new[] { bad, "公式已删掉。", good } })
        {
            using var handler = new MathRepairHandler(answers);
            using var connection = new AiConnection(handler);
            var buffer = new AiStreamBuffer();
            await new AiResponses(connection).RunAsync("test-model", [], [], [], _ => { }, _ => { }, CancellationToken.None,
                activity: buffer.Add, proofread: AiMathProofreading.CheckAsync);
            var final = buffer.Snapshot().Where(s => s.Kind == "final").ToArray();
            if (handler.Requests != 3 || final.Length != 1 || final[0].Text != good)
                throw new Exception("Failed drafts became final, or repair stopped before a successful compilation");
            if (!buffer.Snapshot().Any(s => s.Kind == "activity" && s.Details.Contains(bad)))
                throw new Exception("Repair discarded the original draft and diagnostics");
        }
        using (var handler = new MathRepairHandler([bad]))
        using (var connection = new AiConnection(handler))
        {
            var buffer = new AiStreamBuffer();
            var stopped = false;
            try { await new AiResponses(connection).RunAsync("test-model", [], [], [], _ => { }, _ => { }, CancellationToken.None, activity: buffer.Add, proofread: AiMathProofreading.CheckAsync); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("5 次")) { stopped = true; }
            if (!stopped || handler.Requests != 6 || buffer.Snapshot().Any(s => s.Kind == "final"))
                throw new Exception("Unfixable math was marked successful or retried without a bound");
        }
        using (var handler = new MathRepairHandler([bad]))
        using (var connection = new AiConnection(handler))
        {
            var stopped = false;
            try { await new AiResponses(connection).RunAsync("test-model", [], [], [], _ => { }, _ => { }, CancellationToken.None,
                proofread: (_, _) => Task.FromResult(new AiMathCheck(1, "未找到 latex", false))); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("工具或宏包不可用")) { stopped = true; }
            if (!stopped || handler.Requests != 1) throw new Exception("Missing infrastructure caused unnecessary model retries");
        }
        using (var handler = new MathRepairHandler([bad]))
        using (var connection = new AiConnection(handler))
        using (var stop = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource();
            var task = new AiResponses(connection).RunAsync("test-model", [], [], [], _ => { }, _ => { }, stop.Token,
                proofread: async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(0); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Math validation ignored cancellation"); }
            catch (OperationCanceledException) { }
        }
        if ((await AiMathProofreading.CheckAsync(@"\[x", CancellationToken.None)).Error is null)
            throw new Exception("Unclosed math was accepted");
        var tick = ((char)96).ToString();
        if ((await AiMathProofreading.CheckAsync(tick + @"\[\unknownmacro{x}" + tick, CancellationToken.None)).Error is not null)
            throw new Exception("Literal code triggered proofreading");
        if ((await AiMathProofreading.CheckAsync(@"$$H(X)=256\text{ 位}.$$ ", CancellationToken.None)).Error is not null)
            throw new Exception("Chinese formula failed proofreading");

        using var panelHandler = new MathRepairHandler([bad, good]);
        using var panel = new AiPanel(new AiConnection(panelHandler));
        await panel.SetProjectAsync(Path.Combine(output, "math-proofread.tex"));
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = window, Content = panel, Width = 700, Height = 700 };
        host.Show();
        try
        {
            ((TextBox)panel.FindName("Composer")).Text = "解释公式，并确认排版正确";
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var turn = ((AiNotebook)Field(panel, "_book")!).Turns.Single();
            if (turn.Answer != good || turn.AnsweredAt is null || panelHandler.Requests != 2)
                throw new Exception("Actual AI panel did not validate before recording a completed answer");
        }
        finally { host.Close(); }
        Console.WriteLine("Math proofreading: real failed/corrected TeX, repeated correction, deletion prevention, draft retention, bounded failure, missing tools, cancellation, code exclusion, CJK and actual panel completion passed.");
    }
    private sealed class MathRepairHandler(string[] answers) : HttpMessageHandler
    {
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            if (Requests > 0 && (payload["tools"]!.AsArray().Count != 0 ||
                !payload["input"]!.ToJsonString().Contains("TeXeed") ||
                !payload["instructions"]!.ToString().Contains("仅校对")))
                throw new Exception("Repair request did not carry diagnostics or retained project-editing tools");
            var answer = answers[Math.Min(Requests++, answers.Length - 1)];
            var data = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = answer })
                + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
    }
}
