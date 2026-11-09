using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiRetryAsync(MainWindow owner, string output)
    {
        var checks = 0; void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        static HttpResponseMessage Sse(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        const string success = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"最终答案\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
        var waits = new List<double>(); var executed = 0; var inventories = 0; var buffer = new AiStreamBuffer();
        using (var handler = new RetryFixtureHandler(attempt => attempt switch
        {
            <= 6 => throw new HttpRequestException("fixture SSL interruption", new System.Security.Authentication.AuthenticationException()),
            7 => Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"断流片段\"}\n\n"),
            8 => Sse("data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"function_call\",\"name\":\"read_source\",\"call_id\":\"read-once\",\"arguments\":\"{}\"}]}}\n\n"),
            9 => throw new HttpRequestException("fixture after-tool interruption"),
            10 => Sse("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"server_is_overloaded\",\"message\":\"fixture overload\"}}}\n\n"),
            _ => Sse(success)
        }))
        using (var connection = new AiConnection(handler))
        {
            await new AiResponses(connection, (delay, _) => { waits.Add(delay.TotalSeconds); return Task.CompletedTask; }).RunAsync("fixture", [], [], [], _ => { }, _ => { }, CancellationToken.None,
                toolRunner: (name, _, _) => { if (name == "list_files") inventories++; else executed++; return Task.FromResult("已读取合成资料"); }, activity: buffer.Add);
            Check(handler.Requests == 11 && waits.Count == 9 && waits.Take(6).SequenceEqual(new[] { 1d, 2, 4, 8, 16, 30 }), "Network/overload retries stopped at a fixed attempt count or lost backoff");
            Check(executed == 1 && inventories == 1, "Retry repeated an already completed project operation");
            Check(buffer.Snapshot().Single(s => s.Kind == "final").Text == "最终答案" && !string.Concat(buffer.Snapshot().Select(s => s.Text)).Contains("断流片段"), "Retry duplicated partial stream content");
            Check(buffer.Snapshot().All(s => !s.IsTransientStatus), "Connection/retry status leaked into the persisted transcript");
            Check(buffer.PendingStatus.Text == "正在生成回复", "Pending status did not advance when reply text arrived");
        }
        var transient = new AiStreamBuffer(); var dataVersion = transient.Version;
        transient.Add(new("pending", "status", "网络连接中断，2 秒后重试"));
        transient.Add(new("pending", "status", "网络连接中断，1 秒后重试"));
        Check(transient.Version == dataVersion && transient.Snapshot().Count == 0 && transient.PendingStatus.Version == 2, "Retry countdown caused a transcript snapshot or checkpoint rewrite");
        var oldConnection = new AiTurn("旧问题", "旧答复") { Segments = [new() { Id = "request-state-2", Kind = "activity", Text = "已连接 AI" }, new() { Kind = "activity", Text = "已读取源码 · sample.tex" }, new() { Kind = "final", Text = "旧答复" }] };
        Check(oldConnection.DisplaySegments.Count == 2 && oldConnection.Segments.Count == 3, "Legacy status filtering changed saved content or hid real work");
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new RetryFixtureHandler(_ => throw new HttpRequestException("offline")))
        using (var connection = new AiConnection(handler))
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new AiResponses(connection, async (_, token) => { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); })
                .RunAsync("fixture", [], [], [], _ => { }, _ => { }, cancellation.Token);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await request; throw new Exception("Cancel did not stop retry wait"); } catch (OperationCanceledException) { checks++; }
            Check(handler.Requests == 1, "User cancellation issued another retry");
        }
        Check(!AiResponses.IsNetworkFailure(new HttpRequestException("auth", null, HttpStatusCode.Unauthorized), CancellationToken.None)
            && !AiResponses.IsNetworkFailure(new InvalidOperationException("invalid request"), CancellationToken.None), "Permanent failures enter the network loop");
        var overloaded = AiFailure.FromEvent(JsonNode.Parse("""{"response":{"error":{"code":"server_is_overloaded","message":"try later"}}}"""));
        Check(AiResponses.IsNetworkFailure(overloaded, CancellationToken.None), "Server overload was treated as a permanent failure");

        var fixture = new SharedRetryHandler { ProgramFailure = true };
        using (var panel = new AiPanel(new AiConnection(fixture)))
        {
            await panel.SetProjectAsync(Path.Combine(output, "manual-retry-" + Guid.NewGuid().ToString("N") + ".tex"));
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            var host = new Window { Owner = owner, Width = 820, Height = 720, Content = panel }; host.Show();
            try
            {
                var book = (AiNotebook)Field(panel, "_book")!;
                const string original = "\\documentclass{article}\n\\begin{document}\nORIGINAL_CONTEXT\n\\end{document}";
                Check(book.Active.SharedDocument.TryInitialize(original, "fixture"), "Could not prepare original document snapshot");
                book.Active.SharedDocument.Comments.Add(new AiDocumentComment { Start = original.IndexOf("ORIGINAL_CONTEXT", StringComparison.Ordinal), Length = 16, Quote = "ORIGINAL_CONTEXT", Text = "ORIGINAL_COMMENT" });
                var composer = (TextBox)panel.FindName("Composer"); composer.Text = "原来的问题";
                await panel.SubmitAsync();
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var failed = book.Turns.Last(); var retryButton = (Button)panel.FindName("SharedRetryButton");
                Check(failed.EndReason == "program" && retryButton.IsVisible && retryButton.IsEnabled, "Last program-stopped request has no retry action");
                Capture(host, Path.Combine(output, "retry-program-stop.png"));
                host.Width = 420; host.UpdateLayout();
                Check(VisibleBounds(retryButton, panel).Width >= retryButton.ActualWidth - .5, "Narrow sidebar clips its retry action");
                Capture(host, Path.Combine(output, "retry-program-stop-narrow.png")); host.Width = 820; host.UpdateLayout();
                composer.Text = "下一条草稿";
                Check(book.Active.SharedDocument.TryApply(book.Active.SharedDocument.Revision, original.Replace("ORIGINAL_CONTEXT", "LATEST_CHANGE"), "用户"), "Could not prepare a later document edit");
                book.Active.SharedDocument.Comments[0].Text = "LATEST_COMMENT";
                fixture.ProgramFailure = false;
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var until = DateTime.UtcNow.AddSeconds(5);
                while (panel.HasActiveOperation && DateTime.UtcNow < until) await Task.Delay(10);
                Check(!panel.HasActiveOperation, "Retry button did not finish its operation"); host.UpdateLayout();
                Check(book.Turns.Count == 2 && book.Turns.Last().IsIncomplete && book.Turns.Last().Prompt == "原来的问题", "Retry lost the original question or overwrote a concurrent edit");
                Check(fixture.LastInput.Contains("ORIGINAL_CONTEXT") && fixture.LastInput.Contains("ORIGINAL_COMMENT") && !fixture.LastInput.Contains("LATEST_CHANGE") && !fixture.LastInput.Contains("LATEST_COMMENT"), "Retry did not use the original document and comment snapshot");
                Check(composer.Text == "下一条草稿", "Retry consumed the next draft");
                Check(book.Active.SharedDocument.Source.Contains("LATEST_CHANGE") && book.Active.SharedDocument.Comments[0].Text == "LATEST_COMMENT", "Retry overwrote a later document or comment edit");
                composer.Text = "重新提问"; await panel.SubmitAsync(); host.UpdateLayout();
                Check(!book.Turns.Last().IsIncomplete, "A fresh request did not complete");
                Check(retryButton.Visibility == Visibility.Collapsed, "A completed last request retains a retry action");
                var previousRequests = fixture.ResponseRequests; await panel.SubmitAsync(failed);
                Check(previousRequests == fixture.ResponseRequests, "A stale retry action submitted an old turn");
                var legacy = new AiTurn("已有过载中止记录", "[本轮未完成]") { MentorRequest = failed.MentorRequest?.Snapshot(), Segments = [new() { Id = "failure", Kind = "activity", Text = "本轮未完成：server_is_overloaded" }] };
                book.Turns.Add(legacy); Invoke(panel, "RefreshTranscript"); Invoke(panel, "UpdateRetryActions"); host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(retryButton.IsVisible && retryButton.IsEnabled, "An old overload record without an explicit incomplete flag cannot retry");
                var oldCount = fixture.ResponseRequests; composer.Text = "重试时保留的新草稿";
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                until = DateTime.UtcNow.AddSeconds(5);
                while (panel.HasActiveOperation && DateTime.UtcNow < until) await Task.Delay(10);
                Check(!panel.HasActiveOperation && fixture.ResponseRequests > oldCount && composer.Text == "重试时保留的新草稿", "Retrying an old overload record failed or consumed a later draft");
                var withoutSnapshot = new AiTurn("缺少快照的旧问题", "[本轮未完成]") { Segments = [new() { Id = "failure", Kind = "activity", Text = "本轮未完成：server_is_overloaded" }] };
                book.Turns.Add(withoutSnapshot); Invoke(panel, "RefreshTranscript"); Invoke(panel, "UpdateRetryActions"); host.UpdateLayout();
                var sawWarning = false; var preservedQuestion = false;
                var idleAllocated = GC.GetAllocatedBytesForCurrentThread(); var idleWatch = System.Diagnostics.Stopwatch.StartNew();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var idleMs = idleWatch.Elapsed.TotalMilliseconds; var idleBytes = GC.GetAllocatedBytesForCurrentThread() - idleAllocated;
                var openAllocated = 0L; var openMs = 0d; var visibleControls = 0; var allocationStart = 0L; var openWatch = new System.Diagnostics.Stopwatch();
                var closeAllocated = 0L; var closeMs = 0d; var closeAllocationStart = 0L; var closeWatch = new System.Diagnostics.Stopwatch();
                void AnswerDialog(bool send)
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "重新发送旧问题");
                    if (!send)
                    {
                        openMs = openWatch.Elapsed.TotalMilliseconds; openAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                        visibleControls = Descendants<Control>(dialog).Count(control => control.IsVisible);
                        Capture(dialog, Path.Combine(output, "legacy-resend-dialog.png"));
                    }
                    sawWarning = Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("没有保存当时的文稿和评论快照") && t.Text.Contains("当前文稿"));
                    var input = Descendants<TextBox>(dialog).Single(); preservedQuestion = input.Text == "缺少快照的旧问题";
                    var choice = Descendants<Button>(dialog).Single(b => Equals(b.Content, send ? "用当前文稿发送" : "取消"));
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(choice);
                    if (!send) { closeAllocationStart = GC.GetAllocatedBytesForCurrentThread(); closeWatch.Start(); }
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer).Invoke();
                }
                oldCount = fixture.ResponseRequests;
                allocationStart = GC.GetAllocatedBytesForCurrentThread(); openWatch.Start();
                _ = host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => AnswerDialog(false)));
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                closeMs = closeWatch.Elapsed.TotalMilliseconds; closeAllocated = GC.GetAllocatedBytesForCurrentThread() - closeAllocationStart;
                await File.WriteAllTextAsync(Path.Combine(output, "legacy-resend-dialog-performance.json"), System.Text.Json.JsonSerializer.Serialize(new { openMs, openAllocated, closeMs, closeAllocated, idleMs, idleBytes, visibleControls, adjustedOpenMs = Math.Max(0, openMs - idleMs), adjustedOpenBytes = Math.Max(0, openAllocated - idleBytes), adjustedCloseMs = Math.Max(0, closeMs - idleMs), adjustedCloseBytes = Math.Max(0, closeAllocated - idleBytes), limitation = "One cold modal opening and closing through ApplicationIdle; UI thread allocation includes modal dispatcher and no historical counterpart." }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Check(sawWarning && preservedQuestion && fixture.ResponseRequests == oldCount && composer.Text == "重试时保留的新草稿", "Canceling the missing-snapshot dialog changed the request or draft");
                sawWarning = preservedQuestion = false;
                _ = host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => AnswerDialog(true)));
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                until = DateTime.UtcNow.AddSeconds(5);
                while (panel.HasActiveOperation && DateTime.UtcNow < until) await Task.Delay(10);
                Check(sawWarning && preservedQuestion && !panel.HasActiveOperation && fixture.ResponseRequests > oldCount &&
                    fixture.LastInput.Contains("LATEST_CHANGE") && fixture.LastInput.Contains("LATEST_COMMENT") && composer.Text == "重试时保留的新草稿",
                    "Resending a missing-snapshot record did not use the current document/comments or preserve the draft");
            }
            finally { host.Close(); }
        }
        using (var stalled = new StalledAiHandler())
        using (var panel = new AiPanel(new AiConnection(stalled)))
        {
            await panel.SetProjectAsync(Path.Combine(output, "immediate-send-" + Guid.NewGuid().ToString("N") + ".tex"));
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            var book = (AiNotebook)Field(panel, "_book")!;
            for (var i = 0; i < 100; i++) book.Turns.Add(new("历史问题 " + i, "历史正文。"));
            Invoke(panel, "RefreshTranscript");
            var host = new Window { Owner = owner, Width = 820, Height = 720, Content = panel }; host.Show(); host.UpdateLayout();
            try
            {
                ((TextBox)panel.FindName("Composer")).Text = "网络尚未返回时也应立即显示我的问题";
                var request = panel.SubmitAsync();
                Check(((FrameworkElement)panel.FindName("SharedSubmittedMessage")).Visibility == Visibility.Visible
                    && ((TextBlock)panel.FindName("SharedSubmittedText")).Text.Contains("立即显示"), "Submission waited for disk or network before echoing the question");
                await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var live = (FrameworkElement)panel.FindName("SharedSubmittedMessage");
                Check(live.IsVisible && VisibleBounds((FrameworkElement)panel.FindName("SharedSubmittedText"), panel).Height > 0
                    && ((AiReplyStatus)panel.FindName("SharedReplyStatus")).IsThinking
                    && ((AiReplyStatus)panel.FindName("SharedReplyStatus")).PendingText == "等待 AI 响应", "The active question or processing state was hidden");
                Capture(host, Path.Combine(output, "immediate-send-stalled.png"));
                ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await request.WaitAsync(TimeSpan.FromSeconds(5));
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(book.Turns.Last().EndReason == "user" && ((Button)panel.FindName("SharedRetryButton")).IsVisible, "Last user-stopped request has no retry action");
                Capture(host, Path.Combine(output, "retry-user-stop.png"));
            }
            finally { host.Close(); }
        }
        Console.WriteLine($"PASS: {checks} network retry, cancellation, exactly-once tool, original snapshot, draft preservation and immediate virtualized echo checks.");
    }
    private sealed class RetryFixtureHandler(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(++Requests));
    }
    private sealed class SharedRetryHandler : HttpMessageHandler
    {
        internal bool ProgramFailure;
        internal int ResponseRequests;
        internal string LastInput = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ResponseRequests++;
            LastInput = await request.Content!.ReadAsStringAsync(token);
            var text = ProgramFailure
                ? "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"fixture program failure\"}}}\n\n"
                : "data: " + System.Text.Json.JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "{\"initial_latex\":null,\"edits\":[],\"comments\":[],\"message\":\"\"}" }) + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
        }
    }
}
