using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class FormHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token); Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"已检查\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static async Task ShowAiFormsManualAsync(MainWindow owner, string output)
    {
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(Path.Combine(output, "manual-form-fixture.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Clear();
        book.Turns.Add(AiForms.WithSubmission(new AiTurn("给我出几道可以填写的题。", "# 练习\n\n| 题号 | 输入 y | a | b | 输出 c |\n|---|---|---|---|---|\n| A | 83 | {{text:A-a}} | {{text:A-b}} | {{text:A-c}} |\n| B | 84 | {{text:B-a}} | {{text:B-b}} | {{text:B-c}} |\n\n解释：\n\n{{textarea:reason}}\n\n独占一行的单行框：\n\n{{text:full}}\n\n{{radio:choice:yes:进一格}} {{radio:choice:no:没有进一格}}\n\n{{checkbox:check:先取模}} {{checkbox:next:再取整}}"), null));
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Title = "TeXeed 表单手动测试", Width = 1000, Height = 800, Content = panel };
        host.Closed += (_, _) => { panel.Dispose(); Application.Current.Shutdown(); };
        owner.Hide(); host.Show(); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
    }
    private static async Task CheckAiFormsAsync(MainWindow owner, string output)
    {
        const string markdown = "# 填空\n\n😀\n\n| 题号 | 输入 | a | b | c |\n|---|---|---|---|---|\n| A | 83 | {{text:A-a}} | {{text:A-b}} | {{text:A-c}} {{radio:choice:yes:是}} |\n\n解释：👩🏽‍💻\n\n{{textarea:reason}}\n\n{{text:full}}\n\n句中：{{textarea:inline}} 后续文字。\n\n{{radio:choice:no:否}}\n\n{{checkbox:check:取模}}\n\n`{{text:code}}`\n\n```\n{{text:fenced}}\n```";
        var legacy = new AiTurn("旧", markdown);
        if (AiForms.Snapshot(legacy) is not null) throw new Exception("Legacy answers must not gain form semantics");
        var source = AiForms.WithSubmission(new AiTurn("练习", markdown), null);
        var initial = AiForms.Snapshot(source) ?? throw new Exception("No form controls discovered");
        if (initial.Values.Count != 8 || initial.Values.ContainsKey("code") || initial.Values.ContainsKey("fenced")) throw new Exception("Code or malformed markup became a form");
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FormHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, "forms-fixture.tex"); await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!; book.Turns.Add(source);
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var copied = answer.CopySource(answer.Document.ContentStart, answer.Document.ContentEnd);
            if (!copied.Contains("😀") || !copied.Contains("👩🏽‍💻") || !copied.Contains("{{text:A-a}}")) throw new Exception("Form and emoji copy did not preserve source Unicode");
            var fields = Descendants<TextBox>(answer).ToArray();
            var a = fields.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "A-a");
            var reason = fields.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "reason");
            var full = fields.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "full");
            var inline = fields.Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "inline");
            if (inline.AcceptsReturn || a.AcceptsReturn || !reason.AcceptsReturn || reason.MinLines != 3 || reason.VerticalContentAlignment != VerticalAlignment.Top ||
                reason.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled || reason.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled || full.ActualWidth < 300)
                throw new Exception("Form input mode, full width, three-line defaults, top alignment or no-scroll layout is wrong");
            var numbers = Descendants<TextBoxLineNumbers>(answer).Single();
            if (numbers.MinimumLineCount != 3) throw new Exception("Multiline field did not reserve three line numbers");
            reason.BringIntoView(); host.UpdateLayout();
            await Task.Delay(100); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Capture(host, Path.Combine(output, "ai-forms-editable.png"));
            if (!numbers.PaintedNumbers.Contains(1) || !numbers.PaintedNumbers.Contains(2) || !numbers.PaintedNumbers.Contains(3))
                throw new Exception("The empty multiline field did not visibly draw line numbers 1, 2, 3");
            host.Width = 360; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var tableScroll = Descendants<ScrollViewer>(answer).Single(s => s.Content is MarkdownView nested && Descendants<TextBox>(nested).Contains(a));
            if (tableScroll.ScrollableWidth <= 0 || a.ActualWidth < 100) throw new Exception("A narrow table clipped its input instead of scrolling");
            host.Width = 900; host.UpdateLayout();
            a.Focus(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (!a.IsKeyboardFocusWithin || a.IsReadOnly) throw new Exception("The table field cannot receive keyboard focus or inherited readonly state");
            var input = new TextComposition(InputManager.Current, a, "83");
            TextCompositionManager.StartComposition(input);
            if (a.Text != "83") throw new Exception("WPF text input did not reach the form field");
            reason.Focus();
            reason.Text = "跨越边界";
            if (!reason.IsKeyboardFocusWithin || !reason.AcceptsReturn) throw new Exception("The multiline field cannot receive keyboard focus or line breaks");
            var initialHeight = reason.ActualHeight; reason.Text = "一\n二\n三\n四\n五\n六"; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (reason.ActualHeight <= initialHeight) throw new Exception("Multiline field did not expand to fit six lines");
            reason.BringIntoView(); host.UpdateLayout(); Capture(host, Path.Combine(output, "ai-forms-expanded.png"));
            var longText = new string('长', 5000); reason.Text = longText; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (reason.Text.Length != longText.Length || reason.MaxLength != 0) throw new Exception("Long input was silently truncated");
            reason.Text = new string('长', 120) + "\n第二行"; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var wideHeight = reason.ActualHeight;
            host.Width = 360; host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (reason.ActualHeight <= wideHeight || reason.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled || reason.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                throw new Exception("Wrapped text did not expand when the window narrowed, or introduced a scrollbar");
            host.Width = 900; host.UpdateLayout();
            reason.Text = "跨越边界";
            var radios = Descendants<RadioButton>(answer).ToArray(); var yes = radios.Single(r => Equals(r.Content, "是")); var no = radios.Single(r => Equals(r.Content, "否"));
            yes.IsChecked = true; no.IsChecked = true;
            var check = Descendants<CheckBox>(answer).Single(c => Equals(c.Content, "取模")); check.IsChecked = true;
            if (yes.IsChecked == true || AiForms.Value(AiForms.Draft(source), "radio", "choice") != "no") throw new Exception("Radio group did not persist the selected option");
            await panel.SaveAsync();
            var saved = AiStorage.Load(project).Active.Turns.Single();
            if (AiForms.Value(AiForms.Draft(saved), "textarea", "reason") != "跨越边界") throw new Exception("Unsent form draft was not saved");
            var composer = (TextBox)panel.FindName("Composer"); composer.Text = "";
            var originalHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
            var blockedHome = Path.Combine(output, "blocked-home"); File.WriteAllText(blockedHome, "not a directory");
            try { Environment.SetEnvironmentVariable("TEXEED_HOME", blockedHome); await panel.SubmitAsync(); }
            finally { Environment.SetEnvironmentVariable("TEXEED_HOME", originalHome); }
            if (!a.IsEnabled || a.IsReadOnly || a.Text != "83" || AiForms.Value(AiForms.Draft(source), "textarea", "reason") != "跨越边界" || handler.Entered.Task.IsCompleted)
                throw new Exception("A failed checkpoint discarded answers or left controls locked");
            var send = panel.SubmitAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!a.IsEnabled || !a.IsReadOnly || !reason.IsEnabled || !reason.IsReadOnly || no.IsEnabled || check.IsEnabled) throw new Exception("Submitted text must remain selectable and choices must be locked");
            a.SelectAll(); if (a.SelectedText != "83") throw new Exception("Frozen text cannot be selected for copying");
            var pendingPath = AiTurnCheckpoint.PathsFor(project).Distinct(StringComparer.OrdinalIgnoreCase).Single();
            var pending = JsonSerializer.Deserialize<AiTurnCheckpoint.Pending>(File.ReadAllText(pendingPath))!;
            var submitted = AiForms.FromTurn(pending.Turn) ?? throw new Exception("Crash journal did not retain form snapshot");
            if (AiForms.Value(submitted.Values, "text", "A-a", false) != "83" || AiForms.Value(submitted.Values, "textarea", "reason", false) != "跨越边界" || AiForms.Value(submitted.Values, "radio", "choice", false) != "no" || AiForms.Value(submitted.Values, "checkbox", "check", false) != "true") throw new Exception("Submission lost values");
            if (!handler.Body.Contains("A-a") || !handler.Body.Contains("83")) throw new Exception("Actual model request omitted form values");
            var recovered = AiStorage.Load(project).Active;
            if (AiForms.Value(AiForms.SubmittedFor(recovered, source.Id)?.Values ?? [], "textarea", "reason", false) != "跨越边界") throw new Exception("Crash recovery lost the frozen values");
            Capture(host, Path.Combine(output, "ai-forms-submitted.png"));
            handler.Release.TrySetResult(); await send.WaitAsync(TimeSpan.FromSeconds(20)); await panel.StopAndSaveAsync();
            var stored = AiStorage.Load(project).Active;
            var snapshot = AiForms.SubmittedFor(stored, source.Id) ?? throw new Exception("Completed message lost form data");
            if (AiForms.Value(snapshot.Values, "text", "A-a", false) != "83" || !AiForms.HistoryQuestion(stored.Turns.Last()).Contains("83")) throw new Exception("History lost submitted form answers");
            Console.WriteLine("PASS: WPF form controls, radio groups, draft, empty-composer submit, request, checkpoint, recovery, lock and history.");
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
    }
}
