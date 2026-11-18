using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiFormUnlockDifferentAnswersAsync(MainWindow owner, string output)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "unlock-two-answers.tex"));
        var book = (AiNotebook)Field(panel, "_book")!; book.Turns.Clear();
        var a = AiForms.WithSubmission(new AiTurn("第一题", "$x$\n\n{{text:a}}"), null);
        var b = AiForms.WithSubmission(new AiTurn("第二题", "$y$\n\n{{text:b}}"), null);
        book.Turns.Add(a); book.Turns.Add(b);
        var sa = AiForms.Snapshot(a)!; var sb = AiForms.Snapshot(b)!;
        book.Turns.Add(AiForms.WithSubmissions(new AiTurn("继续", "解释"), [sa, sb]));
        AiForms.Lock(book.Active, sa); AiForms.Lock(book.Active, sb);
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000 };
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var av = views[a.Id].Answers.Single(); var bv = views[b.Id].Answers.Single(); await Task.WhenAll(av.RenderTask, bv.RenderTask); host.UpdateLayout();
            var af = Descendants<ChatFormula>(av).Single(); var bf = Descendants<ChatFormula>(bv).Single(); await Task.WhenAll(af.RenderTask, bf.RenderTask);
            var taskField = typeof(ChatFormula).GetField("<RenderTask>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ai = Descendants<FormFeedbackHost>(av).Single(); var bi = Descendants<FormFeedbackHost>(bv).Single();
            async Task Open(FormFeedbackHost field)
            {
                field.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            TaskCompletionSource Pending(ChatFormula formula)
            {
                var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); taskField.SetValue(formula, task.Task); return task;
            }
            var aPending = Pending(af); await Open(ai); var button = (Button)Field(panel, "_formUnlockButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is not null, "First answer did not start waiting");
            await Open(bi);
            var popup = (Popup)Field(panel, "_formUnlockPopup")!;
            Require(popup.IsOpen && !popup.StaysOpen && (string)button.Content == "解锁本轮题目", "Switching answers left an uncancellable waiting popup");
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Switching answers did not cancel first wait");
            Require(AiForms.SubmittedFor(book.Active, a.Id) is not null && AiForms.SubmittedFor(book.Active, b.Id) is not null, "Switching answers changed a locked form");
            aPending.TrySetResult();

            var bPending = Pending(bf); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is not null, "Second answer did not begin waiting");
            Require(popup.StaysOpen && (string)button.Content == "取消", "Second wait lost its cancel entry");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Second wait did not cancel"); bPending.TrySetResult();
            Require(AiForms.SubmittedFor(book.Active, a.Id) is not null && AiForms.SubmittedFor(book.Active, b.Id) is not null, "Cancelling waits changed stored answers");
            Console.WriteLine("PASS unlock waits: switching between answers with and without pending formulas.");
        }
        finally { host.Close(); }
    }

    private static async Task CheckAiFormUnlockViewportAsync(MainWindow owner, string output)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        var project = Path.Combine(output, "unlock-viewport.tex");
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!; book.Turns.Clear();
        var before = string.Join("\n\n", Enumerable.Range(0, 25).Select(i => $"前文 {i}：继续阅读。"));
        var after = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"后文 {i}：还有内容。"));
        var source = AiForms.WithSubmission(new AiTurn("练习", before + "\n\n$z^2$\n\n| 项 | 答案 |\n|---|---|\n| 一 | {{text:first}} |\n| 二 | {{text:second}} |\n\n" + after), null);
        book.Turns.Add(source); AiForms.SetDraft(source, "text", "first", "保留答案");
        var snapshot = AiForms.Snapshot(source)!;
        var sent = AiForms.WithSubmissions(new AiTurn("先问一个问题", "继续解释。"), [snapshot]); book.Turns.Add(sent); AiForms.Lock(book.Active, snapshot);
        for (var i = 0; i < 12; i++) book.Turns.Add(new AiTurn("后续问题", "后续回答。"));
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000 };
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var formula = Descendants<ChatFormula>(answer).First(); await formula.RenderTask;
            var taskField = typeof(ChatFormula).GetField("<RenderTask>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = Descendants<FormFeedbackHost>(answer).Single(item => item.FieldId == "first");
            var second = Descendants<FormFeedbackHost>(answer).Single(item => item.FieldId == "second");
            foreach (var theme in new[] { "github-light-default", "ristretto" })
            {
                TeXeed.App.Services.ThemeManager.Apply(theme); host.UpdateLayout();
                Require(first.Field.FontFamily.Source == EditorTypography.DefaultFamily.Source && first.Field.FontWeight == EditorTypography.DefaultWeight,
                    "Locked form input must use its theme's editing font");
                Require(answer.FontFamily.Source != EditorTypography.DefaultFamily.Source, "Editing typography changed read-only answer text");
            }
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var fieldY = first.TranslatePoint(new Point(), scroll).Y;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + fieldY - 180); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Require(first.TranslatePoint(new Point(), scroll).Y is >= 0 and < 400, "Exercise did not enter viewport");
            async Task Open(FormFeedbackHost field)
            {
                field.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(((Popup)Field(panel, "_formUnlockPopup")!).IsOpen, "Unlock popup did not open");
            }
            async Task Begin(FormFeedbackHost field)
            {
                await Open(field);
                ((Button)Field(panel, "_formUnlockButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is not null, "Pending formula did not start a cancellable wait");
                Require(((TextBlock)Field(panel, "_formUnlockLabel")!).Text == "正在排版，稍后解锁" && (string)((Button)Field(panel, "_formUnlockButton")!).Content == "取消", "Waiting UI does not explain cancellation");
                Require(AiForms.SubmittedFor(book.Active, source.Id) is not null, "Pending formula modified the locked form");
            }
            TaskCompletionSource NewPending()
            {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                taskField.SetValue(formula, pending.Task); return pending;
            }
            var pending = NewPending(); await Begin(first);
            var sameWait = Field(panel, "_formUnlockWait"); await Open(second);
            Require(ReferenceEquals(sameWait, Field(panel, "_formUnlockWait")), "Moving between fields canceled the same form's pending unlock");
            ((Button)Field(panel, "_formUnlockButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Cancel did not stop pending unlock");
            Require(AiForms.SubmittedFor(book.Active, source.Id) is not null && ((TextBox)first.Field).IsReadOnly, "Cancellation changed the form");
            pending.TrySetResult();

            pending = NewPending(); await Begin(first);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 50); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Require(Field(panel, "_formUnlockWait") is not null, "Scrolling canceled the pending unlock");
            var position = first.TranslatePoint(new Point(), scroll).Y; var frames = new List<double>();
            EventHandler render = (_, _) => { if (first.IsLoaded) frames.Add(first.TranslatePoint(new Point(), scroll).Y); };
            CompositionTarget.Rendering += render;
            pending.TrySetResult();
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null && !(bool)Field(panel, "_formUnlockSaving")!, "Completed formula did not unlock");
            await Task.Delay(300); host.UpdateLayout(); CompositionTarget.Rendering -= render;
            Require(!((TextBox)first.Field).IsReadOnly && AiForms.Value(AiForms.Draft(source), "text", "first") == "保留答案", "Unlock lost the answer or remained locked");
            Require(Math.Abs(first.TranslatePoint(new Point(), scroll).Y - position) < 2 && frames.All(y => Math.Abs(y - position) < 2), "Unlock moved the user's new reading position");

            AiForms.Lock(book.Active, snapshot); Invoke(panel, "RefreshFormEnabled"); host.UpdateLayout();
            pending = NewPending(); await Begin(first);
            pending.TrySetException(new InvalidOperationException("测试公式失败"));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Failed formula did not end pending unlock");
            Require(AiForms.SubmittedFor(book.Active, source.Id) is not null, "Failed formula unlocked the form");

            var markdownGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var markdownTaskField = typeof(MarkdownView).GetField("<RenderTask>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            markdownTaskField.SetValue(answer, markdownGate.Task); taskField.SetValue(formula, Task.CompletedTask);
            await Begin(first);
            pending = NewPending(); markdownGate.TrySetResult();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(Field(panel, "_formUnlockWait") is not null, "Newly created formulas were omitted after Markdown rendering");
            pending.TrySetResult();
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null && !(bool)Field(panel, "_formUnlockSaving")!, "Newly created formula did not finish unlocking");
            Require(!((TextBox)first.Field).IsReadOnly, "Form remained locked after pending Markdown and formula");
            AiForms.Lock(book.Active, snapshot); Invoke(panel, "RefreshFormEnabled"); host.UpdateLayout();

            pending = NewPending(); await Begin(first);
            first.Field.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Unloading the clicked field did not cancel waiting");
            Require(AiForms.SubmittedFor(book.Active, source.Id) is not null, "Unloading the clicked field unlocked the form");
            pending.TrySetResult();

            pending = NewPending(); await Begin(first);
            ((TextBox)panel.FindName("Composer")).Text = "其他问题";
            var send = panel.SubmitAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => Field(panel, "_formUnlockWait") is null, "Sending did not cancel pending unlock");
            Require(((TextBox)first.Field).IsReadOnly, "Sending while waiting changed the locked form");
            handler.Release.TrySetResult(); await send.WaitAsync(TimeSpan.FromSeconds(20)); pending.TrySetResult();
            Console.WriteLine("PASS unlock viewport: cancellable formula wait, same-form target, scrolling, failure, sending and retained answers.");
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
    }
}
