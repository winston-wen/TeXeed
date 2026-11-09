using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static int RunAiMentorIntegration(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var result = 1;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckMentorIntegrationAsync(output); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run();
        return result;
    }

    private static async Task CheckMentorIntegrationAsync(string output)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new MentorIntegrationHandler();
        using var connection = new AiConnection(handler);
        using var panel = new AiPanel(connection);
        var host = new Window { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, Width = 1000, Height = 800, ShowInTaskbar = false, Content = panel };
        host.Show();
        try
        {
            var project = Path.Combine(output, "mentor-integration-" + Guid.NewGuid().ToString("N") + ".tex");
            await panel.SetProjectAsync(project);
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture-model", "测试模型") }; models.SelectedIndex = 0;
            var browserView = (AiDocumentView)panel.FindName("SharedDocumentView");
            var browser = Field(browserView, "_browser")!;
            object? Core() => browser.GetType().GetProperty("CoreWebView2")!.GetValue(browser);
            await EventuallyAsync(() => Task.FromResult(Core() is not null), "Actual WebView2 did not initialize: " + (browserView.Content as TextBlock)?.Text);
            async Task<string> Script(string script) => await (Task<string>)Core()!.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(Core(), [script])!;
            await EventuallyAsync(async () => await Script("document.readyState") == "\"complete\"", "WebView2 HTML failed to load");
            var composer = (TextBox)panel.FindName("Composer");
            var book = (AiNotebook)Field(panel, "_book")!;
            var nativeSource = new AiSessionSourceView(book.Active.SharedDocument, AiDocumentRepository.GetDirectory(project, book.Active.Id), new AppSettings());
            panel.SharedDocumentChanged += (_, _) => nativeSource.RefreshFromDocument();
            composer.Text = "为什么成立？";
            var firstGate = handler.Enqueue();
            var first = panel.SubmitAsync();
            await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var submitted = (ScrollViewer)panel.FindName("SharedSubmittedMessage");
            var submittedText = (TextBlock)panel.FindName("SharedSubmittedText");
            Check(submitted.Visibility == Visibility.Visible && submittedText.Text == "为什么成立？", "Pending user note was not visible while the request is in progress");
            await EventuallyAsync(async () => (await Script("(()=>{let e=document.querySelector('#mentor-initial-question');return e&&!e.hidden?e.textContent:''})()")).Contains("为什么成立"), "Initial question was not displayed while response is pending");
            firstGate.Answer.TrySetResult("\\section{结果}\n\n这个结论需要条件。\n\n$2^3=8$。");
            await first.WaitAsync(TimeSpan.FromSeconds(20));
            Check(submitted.Visibility == Visibility.Collapsed && submittedText.Text.Length == 0, "Completed user note stayed visible as a transcript");
            Check(book.Turns.Count == 1 && !book.Turns[0].IsIncomplete, "Initial simulated AI request failed");
            Check(book.Active.SharedDocument.Source.Contains("需要条件") && book.Active.SharedDocument.InitialSource == book.Active.SharedDocument.Source, "Initial source was not preserved");
            Check(nativeSource.Editor.Text == book.Active.SharedDocument.Source, "Simulated model result did not reach the native source view");
            Check(book.Active.SharedDocument.AuthorSpans.Single().Author == "测试模型", "Model author name was lost");
            await EventuallyAsync(async () => (await Script("document.querySelector('#mentor-document')?.textContent || ''")).Contains("需要条件"), "Native WebView2 did not render source");
            var source = book.Active.SharedDocument.Source;
            var start = source.IndexOf("需要条件", StringComparison.Ordinal);
            var bridgeComment = JsonSerializer.Serialize(new { type = "comment", binding = "1", revision = book.Active.SharedDocument.Revision, previous = (object?)null, comment = new { id = "qa-comment", start, end = start + 4, quote = "需要条件", text = "具体是什么条件？", replies = Array.Empty<string>() } });
            await Script("chrome.webview.postMessage(" + bridgeComment + ")");
            await EventuallyAsync(() => Task.FromResult(book.Active.SharedDocument.Comments.Any(c => c.Id == "qa-comment")), "Native WebView2 comment bridge did not persist user comment");
            Check(book.Active.SharedDocument.Comments.Single(c => c.Id == "qa-comment").Author == "用户", "User comment has wrong author");
            book.Active.SharedDocument.Comments.Add(new AiDocumentComment { Id = "old-comment", Quote = "需要条件", Text = "更早的讨论也要看", Author = "用户" });
            book.Active.SharedDocument.Comments.Add(new AiDocumentComment { Id = "unassigned-comment", Quote = "需要条件", Text = "没有轮次的讨论也要看", Author = "用户", Replies = [new AiDocumentReply { Author = "测试模型", Role = "ai", Text = "之前的回复" }] });
            var other = new AiConversation { Title = "另一个会话" };
            other.SharedDocument.Comments.Add(new AiDocumentComment { Id = "other-comment", Text = "不可带入其他会话的评论", Author = "用户" });
            book.Conversations.Add(other);
            composer.Text = "请补充条件";
            var failure = handler.Enqueue(); failure.Answer.TrySetResult("这不是 JSON");
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var failed = book.Turns.Last();
            Check(failed.IsIncomplete && book.Active.SharedDocument.Source == source, "Malformed model response changed the document or marked success");
            Check(nativeSource.Editor.Text == source, "Failed model response changed the native source view");
            var retryButton = (Button)panel.FindName("SharedRetryButton");
            Check(retryButton.IsVisible && retryButton.IsEnabled, "The last failed request has no visible retry button");
            using (var payload = JsonDocument.Parse(handler.Requests.Last()))
            {
                var textParts = payload.RootElement.GetProperty("input").EnumerateArray().Where(e => e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array).SelectMany(e => e.GetProperty("content").EnumerateArray()).Where(e => e.TryGetProperty("text", out _)).Select(e => e.GetProperty("text").GetString() ?? "");
                var joined = string.Join("\n", textParts);
                Check(joined.Contains("具体是什么条件") && joined.Contains("更早的讨论也要看") && joined.Contains("没有轮次的讨论也要看") && joined.Contains("之前的回复"), "Current document discussions were absent from the request snapshot");
                Check(!joined.Contains("不可带入其他会话的评论"), "A different conversation leaked into the request snapshot");
            }
            var frozenRequest = failed.MentorRequest!;
            Check(frozenRequest.References.Count == 3, "The request omitted current document comments");
            book.Active.SharedDocument.Comments.Single(c => c.Id == "qa-comment").Text = "失败后修改的评论";
            composer.Text = "下一条草稿";
            var retry = handler.Enqueue();
            retry.Answer.TrySetResult(JsonSerializer.Serialize(new { initial_latex = (string?)null, edits = new[] { new { old_text = "这个结论需要条件。", new_text = "这个结论需要正整数条件。", reason = "补充适用范围" } }, comments = Array.Empty<object>(), message = "" }));
            retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await retry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await EventuallyAsync(() => Task.FromResult(!panel.HasActiveOperation), "Retry button did not finish");
            using (var payload = JsonDocument.Parse(handler.Requests.Last()))
            {
                var parts = payload.RootElement.GetProperty("input").EnumerateArray().Where(e => e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array).SelectMany(e => e.GetProperty("content").EnumerateArray()).Where(e => e.TryGetProperty("text", out _)).Select(e => e.GetProperty("text").GetString() ?? "");
                var joined = string.Join("\n", parts);
                Check(joined.Contains("具体是什么条件") && !joined.Contains("失败后修改的评论"), "Retry did not use the original frozen comment snapshot");
            }
            Check(!book.Turns.Last().IsIncomplete && book.Active.SharedDocument.Source.Contains("正整数条件"), "Retry did not apply valid frozen response");
            Check(composer.Text == "下一条草稿", "Retry consumed the next draft");
            Check(book.Active.SharedDocument.Comments.Any(c => c.Text == "补充适用范围" && c.Author == "测试模型"), "Substantive modification lacked attributed explanation");
            var preConflict = book.Active.SharedDocument.Source;
            composer.Text = "再次检查";
            var concurrent = handler.Enqueue(); var pending = panel.SubmitAsync();
            await concurrent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(submitted.Visibility == Visibility.Visible && submittedText.Text == "再次检查", "Follow-up note was not displayed while pending");
            Check(book.Active.SharedDocument.TryApply(book.Active.SharedDocument.Revision, preConflict + "\n\n用户新内容。", "用户"), "Could not create concurrent user edit");
            concurrent.Answer.TrySetResult(JsonSerializer.Serialize(new { initial_latex = (string?)null, edits = new[] { new { old_text = "这个结论需要正整数条件。", new_text = "模型修改。", reason = "" } }, comments = Array.Empty<object>(), message = "" }));
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
            Check(submitted.Visibility == Visibility.Collapsed && submittedText.Text.Length == 0, "Failed request left a stale note visible");
            Check(book.Turns.Last().IsIncomplete && book.Active.SharedDocument.Source.EndsWith("用户新内容。") && !book.Active.SharedDocument.Source.Contains("模型修改。"), "Concurrent user edit was overwritten");
            await EventuallyAsync(async () => await Script("(()=>{try{return JSON.parse(document.querySelector('#host-unapplied-content').value).edits[0].new_text === '模型修改。' && !document.querySelector('#host-unapplied').hidden}catch{return false}})()") == "true", "Unapplied model result was not made available in actual WebView2");
            var gitVersions = await AiDocumentRepository.ListVersionsAsync(project, book.Active.Id);
            Check(gitVersions.Count >= 2, "Conversation Git versions did not record model and user changes");
            Console.WriteLine("PASS simulated model initial draft, WebView2 comment bridge, current document snapshot, retry snapshot/draft, concurrent conflict and Git versions");
            var destination = Path.Combine(output, "mentor-destination-" + Guid.NewGuid().ToString("N") + ".tex");
            await panel.SetProjectAsync(destination);
            var destinationBook = (AiNotebook)Field(panel, "_book")!;
            composer.Text = "新会话问题"; var destinationInitial = handler.Enqueue(); destinationInitial.Answer.TrySetResult("新会话资料。");
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(!destinationBook.Turns.Last().IsIncomplete, "Cross-project destination initial request failed");
            composer.Text = "检查新会话"; var remoteReply = handler.Enqueue();
            remoteReply.Answer.TrySetResult(JsonSerializer.Serialize(new { initial_latex = (string?)null, edits = Array.Empty<object>(), comments = Array.Empty<object>(), message = "" }));
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(!destinationBook.Turns.Last().IsIncomplete, "Destination follow-up failed");
            Check(destinationBook.Turns.Last().MentorRequest!.References.Count == 0, "A previous project's comments were carried to the new session");
            await panel.SetProjectAsync(project);
            var reopened = (AiNotebook)Field(panel, "_book")!;
            Check(reopened.Active.SharedDocument.Comments.Single(c => c.Id == "qa-comment").Text == "失败后修改的评论", "Original conversation comment was lost after switching projects");
            Console.WriteLine("PASS cross-project isolation and reopened source discussion");
            await MeasureMentorWebViewPerformanceAsync(browserView, browser, output);
        }
        finally { host.Close(); }
    }

    private static async Task MeasureMentorWebViewPerformanceAsync(AiDocumentView view, object browser, string output)
    {
        var core = browser.GetType().GetProperty("CoreWebView2")!.GetValue(browser)!;
        async Task<string> Script(string script) => await (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [script])!;
        async Task<string> Protocol(string name, string argument) => await (Task<string>)core.GetType().GetMethod("CallDevToolsProtocolMethodAsync")!.Invoke(core, [name, argument])!;
        var paragraphs = Enumerable.Range(1, 120).Select(i => $"第 {i} 段：这个结论需要检查已有假设及推导过程，记录仍未解决的问题。").ToArray();
        var source = string.Join("\n\n", paragraphs);
        var result = new List<object>();
        for (var round = 0; round < 3; round++)
        foreach (var count in new[] { 0, 60 })
        {
            var document = new AiSharedDocument(); document.TryInitialize(source, "测试模型");
            for (var i = 0; i < count; i++)
            {
                var quote = $"第 {i + 1} 段"; var start = source.IndexOf(quote, StringComparison.Ordinal);
                document.Comments.Add(new AiDocumentComment { Id = "perf-" + i, Start = start, Length = quote.Length, Quote = quote, Text = "请核对论证的前提和范围。", Replies = [new AiDocumentReply { Text = "进一步检查支持材料。" }] });
            }
            view.Bind(document, "性能夹具", "用户", "测试模型");
            await EventuallyAsync(async () => await Script("document.querySelectorAll('#mentor-document p').length") == "120", "Performance fixture did not render");
            var samples = new List<object>();
            for (var i = 0; i < 9; i++)
            {
                if (i == 2) await Protocol("HeapProfiler.startSampling", "{\"samplingInterval\":1024,\"includeObjectsCollectedByMajorGC\":true,\"includeObjectsCollectedByMinorGC\":true}");
                await Script("window.__measure=null; {const start=performance.now(); document.addEventListener('texeed-document-rendered',()=>{const sync=performance.now()-start;requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__measure={sync,frame:performance.now()-start}}))},{once:true});} true");
                view.RefreshDocument();
                await EventuallyAsync(async () => await Script("window.__measure!==null") == "true", "WebView render timing did not complete");
                using var rendering = JsonDocument.Parse(await Script("window.__measure"));
                var sync = rendering.RootElement.GetProperty("sync").GetDouble(); var frame = rendering.RootElement.GetProperty("frame").GetDouble();
                await Script("window.__selection=null; {const p=document.querySelector('#mentor-document p'),node=document.createTreeWalker(p,NodeFilter.SHOW_TEXT).nextNode(),r=document.createRange();r.setStart(node,0);r.setEnd(node,Math.min(4,node.length));const start=performance.now(),s=getSelection();s.removeAllRanges();s.addRange(r);const sync=performance.now()-start;requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__selection={sync,frame:performance.now()-start};s.removeAllRanges()}))} true");
                await EventuallyAsync(async () => await Script("window.__selection!==null") == "true", "WebView selection timing did not complete");
                var selection = await Script("window.__selection");
                using var selected = JsonDocument.Parse(selection);
                if (i >= 2) samples.Add(new { render = sync, renderFrame = frame, selection = selected.RootElement.GetProperty("sync").GetDouble(), selectionFrame = selected.RootElement.GetProperty("frame").GetDouble() });
            }
            using var heap = JsonDocument.Parse(await Protocol("HeapProfiler.stopSampling", "{}"));
            static long Retained(JsonElement node) => (node.TryGetProperty("selfSize", out var size) ? size.GetInt64() : 0) + (node.TryGetProperty("children", out var children) ? children.EnumerateArray().Sum(Retained) : 0);
            var profile = heap.RootElement.GetProperty("profile");
            var sampled = Retained(profile.GetProperty("head"));
            var allocated = profile.GetProperty("samples").EnumerateArray().Sum(sample => sample.GetProperty("size").GetInt64());
            using var counts = JsonDocument.Parse(await Script("(()=>{const e=[...document.querySelectorAll('button,input,textarea,select,summary')];return {elements:document.querySelectorAll('*').length,paragraphs:document.querySelectorAll('#mentor-document p').length,comments:document.querySelectorAll('.mentor-comment').length,visible:e.filter(x=>{const r=x.getBoundingClientRect();return r.width&&r.height&&r.right>0&&r.left<innerWidth&&r.bottom>0&&r.top<innerHeight}).length}})()"));
            result.Add(new { round, count, samples, sampledRetainedJsBytes = sampled, sampledAllocatedJsBytes = allocated, counts = counts.RootElement.Clone() });
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "webview-document-performance.json"), JsonSerializer.Serialize(new { fixture = "120 paragraphs, 0 or 60 comments", timing = "render event and two animation frames; programmatic selection", allocation = "CDP sampled JavaScript allocations across seven operations, including objects collected by major/minor GC; estimate, not exact total", viewport = "offscreen 1000x800 WPF host", measurements = result }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Actual offscreen WebView performance samples saved to " + Path.Combine(output, "webview-document-performance.json"));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string error)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { try { if (await condition()) return; } catch (InvalidOperationException) { } await Task.Delay(50); }
        throw new Exception(error);
    }

    private sealed class MentorIntegrationHandler : HttpMessageHandler
    {
        internal sealed class Pending
        {
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<string> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Queue<Pending> _queue = new();
        internal readonly List<string> Requests = [];
        internal Pending Enqueue() { var value = new Pending(); lock (_queue) _queue.Enqueue(value); return value; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token); lock (Requests) Requests.Add(body);
            Pending pending; lock (_queue) pending = _queue.Dequeue();
            pending.Entered.TrySetResult();
            var answer = await pending.Answer.Task.WaitAsync(token);
            var stream = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = answer }) + "\n\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
