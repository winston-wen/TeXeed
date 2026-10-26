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
    private static async Task CheckAiPolishAsync(MainWindow window, string output)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        Button Button(Window dialog, string label) => Descendants<Button>(dialog).Single(b => Equals(b.Content, label));
        Window Dialog(string title) => Application.Current.Windows.Cast<Window>().Single(w => w.Title == title);
        void Click(Window dialog, string label) => Button(dialog, label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var project = (TeXeed.Core.TexProject)Field(window, "_project")!;
        var marker = Path.Combine(project.Root, "approval-marker.txt");
        var approvals = new HashSet<string>();
        var runner = window.CreateAiToolRunner(AiPermission.AskBeforeShell, approvals);
        string Args(string command) => JsonSerializer.Serialize(new { command, working_directory = "", timeout_seconds = 20, intent = "写入测试标记", impact = "只修改测试项目内的 approval-marker.txt。" });
        var command = "[IO.File]::WriteAllText('approval-marker.txt','yes')";
        foreach (var action in new[] { "提交", "其他意见", "拒绝", "允许一次", "允许类似" })
        {
            if (File.Exists(marker)) File.Delete(marker);
            var pending = runner("shell", Args(command), CancellationToken.None);
            await Task.Delay(70); var dialog = Dialog("允许执行此命令？"); dialog.UpdateLayout();
            var full = Descendants<Expander>(dialog).Single();
            Check(!full.IsExpanded && !File.Exists(marker), "Command was revealed/executed before consent");
            full.IsExpanded = true; dialog.UpdateLayout();
            var editor = Descendants<ICSharpCode.AvalonEdit.TextEditor>(dialog).Single();
            Check(editor.IsReadOnly && editor.Text == command && editor.FontFamily.Source.Contains("Consolas"), "Readonly editor lost full command or code font");
            Click(dialog, "其他意见"); dialog.UpdateLayout();
            var input = Descendants<TextBox>(dialog).Single(b => b.Name == "DecisionFeedback");
            input.Text = "先检查已有文件。\n不要写入新文件。";
            Check(input.IsVisible && input.AcceptsReturn, "Feedback box is not multiline/visible");
            if (action == "提交") Capture(dialog, Path.Combine(output, "approval-feedback.png"));
            Click(dialog, action); var result = await pending;
            if (action is "提交" or "其他意见") Check(result.Contains(input.Text) && !File.Exists(marker), "Feedback executed the command or lost text");
            else if (action == "拒绝") Check(result.Contains("拒绝") && !result.Contains(input.Text) && !File.Exists(marker), "Deny preserved feedback or executed shell");
            else Check(File.Exists(marker) && !result.Contains(input.Text), "Allow did not execute or retained discarded feedback");
        }
        File.Delete(marker);
        var remembered = await runner("shell", Args(command), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Check(File.Exists(marker) && remembered.Contains("退出码：0"), "Remembered grant prompted again");
        Check(AiShellConsent.Key("git log --oneline", project.Root) == AiShellConsent.Key("git log --all -5", project.Root), "Read-only Git family not remembered");
        foreach (var unsafeCommand in new[] { "git log --oneline; Remove-Item main.tex", "git log --output=main.tex", "git -c alias.log=bad log", "git log $(Get-Content secret)", "git log --ext-diff" })
            Check(AiShellConsent.Key(unsafeCommand, project.Root) != AiShellConsent.Key("git log --oneline", project.Root), "Unapproved command inherited grant");
        Check(AiShellConsent.Key(command, project.Root + "other") != AiShellConsent.Key(command, project.Root), "Grant crossed working directories");

        var query = JsonSerializer.Serialize(new { question = "缺少模板指定字体，如何处理？", options = new[] { new { label = "安装原字体（推荐）", description = "保留既有版式，需要下载字体。" }, new { label = "替换为现有字体", description = "减少下载，但版式可能变化。" } } });
        var readOnly = window.CreateAiToolRunner(AiPermission.ReadOnly);
        foreach (var custom in new[] { false, true })
        {
            var pending = readOnly("ask_user", query, CancellationToken.None);
            await Task.Delay(70); var dialog = Dialog("需要你的决定"); dialog.UpdateLayout();
            Capture(dialog, Path.Combine(output, "decision-options.png"));
            if (custom) { Click(dialog, "其他意见"); dialog.UpdateLayout(); Descendants<TextBox>(dialog).Single(b => b.Name == "DecisionFeedback").Text = "仅检查字体位置，暂不安装。"; Click(dialog, "提交"); }
            else Click(dialog, "安装原字体（推荐）");
            var result = await pending;
            Check(result.Contains(custom ? "仅检查字体位置" : "安装原字体"), "Decision selection did not reach model tool result");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var pending = readOnly("ask_user", query, cancel.Token); await Task.Delay(50); cancel.Cancel();
            bool canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !Application.Current.Windows.Cast<Window>().Any(w => w.Title == "需要你的决定"), "Stopped agent left question open");
        }
        Check(AiResponses.ShellSummary("读取历史脚本", "Shell 退出码：0\n标准输出：done") == "读取历史脚本 · 成功", "Shell success summary not human-readable");
        Check(AiResponses.ShellSummary("检查字体", "Shell 退出码：1\n标准错误：\nFont not found").Contains("Font not found"), "Shell failure reason lost");
        var buffer = new AiStreamBuffer(); buffer.Add(new("cmd", "activity", "检查字体 · 成功", Details: "命令\ngit log\n输出\nlog"));
        var segment = JsonSerializer.Deserialize<AiSegment>(JsonSerializer.Serialize(buffer.Snapshot().Single().Snapshot()))!;
        Check(segment.Details.Contains("git log"), "Expandable command details did not persist");
        var preview = (PdfPreview)window.FindName("Preview");
        var source = (LatexEditor)window.FindName("Editor");
        Check(preview.ContextMenu is null && !source.ContextMenu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "AI 圈点")), "Removed context menus remain");
        var marking = ((Panel)window.FindName("WorkspaceIconBar")).Children.OfType<Button>().Single(b => Equals(b.Tag, "ai-mark"));
        marking.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(preview.IsAiSelecting && System.Windows.Controls.Primitives.Selector.GetIsSelected(marking), "Sidebar marking button not synchronized");
        marking.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!preview.IsAiSelecting, "Sidebar cannot exit marking");

        new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }.Save();
        var handler = new CompactionHandler(); using var connection = new AiConnection(handler);
        var history = Enumerable.Range(0, 5).Select(i => new AiTurn("约束-" + i, new string('x', 1200))).ToArray();
        AiContextMemory? memory = null;
        await new AiResponses(connection).RunAsync("fixture", [new() { Question = "继续" }], history, [], _ => { }, _ => { }, CancellationToken.None,
            compactThreshold: 4000, compacted: m => memory = m);
        Check(memory?.TurnIds.Length == 3 && handler.Summaries == 1, "History compaction did not preserve the newest turns");
        Check(handler.LastInput.Contains("约束-4") && handler.LastInput.Contains("保留正文"), "Compaction lost summary or recent constraints");
        var oldSummaryCount = handler.Summaries;
        await new AiResponses(connection).RunAsync("fixture", [new() { Question = "继续" }], history, [], _ => { }, _ => { }, CancellationToken.None, memory: memory, compactThreshold: 4000);
        Check(handler.Summaries == oldSummaryCount, "Persisted summary was unnecessarily rebuilt");
        await new AiResponses(connection).RunAsync("fixture", [new() { Question = "继续" }], history.Skip(1).ToArray(), [], _ => { }, _ => { }, CancellationToken.None, memory: memory, compactThreshold: 4000);
        Check(handler.Summaries > oldSummaryCount, "Deleted history reused a stale summary");
        handler.FailSummary = true; bool failed = false, saved = false;
        try { await new AiResponses(connection).RunAsync("fixture", [], history, [], _ => { }, _ => { }, CancellationToken.None, compactThreshold: 4000, compacted: _ => saved = true); }
        catch (Exception) { failed = true; }
        Check(failed && !saved && history.All(t => t.Answer.Length == 1200), "Failed compaction dropped history or persisted incomplete state");
        var workingHandler = new CompactionHandler { Working = true };
        using var workingConnection = new AiConnection(workingHandler);
        var workingEvents = new AiStreamBuffer();
        await new AiResponses(workingConnection).RunAsync("fixture", [new() { Question = "继续保留当前图片", PdfBlocks = [new(1, 0, 0, 10, 10, "CURRENT_IMAGE_MARKER")] }], [], [], _ => { }, _ => { }, CancellationToken.None,
            toolRunner: (name, _, _) => Task.FromResult(name == "list_files" ? "项目 main.tex" : new string('x', 10000)), activity: workingEvents.Add, compactThreshold: 4000);
        Check(workingHandler.Summaries == 1 && workingEvents.Snapshot().Any(s => s.Text.StartsWith("已压缩工作上下文")), "Tool loop did not compact growing context");
        Check(!workingHandler.ImageInSummary && workingHandler.LastInput.Contains("CURRENT_IMAGE_MARKER"), "Compaction summarized base64 or lost the current image");
        Check(!workingHandler.LastInput.Contains("function_call"), "Compaction replayed orphaned tool calls/results");
        File.WriteAllText(Path.Combine(output, "polish-checks.txt"), $"PASS: {count} checks");
        Console.WriteLine($"AI polish: {count} checks passed.");
    }

    private sealed class CompactionHandler : HttpMessageHandler
    {
        internal int Summaries; internal string LastInput = ""; internal bool FailSummary, Working, ImageInSummary;
        private int normalRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var summary = body["instructions"]!.ToString().Contains("会话状态整理器");
            if (summary && FailSummary) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Temporary test failure") };
            if (summary) { Summaries++; ImageInSummary |= body["input"]!.ToJsonString().Contains("CURRENT_IMAGE_MARKER"); }
            else { normalRequests++; LastInput = body["input"]!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
            var message = !summary && Working && normalRequests == 1
                ? new JsonObject { ["type"] = "function_call", ["name"] = "read_source", ["call_id"] = "read", ["arguments"] = "{}" }
                : new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = summary ? "保留正文；已读取模板，尚未验证编译。" : "继续工作。" }) };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(message) } }.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
