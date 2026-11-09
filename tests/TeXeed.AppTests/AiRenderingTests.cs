using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private const string MarkdownFixture = """
        ## 让回答更清晰

        这段回答包含 **重点**、*说明*、~~删除线~~ 和 `行内代码`。

        > 引用与正文保持清楚的层次。

        1. 先检查源文件。
        2. 再编译文档。
           - 支持嵌套列表。

        ```latex
        \documentclass{article}
        \begin{document}
        Hello, TeXeed!
        \end{document}
        ```

        | 操作 | 快捷键 |
        | --- | --- |
        | 换行 | Enter / Shift+Enter |
        | 发送 | Ctrl+Enter |

        [参考链接](https://example.com) · 后续仍可继续追问。
        """;

    private static async Task CheckAiRenderingAsync(MainWindow window, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Invoke(window, "HideMainMenu", false);
        for (var i = 0; i < 200 && ((HashSet<string>)Field(window, "_closingAi")!).Count > 0; i++) await Task.Delay(10);
        Invoke(window, "OpenAiTab"); await Task.Delay(100);
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var composer = (TextBox)ai.FindName("Composer");
        foreach (var mods in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Alt })
            Check(!AiPanel.IsSendGesture(Key.Enter, mods), "Enter sent with an unauthorized modifier");
        Check(AiPanel.IsSendGesture(Key.Enter, ModifierKeys.Control) && !AiPanel.IsSendGesture(Key.ImeProcessed, ModifierKeys.Control), "Send or IME gesture is incorrect");
        window.Activate(); composer.Focus(); window.UpdateLayout();
        var key = CreateKey(window, Key.Enter, Keyboard.PreviewKeyDownEvent);
        await CallAsync(window, "HandleShortcutAsync", key, ModifierKeys.Control);
        Check(!key.Handled, "Window consumed chat Ctrl+Enter as compile");
        composer.Text = "第一行"; composer.CaretIndex = composer.Text.Length;
        EditingCommands.EnterParagraphBreak.Execute(null, composer);
        Check(composer.Text.Contains('\n'), "Composer cannot insert a normal newline");

        var rendered = new MarkdownView { Markdown = MarkdownFixture };
        Check(rendered.Document.Blocks.OfType<Table>().Count() == 1 && rendered.Document.Blocks.OfType<System.Windows.Documents.List>().Count() == 1,
            "Markdown tables/lists were flattened");
        Check(rendered.Document.Blocks.OfType<Paragraph>().First().FontSize > rendered.FontSize, "Heading has no hierarchy");
        var copied = new TextRange(rendered.Document.ContentStart, rendered.Document.ContentEnd).Text;
        Check(copied.Contains("\\documentclass") && !copied.Contains("**重点**") && !copied.Contains("```"), "Markdown selection/copy exposes formatting syntax");
        rendered.Markdown = "**未完成\n\n```tex\n\\section{继续}";
        Check(new TextRange(rendered.Document.ContentStart, rendered.Document.ContentEnd).Text.Contains("section"), "Incomplete stream lost its code fence");
        rendered.Markdown = "[bad](javascript:alert(1)) ![image](https://example.com/tracker.png) <script>alert(1)</script>";
        Check(!rendered.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Any(), "Unsafe Markdown generated an active link");

        // Show the actual submitting panel: previous tests submitted only an unattached, unmeasured control.
        var handler = new AiBurstHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        var host = new Window { Width = 760, Height = 860, Content = panel, Title = "AI 共同文稿回归", Owner = window };
        var panelProject = Path.Combine(output, "burst-" + Guid.NewGuid().ToString("N") + ".tex");
        await panel.SetProjectAsync(panelProject);
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("test-model", "测试模型") }; models.SelectedIndex = 0;
        host.Show();
        var input = (TextBox)panel.FindName("Composer");
        var heartbeat = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => heartbeat++;
        timer.Start();
        try
        {
            for (var round = 0; round < 3; round++)
            {
                Console.WriteLine("AI stream round " + round + " started");
                var bookSnapshot = ((AiNotebook)Field(panel, "_book")!).Active.SharedDocument.Snapshot();
                input.Text = "连续追问 " + round;
                var before = heartbeat;
                var submission = panel.SubmitAsync();
                for (var wait = 0; input.Text.Length > 0 && wait < 100; wait++) await Task.Delay(10);
                Check(input.Text.Length == 0 && !input.IsReadOnly, "Durable acceptance must clear the composer before the response and permit a new draft");
                Check(((AiNotebook)Field(panel,"_book")!).Active.Title == "连续追问 0", "Title must update immediately and stay stable on follow-ups");
                if (round == 0)
                {
                    await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!submission.IsCompleted, "Title regression must inspect a request that has not returned");
                    var saved = AiStorage.Load(panelProject);
                    Check(saved.Active.Title == "连续追问 0" && saved.Turns.Single().Question == "连续追问 0" && saved.Turns.Single().AnsweredAt is null, "Title and pending question must be persisted before the first model response");
                    host.UpdateLayout();
                    Capture(host, Path.Combine(output, "ai-title-during-request.png"));
                    handler.ReleaseFirstResponse.TrySetResult();
                }
                input.Text = "下一条草稿";
                await panel.SubmitAsync(); // Duplicate clicks must not start another request.
                await submission.WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine("AI stream round " + round + " completed");
                host.UpdateLayout();
                var activeBook = (AiNotebook)Field(panel, "_book")!; var turns = activeBook.Turns;
                Check(turns.Count == round + 1 && !turns.Last().IsIncomplete && turns.Last().MentorRequest?.Source == bookSnapshot.Source, "Follow-up lost its document snapshot or failed");
                Check(round == 0 ? activeBook.Active.SharedDocument.Source.Contains("INITIAL_SHARED_DOCUMENT") : activeBook.Active.SharedDocument.Source.Contains("AI_REVISION_" + (round + 1)), "The model response did not update the shared source");
                Check(handler.SourceSnapshots[round] == bookSnapshot.Source, "The sent document did not match the request snapshot");
                if (round > 0) Check(handler.CommentSnapshots[round].Contains("USER_COMMENT"), "The current session comment was omitted from the request snapshot");
                Check(input.Text == "下一条草稿", "Completion cleared the next draft");
                Check(heartbeat > before + 2, "Streaming blocked the UI dispatcher");
                if (round == 0)
                {
                    var document = activeBook.Active.SharedDocument; var source = document.Source;
                    var start = source.IndexOf("\\end{document}", StringComparison.Ordinal);
                    Check(document.TryApply(document.Revision, source.Insert(start, "USER_REVISION\n"), "用户"), "Could not prepare a user revision");
                    var position = document.Source.IndexOf("USER_REVISION", StringComparison.Ordinal);
                    document.Comments.Add(new AiDocumentComment { Start = position, Length = 13, Quote = "USER_REVISION", Text = "USER_COMMENT" });
                }
            }
            Check(handler.HistoryCounts.SequenceEqual(new[] { 0, 0, 0 }), "A fresh request automatically included previous conversation turns");
            input.Text = "取消中的问题";
            var pending = panel.SubmitAsync();
            input.Text = "下一条草稿";
            await Task.Delay(300);
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(input.Text == "下一条草稿" && !input.IsReadOnly && ((Button)panel.FindName("SubmitButton")).IsEnabled, "Cancellation lost the draft or left the composer locked");
            input.Text = "取消后继续"; await panel.SubmitAsync();
            Check(!((AiNotebook)Field(panel, "_book")!).Turns.Last().IsIncomplete, "A fresh request after cancellation failed");

            await panel.NewConversationAsync();
            input.Text = "首次取消的问题";
            pending = panel.SubmitAsync();
            Check(((AiNotebook)Field(panel,"_book")!).Active.Title == "首次取消的问题", "First canceled request was not named immediately");
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var canceledBook = AiStorage.Load(panelProject);
            Check(canceledBook.Active.Title == "首次取消的问题" && canceledBook.Turns.Single().Answer.StartsWith("[本轮未完成]"), "Canceling the first turn lost its title or incorrectly completed it");

            await panel.NewConversationAsync();
            handler.FailNextRequest = true;
            var attemptsBeforeFailure = handler.HistoryCounts.Count;
            input.Text = "首次连接失败的问题";
            await panel.SubmitAsync();
            var failedBook = AiStorage.Load(panelProject);
            Check(failedBook.Active.Title == "首次连接失败的问题" && !failedBook.Turns.Single().IsIncomplete && handler.HistoryCounts.Count == attemptsBeforeFailure + 2,
                "A transient failure did not recover with the original first-turn title");

            await panel.NewConversationAsync();
            ((AiNotebook)Field(panel, "_book")!).Active.Title = "保留自定义标题";
            handler.FailNextRequest = true;
            input.Text = "不应覆盖已有标题";
            pending = panel.SubmitAsync();
            Check(((AiNotebook)Field(panel,"_book")!).Active.Title == "保留自定义标题", "First submission replaced an existing title");
            await pending.WaitAsync(TimeSpan.FromSeconds(30));
            Check(AiStorage.Load(panelProject).Active.Title == "保留自定义标题", "Completion replaced an existing title");
        }
        finally { timer.Stop(); host.Close(); }

        var book = (AiNotebook)Field(ai, "_book")!;
        book.Entries.Clear(); book.Turns.Clear(); book.Turns.Add(new("请展示 Markdown 排版，并保留清楚的阅读层次。", MarkdownFixture));
        book.Active.Title = "更自然的阅读与追问";
        Invoke(ai, "RefreshTranscript"); composer.Text = "继续解释第二步…";
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme); window.UpdateLayout();
            await CheckSelectionPixelsAsync(window, output, theme);
            var selectionBackground = ((SolidColorBrush)composer.SelectionBrush).Color;
            var selectionForeground = ((SolidColorBrush)composer.SelectionTextBrush).Color;
            Check(Contrast(selectionBackground, selectionForeground) >= 4.5, "Input selection contrast below 4.5:1");
            Check(Contrast(((SolidColorBrush)Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey]).Color, selectionForeground) >= 4.5,
                "Inactive selection contrast below 4.5:1");
            composer.Focus(); composer.SelectAll();
            await Task.Delay(150); window.UpdateLayout();
            Capture(window, Path.Combine(output, "ai-markdown-" + theme + ".png"));
            Invoke(window,"OpenAiHistory"); await Task.Delay(50);
            var historyView=(AiHistoryView)Field(window,"_aiHistory")!;
            ((TextBox)historyView.FindName("SearchBox")).Text="阅读";
            Check(((ItemsControl)historyView.FindName("ConversationList")).Items.Count==1,"History tab search failed");
            Capture(window,Path.Combine(output,"ai-history-tab-"+theme+".png"));
            Invoke(window,"OpenAiTab");
        }
        ThemeManager.Apply("ristretto");
        Console.WriteLine("AI rendering: Markdown, immediate/persisted titles, shared-document snapshots, streaming, cancellation, keyboard routing, history dialog and selection contrast passed.");
    }

    private static async Task CheckSelectionPixelsAsync(Window owner, string output, string theme)
    {
        var text = new TextBox { Text = "选中文字必须清晰可见 Selected text", FontSize = 22, Margin = new Thickness(12) };
        var password = new PasswordBox { Password = "selected-password", FontSize = 22, Padding = new Thickness(8), Margin = new Thickness(12) };
        var markdown = new MarkdownView { Markdown = "**Markdown 选区** 与 `代码` 应清晰可读", Margin = new Thickness(12) };
        var stack = new StackPanel(); stack.Children.Add(text); stack.Children.Add(password); stack.Children.Add(markdown);
        var host = new Window { Content = stack, Owner = owner, Width = 650, Height = 270, Title = "文字选区 · " + theme };
        host.Show(); host.Activate();
        try
        {
            foreach (var control in new Control[] { text, password, markdown })
            {
                control.Focus();
                switch (control) { case TextBox box: box.SelectAll(); break; case PasswordBox box: box.SelectAll(); break; case RichTextBox box: box.SelectAll(); break; }
                await Task.Delay(70); host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(control), null, new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                bitmap.Render(visual);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var expected = ((SolidColorBrush)Application.Current.Resources[control == markdown ? "Ink" : "TextSelectionForeground"]).Color;
                if (control == markdown)
                {
                    var selection = ((SolidColorBrush)markdown.SelectionBrush).Color;
                    expected = Color.FromRgb((byte)(expected.R * .65 + selection.R * .35), (byte)(expected.G * .65 + selection.G * .35), (byte)(expected.B * .65 + selection.B * .35));
                }
                var glyphPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i + 3] > 200 && Math.Abs(pixels[i] - expected.B) < 24 && Math.Abs(pixels[i + 1] - expected.G) < 24 && Math.Abs(pixels[i + 2] - expected.R) < 24) glyphPixels++;
                Capture(host, Path.Combine(output, $"selection-{theme}-{control.GetType().Name}.png"));
                if (glyphPixels < 30) throw new Exception($"{theme} {control.GetType().Name}: selection painted over text ({glyphPixels} glyph pixels)");
            }
        }
        finally { host.Close(); }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color color)
        {
            static double C(byte x) { var s = x / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }

    private sealed class AiBurstHandler : HttpMessageHandler
    {
        internal List<int> HistoryCounts = [];
        internal List<string> SourceSnapshots = [];
        internal List<string> CommentSnapshots = [];
        internal TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailNextRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            HistoryCounts.Add(json["input"]!.AsArray().Count(n => n?["role"]?.ToString() == "assistant"));
            var contexts = json["input"]!.AsArray().Where(n => n?["content"] is JsonArray)
                .SelectMany(n => n!["content"]!.AsArray()).Select(part => part?["text"]?.ToString() ?? "");
            var context = contexts.Single(text => text.Contains("\"current_latex\"", StringComparison.Ordinal));
            var parsed = JsonNode.Parse(context[context.IndexOf('{')..].Trim())!;
            var source = parsed["current_latex"]!.GetValue<string>();
            SourceSnapshots.Add(source); CommentSnapshots.Add(parsed["comments"]!.ToJsonString());
            if (HistoryCounts.Count == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstResponse.Task.WaitAsync(token);
            }
            if (FailNextRequest) { FailNextRequest = false; throw new HttpRequestException("模拟连接失败"); }
            var answer = source.Length == 0
                ? "\\documentclass{article}\n\\begin{document}\nINITIAL_SHARED_DOCUMENT\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"Paragraph {i}: a long shared document for streaming.\n\n")) + "\\end{document}"
                : JsonSerializer.Serialize(new { initial_latex = (string?)null, edits = new[] { new { old_text = "\\end{document}", new_text = "AI_REVISION_" + HistoryCounts.Count + ": " + new string('x', 150) + "\n\\end{document}", reason = "" } }, comments = Array.Empty<object>(), message = "" });
            var events = new StringBuilder();
            foreach (var c in answer) events.Append("data: ").Append(JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = c.ToString() })).Append("\n\n");
            events.Append("data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowAiStream(Encoding.UTF8.GetBytes(events.ToString()))) };
        }
    }
    private sealed class SlowAiStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(10, cancellationToken); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 2048)], cancellationToken); }
    }
}
