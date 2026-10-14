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
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiPermissionsAsync(MainWindow window, string output)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "shell project 中文"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.tex"), "\\documentclass{article}\n\\begin{document}\nOriginal.\n\\end{document}\n");
        File.WriteAllText(Path.Combine(folder, "install-fonts.ps1"), "# 历史安装脚本\nWrite-Output 'FONT_HISTORY_MARKER'\n");
        var setup = await AiShell.ExecuteAsync(folder, "git init -q; git -c user.name=Fixture -c user.email=fixture@example.invalid add -- main.tex install-fonts.ps1; git -c user.name=Fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit -qm 'add font installer'; git rm -q -- install-fonts.ps1; git -c user.name=Fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit -qm 'remove installer'", "", 30, CancellationToken.None);
        Check(setup.Contains("退出码：0"), "Could not create Git history fixture: " + setup);
        await CallAsync(window, "OpenAsync", Path.Combine(folder, "main.tex")); Invoke(window, "OpenAiTab");
        var runner = window.CreateAiToolRunner(AiPermission.FullAccess);
        string Shell(string command, int timeout = 20) => JsonSerializer.Serialize(new { command, working_directory = "", timeout_seconds = timeout });
        var result = await runner("shell", Shell("git log --all --oneline --name-status; git show HEAD~1:install-fonts.ps1"), CancellationToken.None);
        Check(result.Contains("FONT_HISTORY_MARKER") && result.Contains("remove installer") && result.Contains("历史安装脚本"), "Real shell did not retrieve the deleted historical script with Unicode");
        Check(!File.Exists(Path.Combine(folder, "install-fonts.ps1")), "Reading history restored a deleted file unexpectedly");
        var readOnly = window.CreateAiToolRunner(AiPermission.ReadOnly);
        foreach (var name in new[] { "shell", "replace_source", "create_source", "save_project", "compile_project" })
        {
            var rejected = false;
            try { await readOnly(name, "{}", CancellationToken.None); } catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Read-only session allowed " + name);
        }
        result = await readOnly("read_source", "{\"file\":\"main.tex\",\"start_line\":1,\"line_count\":20}", CancellationToken.None);
        Check(result.Contains("Original."), "Read-only session lost read access");
        var tools = AiResponses.WorkspaceTools(AiPermission.ReadOnly).ToJsonString();
        Check(!tools.Contains("\"name\":\"shell\"") && !tools.Contains("\"name\":\"compile_project\""), "Read-only tools were still advertised");
        result = await runner("shell", Shell("[IO.File]::WriteAllText((Join-Path (Get-Location) 'main.tex'), ([IO.File]::ReadAllText('main.tex').Replace('Original.', 'Changed by shell.')), (New-Object Text.UTF8Encoding($false)))"), CancellationToken.None);
        Check(result.Contains("退出码：0"), "Shell source edit failed");
        result = await runner("read_source", "{\"file\":\"main.tex\",\"start_line\":1,\"line_count\":20}", CancellationToken.None);
        Check(result.Contains("Changed by shell."), "read_source returned stale buffer after shell edit");
        Check(((TabWorkspace)Field(window, "_workspaceTabs")!).FocusedTab == TabWorkspace.AiKey, "Shell refresh stole chat focus");
        result = await AiShell.ExecuteAsync(folder, "[Console]::Error.WriteLine('expected error'); exit 7", "", 20, CancellationToken.None);
        Check(result.Contains("退出码：7") && result.Contains("expected error"), "Exit code/stderr were not preserved");
        result = await AiShell.ExecuteAsync(folder, "[Console]::Write(('x' * 50000))", "", 20, CancellationToken.None);
        Check(result.Contains("输出截断") && result.Length < 25000, "Shell output was unbounded or silently truncated");
        result = await AiShell.ExecuteAsync(folder, "Start-Sleep -Seconds 10; [IO.File]::WriteAllText('too-late.txt','bad')", "", 1, CancellationToken.None);
        Check(result.Contains("超时") && !File.Exists(Path.Combine(folder, "too-late.txt")), "Timed-out shell continued to mutate files");
        using (var cancel = new CancellationTokenSource(300))
        {
            var canceled = false;
            try { await AiShell.ExecuteAsync(folder, "Start-Sleep -Seconds 10", "", 20, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Shell ignored cancellation");
        }
        var asking = window.CreateAiToolRunner(AiPermission.AskBeforeShell);
        var pending = asking("shell", Shell("[IO.File]::WriteAllText('approved.txt','yes')"), CancellationToken.None);
        await Task.Delay(100);
        var approval = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "允许执行此命令？");
        var approvalButtons = Descendants<Button>(approval).Where(b => b.Tag is string).ToArray();
        Check(!pending.IsCompleted && !File.Exists(Path.Combine(folder, "approved.txt")), "Ask mode executed before approval");
        approvalButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((await pending).Contains("拒绝") && !File.Exists(Path.Combine(folder, "approved.txt")), "Denied shell was executed");
        pending = asking("shell", Shell("[IO.File]::WriteAllText('approved.txt','yes')"), CancellationToken.None);
        await Task.Delay(100);
        approval = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "允许执行此命令？");
        Descendants<Button>(approval).Single(b => Equals(b.Tag, "once")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((await pending).Contains("退出码：0") && File.ReadAllText(Path.Combine(folder, "approved.txt")) == "yes", "Approved command did not execute");

        var sent = new DateTimeOffset(2026, 10, 14, 23, 59, 59, TimeSpan.FromHours(8));
        Check(AiTimestamp.Full(sent) == "2026-10-14 (+8) 23:59:59", "Full timestamp format incorrect");
        Check(AiTimestamp.Full(sent.ToOffset(TimeSpan.FromHours(5.5))).Contains("(+5.5)"), "Half-hour timezone was rounded");
        Check(AiTimestamp.Full(sent.ToOffset(TimeSpan.FromHours(-3.5))).Contains("(-3.5)"), "Negative timezone was lost");
        Check(AiTimestamp.Activity(sent.AddSeconds(2), sent) == "+1d 00:00:01", "Midnight day marker incorrect");
        Check(AiTimestamp.Activity(sent.AddDays(2), sent).StartsWith("+2d"), "Multi-day activity marker incorrect");
        Check(AiTimestamp.Activity(sent.ToOffset(TimeSpan.FromHours(9)), sent).Contains("(+9)"), "Changed timezone omitted full timestamp");
        var usage = AiContextUsage.FromResponse("fixture", JsonNode.Parse("{\"usage\":{\"input_tokens\":100,\"output_tokens\":5}}"), 1000)!;
        Check(usage.Display == "10.5%" && (usage with { ContextWindow = null }).Display == "—", "Context usage was not grounded in measured tokens and known window");
        using var catalog = JsonDocument.Parse("{\"slug\":\"test\",\"display_name\":\"Test\",\"supported_reasoning_levels\":[\"low\",\"high\"],\"default_reasoning_level\":\"high\",\"context_window\":1000}");
        var model = AiModel.FromCatalog(catalog.RootElement);
        Check(model.ContextWindow == 1000 && AiEffort.Choices(model)[0].Name == "默认·高", "Model catalog default effort/context not surfaced");
        Check(AiEffort.Choices(new AiModel("unknown", "Unknown"))[0].Name == "服务端默认", "Unknown effort default was falsely specified");
        var notebook = new AiNotebook(); notebook.Active.Permission = AiPermission.ReadOnly; notebook.Active.ContextUsage = usage;
        notebook.Turns.Add(new("问题", "回答") { SentAt = sent, AnsweredAt = sent.AddSeconds(10), Segments = [new() { Id = "request", Kind = "activity", Text = "已发送" }, new() { Kind = "final", Text = "回答", CreatedAt = sent.AddSeconds(10) }] });
        AiStorage.Save(Path.Combine(folder, "notebook.tex"), notebook);
        var loaded = AiStorage.Load(Path.Combine(folder, "notebook.tex"));
        Check(loaded.Active.Permission == AiPermission.ReadOnly && loaded.Active.ContextUsage?.Display == "10.5%", "Conversation permission/usage did not persist");
        Check(loaded.Turns[0].DisplaySegments.Count == 1 && loaded.Turns[0].DisplayTime == AiTimestamp.Full(sent) && loaded.Turns[0].AnswerTime.Contains("答复时间："), "Transcript timestamps/request migration incorrect");
        Check(new AiNotebook().Active.Permission == AiPermission.FullAccess, "New conversations did not default to full access");

        new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }.Save();
        var handler = new ShellProtocolHandler();
        using var connection = new AiConnection(handler);
        AiContextUsage? actualUsage = null;
        var stream = new AiStreamBuffer();
        await new AiResponses(connection).RunAsync("fixture", [new() { Question = "Git 历史里有安装脚本，找出它。" }], [], [], _ => { }, _ => { }, CancellationToken.None,
            toolRunner: runner, activity: stream.Add, usage: measured => actualUsage = measured, contextWindow: 1000);
        Check(handler.Requests == 2 && handler.ReadHistory, "Responses loop failed to execute shell and return its result");
        Check(actualUsage?.Display == "10.5%" && stream.Snapshot().Any(s => s.Text.Contains("FONT_HISTORY_MARKER") && s.Kind == "final"), "Usage/final answer did not reach the stream");

        var panel = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Add(new("项目 Git 历史里曾经有安装脚本，请帮我查找。", "已从 Git 历史中找到 `install-fonts.ps1`，核对了字体名称与安装位置。")
        {
            SentAt = sent, AnsweredAt = sent.AddSeconds(30), Segments =
            [new() { Kind = "activity", Text = "已查看项目目录", CreatedAt = sent }, new() { Kind = "activity", Text = "读取历史安装脚本 · 成功", Details = "命令\ngit show HEAD~1:install-fonts.ps1\n\n命令输出\n# 历史安装脚本\nWrite-Output FONT_HISTORY_MARKER", CreatedAt = sent.AddSeconds(10) }, new() { Kind = "final", Text = "已找到历史安装脚本，保留当前源码。", CreatedAt = sent.AddSeconds(30) }]
        });
        Invoke(panel, "RefreshTranscript");
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("gpt-6.1-sol", "GPT-6.1-Sol") { ReasoningEfforts = ["low", "medium", "high"], ContextWindow = 1000, DefaultReasoningEffort = "medium" } };
        models.SelectedIndex = 0; await panel.Settings.SelectEffortAsync("high");
        book.Active.ContextUsage = usage with { Model = "gpt-6.1-sol" }; Invoke(panel, "UpdateContextUsage");
        window.UpdateLayout(); Capture(window, Path.Combine(output, "ai-permissions-wide.png"));
        var expansion = Descendants<Expander>(panel).First(e => e.DataContext is AiSegment segment && segment.Details.Contains("git show"));
        expansion.IsExpanded = true; window.UpdateLayout(); Capture(window, Path.Combine(output, "shell-expanded.png"));
        Check(Descendants<TextBox>(expansion).Any(t => t.Text.Contains("git show") && t.Text.Contains("FONT_HISTORY_MARKER") && t.IsReadOnly), "Expanded shell details not visible");
        Check(((Button)panel.FindName("ChatSettingsButton")).IsVisible, "Header settings gear missing");
        Check(((ComboBox)panel.FindName("ModelPicker")).ActualWidth <= 190 && ((ComboBox)panel.FindName("EffortPicker")).ActualWidth < 76, "Model/effort controls remain oversized");
        File.WriteAllText(Path.Combine(output, "permissions-checks.txt"), $"PASS: {checks} checks");
        Console.WriteLine($"AI permissions, shell, history, timestamps, usage and UI: {checks} checks passed.");
    }

    private sealed class ShellProtocolHandler : HttpMessageHandler
    {
        internal int Requests;
        internal bool ReadHistory;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            if (!body["tools"]!.ToJsonString().Contains("\"name\":\"shell\"") || !body["instructions"]!.ToString().Contains("git log")) throw new Exception("Shell tools/prompt missing from request");
            var output = new JsonArray();
            if (Requests == 1) output.Add(new JsonObject { ["type"] = "function_call", ["name"] = "shell", ["call_id"] = "history", ["arguments"] = JsonSerializer.Serialize(new { command = "git show HEAD~1:install-fonts.ps1", working_directory = "", timeout_seconds = 20 }) });
            else
            {
                ReadHistory = body["input"]!.ToJsonString().Contains("FONT_HISTORY_MARKER");
                output.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "已找到历史脚本：FONT_HISTORY_MARKER。" }) });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = output, ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 5 } } }.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
