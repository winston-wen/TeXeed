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
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var composer = (TextBox)ai.FindName("Composer");
        foreach (var mods in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Alt })
            Check(!AiPanel.IsSendGesture(Key.Enter, mods), "Enter sent with an unauthorized modifier");
        Check(AiPanel.IsSendGesture(Key.Enter, ModifierKeys.Control) && !AiPanel.IsSendGesture(Key.ImeProcessed, ModifierKeys.Control), "Send or IME gesture is incorrect");
        composer.Focus(); window.UpdateLayout();
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
        var host = new Window { Width = 760, Height = 860, Content = panel, Title = "AI 连续追问回归", Owner = window };
        var panelProject = Path.Combine(output, "burst.tex");
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
                input.Text = "连续追问 " + round;
                var before = heartbeat;
                var submission = panel.SubmitAsync();
                Check(input.Text.Length == 0 && !input.IsReadOnly, "Submission must clear the composer immediately and permit a new draft");
                Check(((TextBlock)panel.FindName("ConversationTitle")).Text == "连续追问 0", "Title must update immediately and stay stable on follow-ups");
                if (round == 0)
                {
                    await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!submission.IsCompleted, "Title regression must inspect a request that has not returned");
                    var saved = AiStorage.Load(panelProject);
                    Check(saved.Active.Title == "连续追问 0" && saved.Turns.Count == 0, "Title must be persisted before the first model response");
                    host.UpdateLayout();
                    Capture(host, Path.Combine(output, "ai-title-during-request.png"));
                    handler.ReleaseFirstResponse.TrySetResult();
                }
                input.Text = "下一条草稿";
                await panel.SubmitAsync(); // Duplicate clicks must not start another request.
                await submission.WaitAsync(TimeSpan.FromSeconds(30));
                host.UpdateLayout();
                var turns = ((AiNotebook)Field(panel, "_book")!).Turns;
                Check(turns.Count == round + 1 && turns.Last().Answer == handler.Answer, "Follow-up answer was lost, duplicated or truncated");
                Check(input.Text == "下一条草稿", "Completion cleared the next draft");
                Check(heartbeat > before + 2, "Streaming blocked the UI dispatcher");
                Check(((ItemsControl)panel.FindName("History")).Items.Count == round + 1, "Visible history did not update after follow-up");
            }
            Check(handler.HistoryCounts.SequenceEqual(new[] { 0, 1, 2 }), "Follow-up requests lost previous conversation turns");
            input.Text = "取消中的问题";
            var pending = panel.SubmitAsync();
            input.Text = "下一条草稿";
            await Task.Delay(300);
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(input.Text == "下一条草稿" && !input.IsReadOnly && ((Button)panel.FindName("SubmitButton")).IsEnabled, "Cancellation lost the draft or left the composer locked");
            input.Text = "取消后继续"; await panel.SubmitAsync();
            Check(((AiNotebook)Field(panel, "_book")!).Turns.Last().Answer == handler.Answer, "Retry after cancellation failed");

            await panel.NewConversationAsync();
            input.Text = "首次取消的问题";
            pending = panel.SubmitAsync();
            Check(((TextBlock)panel.FindName("ConversationTitle")).Text == "首次取消的问题", "First canceled request was not named immediately");
            ((Button)panel.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var canceledBook = AiStorage.Load(panelProject);
            Check(canceledBook.Active.Title == "首次取消的问题" && canceledBook.Turns.Single().Answer.StartsWith("[本轮未完成]"), "Canceling the first turn lost its title or incorrectly completed it");

            await panel.NewConversationAsync();
            handler.FailNextRequest = true;
            input.Text = "首次连接失败的问题";
            await panel.SubmitAsync();
            var failedBook = AiStorage.Load(panelProject);
            Check(failedBook.Active.Title == "首次连接失败的问题" && failedBook.Turns.Single().Answer.Contains("模拟连接失败"), "Failure on the first turn lost its title or was not exercised");

            await panel.NewConversationAsync();
            ((AiNotebook)Field(panel, "_book")!).Active.Title = "保留自定义标题";
            handler.FailNextRequest = true;
            input.Text = "不应覆盖已有标题";
            pending = panel.SubmitAsync();
            Check(((TextBlock)panel.FindName("ConversationTitle")).Text == "保留自定义标题", "First submission replaced an existing title");
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(AiStorage.Load(panelProject).Active.Title == "保留自定义标题", "Completion replaced an existing title");
        }
        finally { timer.Stop(); host.Close(); }

        var book = (AiNotebook)Field(ai, "_book")!;
        book.Entries.Clear(); book.Turns.Clear(); book.Turns.Add(new("请展示 Markdown 排版，并保留清楚的阅读层次。", MarkdownFixture));
        book.Active.Title = "更自然的阅读与追问";
        Invoke(ai, "Rebind"); Invoke(ai, "RefreshTranscript"); composer.Text = "继续解释第二步…";
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
            ((ScrollViewer)ai.FindName("MessagesScroll")).ScrollToTop();
            await Task.Delay(150); window.UpdateLayout();
            Capture(window, Path.Combine(output, "ai-markdown-" + theme + ".png"));
            ((Button)ai.FindName("HistoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            ((TextBox)ai.FindName("HistorySearchBox")).Text = "阅读";
            Check(((ListBox)ai.FindName("HistoryList")).Items.Count == 1, "History popup search failed");
            var popup = (Popup)ai.FindName("HistoryPopup");
            var surface = (FrameworkElement)popup.Child;
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "ai-history-popup-" + theme + ".png"))) png.Save(file);
            popup.IsOpen = false;
        }
        ThemeManager.Apply("ristretto");
        Console.WriteLine("AI rendering: Markdown, immediate/persisted titles, visible long streaming follow-ups, context, cancellation/retry, keyboard routing, history popup and selection contrast passed.");
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
        internal string Answer = MarkdownFixture + "\n\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"段落 {i}：流式长回答中的 **重点** 与 `代码`，可以继续追问。\n\n"));
        internal List<int> HistoryCounts = [];
        internal TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailNextRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            HistoryCounts.Add(json["input"]!.AsArray().Count(n => n?["role"]?.ToString() == "assistant"));
            if (HistoryCounts.Count == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstResponse.Task.WaitAsync(token);
            }
            if (FailNextRequest) { FailNextRequest = false; throw new HttpRequestException("模拟连接失败"); }
            var events = new StringBuilder();
            foreach (var c in Answer) events.Append("data: ").Append(JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = c.ToString() })).Append("\n\n");
            events.Append("data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowAiStream(Encoding.UTF8.GetBytes(events.ToString()))) };
        }
    }
    private sealed class SlowAiStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(3, cancellationToken); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 2048)], cancellationToken); }
    }
}
