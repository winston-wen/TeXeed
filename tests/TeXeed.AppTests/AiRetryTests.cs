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

        var fixture = new AiFakeHandler { ProgramFailure = true };
        using (var panel = new AiPanel(new AiConnection(fixture)))
        {
            await panel.SetProjectAsync(Path.Combine(output, "manual-retry.tex"));
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            var host = new Window { Owner = owner, Width = 820, Height = 720, Content = panel }; host.Show();
            try
            {
                var composer = (TextBox)panel.FindName("Composer"); composer.Text = "原来的问题";
                var original = new AiEntry { Question = "原批注" }; panel.AddEntry(original); await panel.SubmitAsync();
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var book = (AiNotebook)Field(panel, "_book")!; var failed = book.Turns.Last();
                Check(failed.EndReason == "program" && Descendants<AiTurnView>(panel).Single().CanRetry, "Last program-stopped turn has no retry action");
                Capture(host, Path.Combine(output, "retry-program-stop.png"));
                host.Width = 420; host.UpdateLayout();
                var narrowRetry = Descendants<Button>(panel).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "重试本轮" && b.IsVisible);
                Check(VisibleBounds(narrowRetry, panel).Width >= narrowRetry.ActualWidth - .5, "Narrow footer clips its retry action");
                Capture(host, Path.Combine(output, "retry-program-stop-narrow.png")); host.Width = 820; host.UpdateLayout();
                composer.Text = "下一条草稿"; var pending = new AiEntry { Question = "尚未发送的批注" }; panel.AddEntry(pending);
                fixture.ProgramFailure = false;
                var retryButton = Descendants<Button>(panel).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "重试本轮" && b.IsVisible);
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var until = DateTime.UtcNow.AddSeconds(5);
                while (panel.HasActiveOperation && DateTime.UtcNow < until) await Task.Delay(10);
                Check(!panel.HasActiveOperation, "Retry button did not finish its operation"); host.UpdateLayout();
                Check(book.Turns.Count == 2 && !book.Turns.Last().IsIncomplete && book.Turns.Last().Prompt == "原来的问题" && book.Turns.Last().SentAnnotations.Single().Id == original.Id, "Retry lost the original question or annotation snapshot");
                Check(composer.Text == "下一条草稿" && book.Entries.Single() == pending, "Retry consumed the next draft or pending annotation");
                Check(!Descendants<AiTurnView>(panel).Any(v => v.CanRetry), "A non-last or completed turn retains a retry action");
                var previousRequests = fixture.ResponseRequests; await panel.SubmitAsync(failed);
                Check(previousRequests == fixture.ResponseRequests, "A stale retry action submitted an old turn");
                var legacy = new AiTurn("已有过载中止记录", "[本轮未完成]") { Segments = [new() { Id = "failure", Kind = "activity", Text = "本轮未完成：server_is_overloaded" }] };
                book.Turns.Add(legacy); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ((ScrollViewer)panel.FindName("MessagesScroll")).ScrollToEnd(); host.UpdateLayout();
                Check(Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == legacy.Id).CanRetry, "An old overload record without an explicit incomplete flag cannot retry");
            }
            finally { host.Close(); }
        }
        using (var stalled = new StalledAiHandler())
        using (var panel = new AiPanel(new AiConnection(stalled)))
        {
            await panel.SetProjectAsync(Path.Combine(output, "immediate-send.tex"));
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            var book = (AiNotebook)Field(panel, "_book")!;
            for (var i = 0; i < 100; i++) book.Turns.Add(new("历史问题 " + i, "历史正文。"));
            Invoke(panel, "RefreshTranscript");
            var host = new Window { Owner = owner, Width = 820, Height = 720, Content = panel }; host.Show(); host.UpdateLayout();
            try
            {
                ((TextBox)panel.FindName("Composer")).Text = "网络尚未返回时也应立即显示我的问题";
                var request = panel.SubmitAsync();
                Check(((FrameworkElement)panel.FindName("LiveContainer")).Visibility == Visibility.Visible
                    && ((MarkdownView)panel.FindName("LiveQuestion")).Markdown.Contains("立即显示"), "Submission waited for disk or network before echoing the question");
                await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var live = (FrameworkElement)panel.FindName("LiveContainer");
                Check(live.IsVisible && VisibleBounds((FrameworkElement)panel.FindName("LiveQuestion"), panel).Height > 0
                    && ((AiReplyStatus)panel.FindName("LiveReplyStatus")).IsThinking
                    && ((AiReplyStatus)panel.FindName("LiveReplyStatus")).PendingText == "等待 AI 响应", "Virtualized history hid the active question or processing state");
                Capture(host, Path.Combine(output, "immediate-send-stalled.png"));
                ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await request.WaitAsync(TimeSpan.FromSeconds(5));
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(book.Turns.Last().EndReason == "user" && Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == book.Turns.Last().Id).CanRetry, "Last user-stopped turn has no retry action");
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
}
