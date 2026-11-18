using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAnswerAnnotationModesAsync(MainWindow owner, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var directory = Path.Combine(output, "answer-marking-project"); Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "main.tex"); File.WriteAllText(project, "Synthetic project");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!; book.Entries.Clear(); book.Turns.Clear();
        const string markdown = "First paragraph for selection.\n\nSecond paragraph for other selection.";
        book.Turns.Add(new AiTurn("Question", markdown));
        var host = new Window { Owner = owner, Content = panel, Width = 720, Height = 720,
            Left = -3000, Top = -3000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var answer = Descendants<MarkdownView>(panel).Single(view => view.Markdown == markdown);
            var paragraphs = answer.Document.Blocks.OfType<Paragraph>().ToArray();
            Check(paragraphs.Length >= 2, "Answer fixture did not render two paragraphs.");
            var first = paragraphs[0].Inlines.OfType<Run>().First();
            var second = paragraphs[1].Inlines.OfType<Run>().First();
            var menu = answer.ContextMenu ?? throw new Exception("Answer menu is unavailable.");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Check(!items.Any(item => Equals(item.Header, "引用到当前会话")), "Obsolete separate quote action remains.");
            var annotate = items.Single(item => Equals(item.Header, "批注到当前会话"));
            answer.Selection.Select(first.ContentStart, first.ContentStart);
            annotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check((bool)Field(panel, "_answerMarking")! && book.Entries.Count == 0, "Annotation-first did not enter selection mode without creating a point.");
            var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.QueryCursorEvent };
            answer.RaiseEvent(cursor); Check(cursor.Cursor == Cursors.Pen, "Annotation mode has no dedicated cursor.");
            answer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(book.Entries.Count == 0, "A point click created an annotation.");
            answer.Selection.Select(first.ContentStart, first.ContentStart.GetPositionAtOffset(5)!);
            answer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(book.Entries.Count == 1 && book.Entries[0].AnswerAnchor?.Quote == "First", "Selecting a region in annotation mode did not create the correct anchor.");
            var layers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(panel, "_answerLayers")!;
            var popup = (System.Windows.Controls.Primitives.Popup)Field(layers[answer], "_popup")!;
            Check(popup.IsOpen, "Selecting the answer did not open the local annotation.");
            Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationConfirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!popup.IsOpen && book.Entries[0].Question == "" && book.Entries[0].ReferenceSelected, "An empty annotation was not saved as a selected reference.");
            panel.CancelAnswerMarking();
            answer.Selection.Select(first.ContentStart, first.ContentStart.GetPositionAtOffset(5)!);
            var inside = first.ContentStart.GetPositionAtOffset(2)!.GetCharacterRect(LogicalDirection.Forward);
            answer.PrepareContextSelection(new Point(inside.Left + inside.Width / 2, inside.Top + inside.Height / 2));
            Check(answer.HasBookmarkSelection, "Right click inside a selection cleared it.");
            annotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); host.UpdateLayout();
            Check(book.Entries.Count == 2 && book.Entries[1].AnswerAnchor?.Quote == "First", "Selection-first annotation lost its selection.");
            popup.IsOpen = false;
            answer.Selection.Select(first.ContentStart, first.ContentStart.GetPositionAtOffset(5)!);
            var sameParagraph = first.ContentStart.GetPositionAtOffset(15)!.GetCharacterRect(LogicalDirection.Forward);
            answer.PrepareContextSelection(new Point(sameParagraph.Left + sameParagraph.Width / 2, sameParagraph.Top + sameParagraph.Height / 2));
            Check(!answer.HasBookmarkSelection, "Right click outside selected characters in the same paragraph reused the selection.");
            answer.Selection.Select(first.ContentStart, first.ContentStart.GetPositionAtOffset(5)!);
            var outside = second.ContentStart.GetPositionAtOffset(2)!.GetCharacterRect(LogicalDirection.Forward);
            answer.PrepareContextSelection(new Point(outside.Left + outside.Width / 2, outside.Top + outside.Height / 2));
            Check(!answer.HasBookmarkSelection, "Right click in another paragraph reused a stale selection.");
            annotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check((bool)Field(panel, "_answerMarking")! && book.Entries.Count == 2, "Right clicking another paragraph created an annotation without a new region.");
            panel.CancelAnswerMarking();
            var status = (TextBlock)panel.FindName("StatusText");
            Check(status.Text == "已退出回答批注模式。" && status.Visibility == Visibility.Visible, "Exiting annotation mode did not show a brief hint.");
            var timer = (DispatcherTimer?)Field(panel, "_answerExitHintTimer");
            Check(timer is { IsEnabled: true } && timer.Interval == TimeSpan.FromSeconds(3), "Exit hint did not schedule its three-second timeout.");
            await Task.Delay(3250); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            Check(status.Text == "" && status.Visibility == Visibility.Collapsed && status.ActualHeight == 0 && Field(panel, "_answerExitHintTimer") is null,
                "Exit hint did not disappear and release its layout space.");
            panel.SetAnswerMarking(true); panel.CancelAnswerMarking();
            timer = (DispatcherTimer?)Field(panel, "_answerExitHintTimer"); timer!.Interval = TimeSpan.FromMilliseconds(120);
            status.Text = "新的错误提示";
            Check(Field(panel, "_answerExitHintTimer") is null, "A new status did not cancel the pending exit hint.");
            await Task.Delay(180); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(status.Text == "新的错误提示", "Exit timeout cleared a newer status.");
            panel.SetAnswerMarking(true); status.Text = "圈点期间出现错误"; panel.CancelAnswerMarking();
            Check(status.Text == "圈点期间出现错误" && Field(panel, "_answerExitHintTimer") is null,
                "Exiting annotation mode overwrote an existing error status.");
            panel.SetAnswerMarking(true); panel.CancelAnswerMarking();
            timer = (DispatcherTimer?)Field(panel, "_answerExitHintTimer"); timer!.Interval = TimeSpan.FromMilliseconds(220);
            await Task.Delay(100);
            panel.SetAnswerMarking(true); panel.CancelAnswerMarking();
            timer = (DispatcherTimer?)Field(panel, "_answerExitHintTimer"); timer!.Interval = TimeSpan.FromMilliseconds(220);
            await Task.Delay(140); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(status.Text == "已退出回答批注模式。", "An earlier timeout cleared a newer exit hint.");
            await Task.Delay(130); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(status.Text == "" && status.Visibility == Visibility.Collapsed, "Repeated exit hint did not expire.");
            CheckAiContextBudget(Check);
            Capture(host, Path.Combine(output, "answer-annotation-offscreen.png"));
            panel.SetAnswerMarking(true); panel.CancelAnswerMarking();
            host.Content = null; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Field(panel, "_answerExitHintTimer") is null && status.Text == "", "Unloading retained the exit hint or timer.");
            Console.WriteLine("PASS: answer selection-first and annotation-first, no points, cursor, empty references, stale selection, transient exit hint and all three reference serializers.");
        }
        finally { panel.CancelAnswerMarking(); host.Close(); }
    }
}
