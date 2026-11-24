using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private static async Task CheckRevisionEditorViewportAsync(MainWindow owner, string output)
    {
        var project = Path.Combine(output, "revision-viewport-" + Guid.NewGuid().ToString("N") + ".tex");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 740, Height = 720, Content = panel };
        host.Show();
        try
        {
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var samples = Enumerable.Range(1, 70).Select(i => $"第 {i} 段　这段话用来核对长回答编辑时的阅读位置。").ToArray();
            var turn = new AiTurn("这份整理有什么变化？", string.Join("\n\n", samples));
            for (var i = 0; i < 40; i++) book.Turns.Add(i == 17 ? turn : new AiTurn($"第 {i} 轮", "邻近的简短回答。"));
            Invoke(panel, "RefreshTranscript");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var history = (ItemsControl)panel.FindName("History");
            async Task Idle()
            {
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            }
            await Idle();
            var virtualizer = Descendants<ChatVirtualizingPanel>(panel).Single();
            var index = history.Items.IndexOf(turn);
            virtualizer.Reveal(index); await Idle();
            var view = Descendants<AiTurnView>(panel).Single(v => v.Turn.Id == turn.Id);
            var answer = view.Answers.Single(); await answer.RenderTask; await Idle();
            const int rowIndex = 43;
            var paragraph = answer.Document.Blocks.OfType<Paragraph>().First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(samples[rowIndex]));
            var point = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + answer.TranslatePoint(point, scroll).Y - 150);
            await Idle();
            var before = answer.TranslatePoint(point, scroll).Y;
            if (before < 20 || before > scroll.ViewportHeight - 20) throw new Exception($"Long editor fixture target was not visible: {before}");
            answer.CaptureContextParagraph(new Point(point.X + 6, point.Y + 8));
            var menu = answer.ContextMenu!; menu.PlacementTarget = answer; menu.IsOpen = true; await Idle();
            var open = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "整理这一轮"));
            menu.IsOpen = false;
            var start = Stopwatch.GetTimestamp(); var allocated = GC.GetAllocatedBytesForCurrentThread();
            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Idle();
            var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var editor = Descendants<TurnRevisionEditor>(panel).Single(v => v.IsVisible);
            var lines = Descendants<RevisionLineHost>(editor).ToArray();
            var initialEditors = Descendants<RevisionLineEditor>(editor).Count();
            var active = lines[rowIndex]; var guard = lines[rowIndex - 1];
            var entered = active.TranslatePoint(new Point(), scroll).Y;
            if (Math.Abs(entered - before) > 4) throw new Exception($"Opening the editor lost the clicked paragraph: {before:F1} -> {entered:F1}");
            active.Focus(); var line = active.Editor!; line.TextArea.Focus(); line.CaretOffset = 4;
            var splitAt = Stopwatch.GetTimestamp(); var splitAllocated = GC.GetAllocatedBytesForCurrentThread();
            line.TextArea.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent));
            await Idle();
            var split = Stopwatch.GetElapsedTime(splitAt).TotalMilliseconds; var splitBytes = GC.GetAllocatedBytesForCurrentThread() - splitAllocated;
            var moved = Descendants<RevisionLineHost>(editor).ToArray();
            if (!ReferenceEquals(moved[rowIndex - 1], guard) || Math.Abs(moved[rowIndex].TranslatePoint(new Point(), scroll).Y - entered) > 4)
                throw new Exception("Splitting a paragraph rebuilt or displaced adjacent long-answer rows");
            var caret = moved[rowIndex + 1].Editor!; caret.TextArea.RaiseEvent(CreateKey(host, Key.Back, Keyboard.PreviewKeyDownEvent)); await Idle();
            if (turn.UserRevisionDraft?.Rows.Count != samples.Length || !ReferenceEquals(guard, Descendants<RevisionLineHost>(editor).ElementAt(rowIndex - 1)))
                throw new Exception("Joining a long-answer paragraph lost a row or rebuilt its neighbor");
            scroll.ScrollToEnd(); await Idle();
            virtualizer.Reveal(index); await Idle();
            if (!Descendants<TurnRevisionEditor>(panel).Any(v => v.IsVisible) || turn.UserRevisionDraft?.Rows.Count != samples.Length)
                throw new Exception("Virtualization lost the active revision draft");
            File.WriteAllText(Path.Combine(output, "revision-editor-long-performance.json"), JsonSerializer.Serialize(new
            {
                turns = 40, editableRows = lines.Length, initialNativeEditors = initialEditors, openMs = duration, openUiAllocatedBytes = bytes, splitMs = split, splitUiAllocatedBytes = splitBytes,
                screenYBefore = before, screenYAfter = entered, neighboringEditorReused = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: right-clicked long-answer row stayed at {before:F1} / {entered:F1} DIP, unchanged editor survived split/merge, and draft survived virtualization");
        }
        finally { host.Close(); }
    }
}
