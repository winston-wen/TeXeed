using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiFormUnlockAsync(MainWindow owner, string output)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        var project = Path.Combine(output, "form-unlock-fixture.tex");
        using var handler = new FormHandler(); using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!; book.Turns.Clear();
        var source = AiForms.WithSubmission(new AiTurn("题目", "| 题 | 输入 |\n|---|---|\n| 1 | {{text:answer}} |\n\n{{textarea:reason}}\n\n{{radio:choice:yes:是}}\n\n{{checkbox:check:选择}}\n\n{{file:file:上传资料}}"), null);
        book.Turns.Add(source);
        var old = AiForms.Snapshot(source)!;
        var sent = AiForms.WithSubmissions(new AiTurn("先问一下", "解释"), [old]); book.Turns.Add(sent); AiForms.Lock(book.Active, old);
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var host = new Window { Owner = owner, Width = 900, Height = 700, Content = panel, ShowActivated = false }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var fields = Descendants<FormFeedbackHost>(answer).ToArray();
            Require(fields.Length == 5, "Missing form controls in test fixture");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll"); var before = scroll.VerticalOffset;
            foreach (var field in fields)
            {
                field.BringIntoView(); host.UpdateLayout(); var viewport = scroll.VerticalOffset;
                var hit = field.InputHitTest(new Point(Math.Max(1, field.ActualWidth / 2), Math.Max(1, field.ActualHeight / 2))) as DependencyObject;
                Require(hit is not null, "Locked field cannot be hit by the mouse: " + field.FieldType);
                field.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var popup = (Popup?)Field(panel, "_formUnlockPopup");
                Require(popup is { IsOpen: true } && ReferenceEquals(popup.PlacementTarget, field.Field), "Locked field did not show its own unlock entry: " + field.FieldType);
                Require(Math.Abs(scroll.VerticalOffset - viewport) < 1, "Showing unlock entry changed reading position");
                Require(field.Field is TextBox { IsReadOnly: true } || field.Field is not TextBox && !field.Field.IsEnabled, "Field became editable before unlocking");
                popup!.IsOpen = false;
            }
            var choice = fields.Single(field => field.FieldType == "checkbox");
            Require(choice.Focusable, "Locked choice lacks keyboard focus target");
            var keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            choice.RaiseEvent(keyboard); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var unlockPopup = (Popup)Field(panel, "_formUnlockPopup")!; var unlock = (Button)Field(panel, "_formUnlockButton")!;
            Require(keyboard.Handled && unlockPopup.IsOpen && unlock.Focusable, "Keyboard could not reach unlock entry from locked choice (handled=" + keyboard.Handled + ", open=" + unlockPopup.IsOpen + ")");
            var popupChild = (FrameworkElement)unlockPopup.Child; popupChild.UpdateLayout();
            var visual = new RenderTargetBitmap((int)Math.Ceiling(popupChild.ActualWidth), (int)Math.Ceiling(popupChild.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            visual.Render(popupChild); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visual));
            using (var image = File.Create(Path.Combine(output, "form-unlock-popup.png"))) encoder.Save(image);
            var originalHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
            var blockedHome = Path.Combine(output, "unlock-blocked-home"); File.WriteAllText(blockedHome, "not a directory");
            try
            {
                Environment.SetEnvironmentVariable("TEXEED_HOME", blockedHome);
                unlock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => !(bool)Field(panel, "_formUnlockSaving")!, "Failed unlock save did not finish");
            }
            finally { Environment.SetEnvironmentVariable("TEXEED_HOME", originalHome); }
            Require(AiForms.SubmittedFor(book.Active, source.Id) is not null && ((TextBox)fields[0].Field).IsReadOnly, "Failed save unlocked the form");
            var queued = AiFollowUpQueue.Load(book.Active); queued.Enqueue("queue", "稍后再问", []); queued.Store(book.Active);
            choice.RaiseEvent(keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            unlock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !(bool)Field(panel, "_formUnlockSaving")!, "Unlock save did not finish");
            Require(AiForms.SubmittedFor(book.Active, source.Id) is null && !((TextBox)fields[0].Field).IsReadOnly, "Unlock did not reopen controls");
            Require(AiFollowUpQueue.Load(book.Active) is { IsPaused: true, Items.Count: 1 } && !handler.Entered.Task.IsCompleted, "Unlock dispatched an existing queue before the user could fill the form");
            Require(AiForms.SubmittedFor(AiStorage.Load(project).Active, source.Id) is null, "Unlock did not persist");
            var text = (TextBox)fields[0].Field; text.Text = "42";
            ((TextBox)panel.FindName("Composer")).Text = "检查";
            var send = panel.SubmitAsync(); await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Require(text.IsReadOnly && AiForms.Value(AiForms.Draft(source), "text", "answer") == "42", "Resubmission failed to relock or lost the answer");
            fields[0].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(unlockPopup.IsOpen && !unlock.IsEnabled, "Unlock was available while submission was running");
            handler.Release.TrySetResult(); await send.WaitAsync(TimeSpan.FromSeconds(20)); await panel.StopAndSaveAsync();
            var saved = AiStorage.Load(project).Active;
            Require(AiForms.SubmittedFor(saved, source.Id)?.Values.GetValueOrDefault("text:answer") == "42" && AiForms.AllFromTurn(saved.Turns[1]).Count == 1,
                "Reload lost the new lock or original historical snapshot");
            Console.WriteLine("PASS manual form unlock: all controls, keyboard, save failure, reopen, resubmit and history.");
        }
        finally { handler.Release.TrySetResult(); host.Close(); }
    }
}
