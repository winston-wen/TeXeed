using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiAgentAsync(MainWindow window, string root, string output)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var folder = Path.Combine(output, "agent-project"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        var original = "\\documentclass{article}\r\n\\begin{document}\r\nKeep this sentence. \\badcommand\r\n\\end{document}\r\n";
        File.WriteAllText(main, original);
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window,
            new AppSettings { Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"), LivePreview = false, FollowMode = PreviewFollowMode.None });
        await CallAsync(window, "OpenAsync", main); Invoke(window, "OpenAiTab");
        new AiCredentials { ApiKey = "test-key-never-send", UseApiKey = true }.Save();
        var handler = new AiRepairHandler();
        using var panel = new AiPanel(new AiConnection(handler)) { CreateToolRunner = window.CreateAiToolRunner };
        var notebookProject = Path.Combine(output, "agent-chat.tex");
        await panel.SetProjectAsync(notebookProject);
        var modelBox = (ComboBox)panel.Settings.FindName("Models");
        modelBox.ItemsSource = new[] { new AiModel("test-model", "TeX 工作助手") }; modelBox.SelectedIndex = 0;
        var host = new Window { Content = panel, Width = 780, Height = 960, Owner = window, Title = "TeXeed · 修改与编译" };
        host.Show();
        try
        {
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "帮我修到能编译，保留原文。";
            var pending = panel.SubmitAsync();
            Check(composer.Text.Length == 0 && !composer.IsReadOnly, "Composer did not clear immediately");
            host.UpdateLayout();
            Check(((Button)panel.FindName("SubmitButton")).Visibility == Visibility.Collapsed && ((Button)panel.FindName("StopButton")).IsVisible, "Send arrow is showing behind stop icon");
            var add = (Button)panel.FindName("AddButton"); var stop = (Button)panel.FindName("StopButton");
            Check(Math.Abs(add.TranslatePoint(new Point(0, add.ActualHeight / 2), panel).Y - stop.TranslatePoint(new Point(0, stop.ActualHeight / 2), panel).Y) < 1, "Composer icons are not centered on the same row");
            composer.Text = "下一步再检查参考文献。";
            Capture(host, Path.Combine(output, "agent-working.png"));
            await pending.WaitAsync(TimeSpan.FromSeconds(100));
            var book = (AiNotebook)Field(panel, "_book")!;
            Check(handler.Requests == 4 && handler.SawEditTools && handler.SawFailure && handler.SawSuccess, "Agent did not complete read/failure/edit/recompile/final loop");
            Check(File.ReadAllText(main) == original.Replace("\\badcommand", "\\textbf{Repaired}"), "Agent failed to save the exact source edit or changed unrelated text/newlines");
            Check(File.Exists(Path.Combine(folder, ".texeed", "preview", "main.pdf")), "Real repaired project has no preview PDF");
            Check(((TabWorkspace)Field(window, "_workspaceTabs")!).FocusedTab == TabWorkspace.AiKey, "AI save/compile stole the chat tab");
            Check(composer.Text == "下一步再检查参考文献。", "Final response cleared the next draft");
            var turn = book.Turns.Single();
            Check(turn.Answer == AiRepairHandler.Final && turn.Segments.Last().Kind == "final", "Final answer was concatenated with intermediate commentary");
            var kinds = turn.Segments.Select(s => s.Kind).ToArray();
            Check(kinds.Count(k => k == "text") == 2 && kinds.Count(k => k == "activity") == 5, "Intermediate prose and tool operations were not kept as chronological segments");
            Check(turn.Segments.Any(s => s.Text.Contains("编译失败")) && turn.Segments.Any(s => s.Text.Contains("编译成功")), "The output stream lost compile results");
            Check(turn.Segments.Where(s => s.Kind == "activity").All(s => !s.Text.Contains("SHA256") && !s.Text.Contains("备份 ") && !s.Text.Contains("等待模型")), "Completed activity contains protocol details or a stale waiting state");
            Check(turn.Segments.Zip(turn.Segments.Skip(1)).All(p => p.First.CreatedAt <= p.Second.CreatedAt), "Activity timestamps are not chronological");
            Check(AiStorage.Load(notebookProject).Turns.Single().Segments.Count == turn.Segments.Count, "Transcript segments were lost on reload");
            Check(Directory.EnumerateFiles(Path.Combine(AiStorage.Home, "backups"), "*-main.tex", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == original), "Original buffer backup was not persisted");
            ((ScrollViewer)panel.FindName("MessagesScroll")).ScrollToEnd(); host.UpdateLayout();
            Capture(host, Path.Combine(output, "agent-complete.png"));
            host.Width = 390; host.UpdateLayout(); Capture(host, Path.Combine(output, "agent-narrow.png"));
        }
        finally { host.Close(); }

        var editor = (LatexEditor)window.FindName("LeftEditor");
        Invoke(window, "ActivateFile", main);
        editor.Undo(); Check(editor.Text == original, "AI edit cannot be undone after compilation");
        editor.Redo(); Check(editor.Text.Contains("\\textbf{Repaired}"), "AI edit cannot be redone");
        var runner = window.CreateAiToolRunner();
        var read = await runner("read_source", "{\"file\":\"main.tex\",\"start_line\":1,\"line_count\":100}", CancellationToken.None);
        var hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        editor.Document.Insert(0, "% user change\r\n");
        var beforeConflict = editor.Text;
        var rejected = false;
        try { await runner("replace_source", JsonSerializer.Serialize(new { file = "main.tex", expected_sha256 = hash, old_text = "Keep this sentence.", new_text = "Overwritten" }), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && editor.Text == beforeConflict, "Agent overwrote an edit made after reading");
        foreach (var path in new[] { "../outside.tex", ".git/config", "C:/outside.tex", "folder/../outside.tex", "NUL.tex" })
        {
            rejected = false; try { await runner("create_source", JsonSerializer.Serialize(new { file = path, content = "bad" }), CancellationToken.None); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "Unsafe creation path accepted: " + path);
        }
        var newFile = "sections/new.tex";
        await runner("create_source", JsonSerializer.Serialize(new { file = newFile, content = "New section." }), CancellationToken.None);
        rejected = false;
        try { await runner("create_source", JsonSerializer.Serialize(new { file = newFile, content = "overwrite" }), CancellationToken.None); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(folder, newFile)) == "New section.", "create_source overwrote an existing file");
        await runner("save_project", "{}", CancellationToken.None);
        Check((await runner("list_files", "{}", CancellationToken.None)).Contains(newFile.Replace('/', Path.DirectorySeparatorChar)), "Saving dropped an unreferenced newly created source");
        File.AppendAllText(main, "% external change\r\n"); editor.Document.Insert(0, "% unsaved\r\n");
        rejected = false;
        try { await runner("save_project", "{}", CancellationToken.None); } catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(main).Contains("% external change"), "AI save overwrote external modifications");
        // Restore only this fixture's expected baseline to finish the regression without a conflict dialog.
        var project = (TexProject)Field(window, "_project")!;
        File.WriteAllText(main, project.Files.First(f => f.Path == main).Content);
        await runner("save_project", "{}", CancellationToken.None);

        Invoke(window, "OpenAiTab");
        File.AppendAllText(main, "% accepted disk change\r\n");
        await runner("save_project", "{}", CancellationToken.None);
        read = await runner("read_source", "{\"file\":\"main.tex\",\"start_line\":1,\"line_count\":100}", CancellationToken.None);
        hash = Regex.Match(read, "SHA256=([A-F0-9]+)").Groups[1].Value;
        await runner("replace_source", JsonSerializer.Serialize(new { file = "main.tex", expected_sha256 = hash, old_text = "Keep this sentence.", new_text = "Kept this sentence." }), CancellationToken.None);
        await runner("save_project", "{}", CancellationToken.None);
        Check(File.ReadAllText(main).Contains("Kept this sentence.") && File.ReadAllText(main).Contains("% accepted disk change"), "Inactive editor session rejected an accepted disk reload or lost external content");

        var compiler = (CompileService)Field(window, "_compiler")!;
        var gate = (SemaphoreSlim)typeof(CompileService).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(compiler)!;
        await gate.WaitAsync();
        using var cancel = new CancellationTokenSource();
        try
        {
            var compiling = runner("compile_project", "{}", cancel.Token);
            await WaitUntilAsync(() => Field(window, "_compileTask") is Task, "AI compiler never started");
            cancel.Cancel(); rejected = false;
            try { await compiling.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && !(bool)Field(window, "_building")!, "Stopping AI did not cancel its compiler");
        }
        finally { gate.Release(); }
        var next = Path.Combine(output, "next-project.tex"); File.WriteAllText(next, "\\documentclass{article}\n\\begin{document}Next\\end{document}");
        await CallAsync(window, "OpenAsync", next);
        rejected = false;
        try { await runner("create_source", "{\"file\":\"late.tex\",\"content\":\"late\"}", CancellationToken.None); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(folder, "late.tex")), "Old agent session survived project switch");
        Console.WriteLine($"AI agent: {checks} checks passed, including real failing TeX -> exact edit -> successful compile, backup/undo, draft preservation, chronological persistence, conflicts, path confinement and cancellation.");
    }

    private sealed class AiRepairHandler : HttpMessageHandler
    {
        internal const string Final = "已修复 `main.tex` 的未定义命令，保留了原文。\n\n**编译成功**，PDF 预览已更新。";
        internal int Requests;
        internal bool SawEditTools, SawFailure, SawSuccess;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/responses") throw new Exception("Unexpected network endpoint");
            Requests++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            SawEditTools = body["tools"]!.ToJsonString().Contains("replace_source") && body["instructions"]!.ToString().Contains("直接使用工具修改");
            var results = body["input"]!.AsArray().Where(n => n?["type"]?.ToString() == "function_call_output").Select(n => n!["output"]!.ToString()).ToArray();
            SawFailure |= results.Any(r => r.StartsWith("编译失败")); SawSuccess |= results.Any(r => r.StartsWith("编译成功"));
            JsonObject Tool(string name, object args) => new() { ["type"] = "function_call", ["name"] = name, ["call_id"] = name + Requests, ["arguments"] = JsonSerializer.Serialize(args) };
            JsonObject Message(string text) => new() { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
            var output = new JsonArray();
            if (Requests == 1)
            {
                output.Add(Message("我先读取主文件，编译确认错误位置。"));
                output.Add(Tool("read_source", new { file = "main.tex", start_line = 1, line_count = 100 }));
                output.Add(Tool("compile_project", new { }));
            }
            else if (Requests == 2)
            {
                var hash = Regex.Match(results.First(r => r.Contains("SHA256=")), "SHA256=([A-F0-9]+)").Groups[1].Value;
                output.Add(Message("错误来自一个未定义命令。我会直接替换它，保留其余内容。"));
                output.Add(Tool("replace_source", new { file = "main.tex", expected_sha256 = hash, old_text = "\\badcommand", new_text = "\\textbf{Repaired}" }));
            }
            else if (Requests == 3) output.Add(Tool("compile_project", new { }));
            else output.Add(Message(Final));
            await Task.Delay(100, token);
            var events = "data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = output } }.ToJsonString() + "\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
    }
}
