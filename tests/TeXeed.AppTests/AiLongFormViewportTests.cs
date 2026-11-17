using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckLongFormViewportAsync(Window owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(Path.Combine(output, "long-form-" + Guid.NewGuid().ToString("N") + ".tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        var before = string.Join("\n\n", Enumerable.Range(0, 80).Select(i => $"前文第 {i} 段：保留原来的阅读位置。"));
        var after = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"后文第 {i} 段：保留原来的阅读位置。"));
        var turn = AiForms.WithSubmission(new AiTurn("练习", before + "\n\n{{textarea:long}}\n\n" + after), null);
        AiForms.SetDraft(turn, "textarea", "long", string.Join("\n", Enumerable.Range(0, 80).Select(i => $"第 {i} 行的长答案")));
        book.Turns.Add(turn);
        var host = new Window { Owner = owner, Width = 900, Height = 680, Content = panel, Topmost = true }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[turn.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var box = Descendants<TextBox>(answer).Single(item => AutomationProperties.GetName(item) == "long");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var events = new List<string>();
            answer.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
                events.Add($"bring {e.OriginalSource?.GetType().Name}/{(e.OriginalSource as FrameworkElement)?.Name} rect={e.TargetRect} handled={e.Handled} offset={scroll.VerticalOffset:F1}")), true);
            scroll.ScrollChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, scroll) && e.VerticalChange != 0) events.Add($"scroll {e.VerticalOffset:F1} change {e.VerticalChange:F1}"); };
            async Task Idle() { await Task.Delay(100); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
            await Idle(); Check(box.ActualHeight > scroll.ViewportHeight, "Long fixture did not exceed the viewport");
            var boxTop = box.TranslatePoint(new Point(), scroll).Y;
            scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + boxTop - 40, 0, scroll.ScrollableHeight)); await Idle();
            host.Activate(); box.Focus(); await Idle();
            Check(box.IsKeyboardFocusWithin, "Cannot focus long form field");
            double CaretY()
            {
                var rect = box.GetRectFromCharacterIndex(box.CaretIndex);
                Check(!rect.IsEmpty, "Long field caret is not laid out");
                return box.TranslatePoint(rect.TopLeft, scroll).Y;
            }
            void Text(string value) => TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, value));
            EditingCommands.MoveToDocumentStart.Execute(null, box); await Idle(); Text("1"); await Idle();
            Console.WriteLine($"Long field start caret={CaretY():F1}, offset={scroll.VerticalOffset:F1}, viewport={scroll.ViewportHeight:F1}");
            Check(box.Text.StartsWith("1", StringComparison.Ordinal) && CaretY() >= 0 && CaretY() < scroll.ViewportHeight, "Top insertion did not keep the caret visible");
            events.Clear(); EditingCommands.MoveToDocumentEnd.Execute(null, box); await Idle();
            Console.WriteLine($"After document-end: caret={CaretY():F1}, offset={scroll.VerticalOffset:F1}, fieldTop={box.TranslatePoint(new Point(), scroll).Y:F1}, boxHeight={box.ActualHeight:F1}, rect={box.GetRectFromCharacterIndex(box.CaretIndex)}");
            Console.WriteLine("Events: " + string.Join(" | ", events));
            Text("2"); await Idle();
            Console.WriteLine($"Long field end caret={CaretY():F1}, offset={scroll.VerticalOffset:F1}, viewport={scroll.ViewportHeight:F1}, fieldTop={box.TranslatePoint(new Point(), scroll).Y:F1}, boxHeight={box.ActualHeight:F1}, rect={box.GetRectFromCharacterIndex(box.CaretIndex)}");
            Check(box.Text.EndsWith("2", StringComparison.Ordinal) && CaretY() >= 0 && CaretY() < scroll.ViewportHeight, "Bottom insertion did not keep the caret visible");
            var previous = scroll.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            box.RaiseEvent(wheel); await Idle();
            Check(scroll.VerticalOffset < previous, "Wheel route did not scroll when a long field was focused");
            EditingCommands.MoveToDocumentStart.Execute(null, box); await Idle();
            Check(CaretY() >= 0 && CaretY() < scroll.ViewportHeight, "Returning to the top did not keep the caret visible");
            Console.WriteLine("PASS: long multiline WPF caret commands, text input and wheel route.");
        }
        finally { host.Close(); }
    }
}
