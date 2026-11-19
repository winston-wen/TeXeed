using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        var project = Path.Combine(output, "long-form-" + Guid.NewGuid().ToString("N") + ".tex");
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var before = string.Join("\n\n", Enumerable.Range(0, 80).Select(i => $"前文第 {i} 段：保留原来的阅读位置。"));
        var after = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"后文第 {i} 段：保留原来的阅读位置。"));
        var turn = AiForms.WithSubmission(new AiTurn("练习", before + "\n\n{{textarea:long}}\n\n" + after), null);
        AiForms.SetDraft(turn, "textarea", "long", string.Join("\r\n", Enumerable.Range(1, 3).Select(i => $"第 {i} 行")));
        book.Turns.Add(turn);
        var offscreen = Environment.GetCommandLineArgs().Contains("--offscreen");
        var host = new Window { Owner = owner, Width = 900, Height = 680, Content = panel, Topmost = !offscreen };
        if (offscreen)
        {
            host.WindowStartupLocation = WindowStartupLocation.Manual;
            host.Left = -12000;
            host.Top = 20;
            host.ShowActivated = false;
            host.ShowInTaskbar = false;
        }
        host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[turn.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout();
            var box = Descendants<TextBox>(answer).Single(item => AutomationProperties.GetName(item) == "long");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var inner = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            var numbers = Descendants<TextBoxLineNumbers>(answer).Single();
            var anchorText = answer.Document.Blocks.OfType<Paragraph>().Last(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("前文第 79 段")).ContentStart;
            double AnchorY() => answer.TranslatePoint(anchorText.GetCharacterRect(LogicalDirection.Forward).TopLeft, scroll).Y;
            double BoxY() => box.TranslatePoint(new Point(), scroll).Y;
            async Task Idle()
            {
                host.UpdateLayout();
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
            }
            async Task PlaceBox(double y)
            {
                for (var attempt = 0; attempt < 3 && Math.Abs(BoxY() - y) > 2; attempt++)
                {
                    scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + BoxY() - y, 0, scroll.ScrollableHeight));
                    await Idle();
                }
                Check(Math.Abs(BoxY() - y) < 2, $"Could not place the field at Y={y:F1}, actual {BoxY():F1}");
            }
            await Idle();
            await PlaceBox(120);
            host.Activate(); box.Focus(); await Idle();
            Check(box.IsKeyboardFocusWithin, "Cannot focus long form field");

            async Task Edit(RoutedCommand command)
            {
                Check(command.CanExecute(null, box), $"Command {command.Name} is unavailable");
                command.Execute(null, box);
                await Idle();
            }
            void Text(string value) => TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, value));
            Rect Caret() => box.GetRectFromCharacterIndex(box.CaretIndex);
            var original = box.Text;
            var initialHeight = box.ActualHeight;
            foreach (var y in new[] { 120, scroll.ViewportHeight - initialHeight - 10, scroll.ViewportHeight - 43 })
            {
                box.Select(0, 0);
                await Idle();
                await PlaceBox(y);
                var anchorY = AnchorY();
                var fieldY = BoxY();
                var beforeOffset = scroll.VerticalOffset;
                await Edit(EditingCommands.EnterParagraphBreak);
                var grown = box.ActualHeight;
                var growthShift = AnchorY() - anchorY;
                var growthBoxShift = BoxY() - fieldY;
                var afterEnterOffset = scroll.VerticalOffset;
                await Edit(EditingCommands.Backspace);
                var shrinkShift = AnchorY() - anchorY;
                var shrinkBoxShift = BoxY() - fieldY;
                Console.WriteLine($"Enter/Backspace: field Y {fieldY:F1}, height {initialHeight:F1} -> {grown:F1} -> {box.ActualHeight:F1}, " +
                    $"anchor shift {growthShift:F1}/{shrinkShift:F1}, field shift {growthBoxShift:F1}/{shrinkBoxShift:F1}, " +
                    $"outer offsets {beforeOffset:F1} -> {afterEnterOffset:F1} -> {scroll.VerticalOffset:F1} / {scroll.ScrollableHeight:F1}.");
                Check(grown > initialHeight + 15 && Math.Abs(box.ActualHeight - initialHeight) < 2 && box.Text == original,
                    "Editing commands did not grow and shrink the actual multiline field.");
                Check(Math.Max(Math.Abs(growthShift), Math.Abs(shrinkShift)) < 2 &&
                    Math.Max(Math.Abs(growthBoxShift), Math.Abs(shrinkBoxShift)) < 2,
                    "Adding or removing a line moved visible conversation content despite a visible caret.");
            }

            await PlaceBox(100);
            await Edit(EditingCommands.MoveToDocumentEnd);
            var heightAtEight = 0d;
            for (var line = 4; line <= 12; line++)
            {
                await Edit(EditingCommands.EnterParagraphBreak);
                Text($"第 {line} 行");
                await Idle();
                Check(box.LineCount == line, $"Expected {line} visual rows, got {box.LineCount}");
                if (line == 8) heightAtEight = box.ActualHeight;
                var bar = Descendants<ScrollBar>(inner).Single(item => item.Orientation == Orientation.Vertical);
                Check(bar.IsVisible == (line > 8) && (inner.ScrollableHeight > 1) == (line > 8),
                    $"Unexpected vertical scrollbar at {line} rows: bar={bar.IsVisible}, range={inner.ScrollableHeight:F1}");
                if (line > 8)
                    Check(Math.Abs(heightAtEight - box.ActualHeight) < 2,
                        $"Field height changed after the eight-row limit: {heightAtEight:F1} -> {box.ActualHeight:F1}");
            }
            var nestedViewport = (FrameworkElement)inner.Template.FindName("PART_ScrollContentPresenter", inner);
            var lineHeight = TextBlock.GetLineHeight(box);
            async Task CheckVisibleCaret(int index, string operation)
            {
                Rect CaretInViewport() => box.TransformToVisual(nestedViewport).TransformBounds(Caret());
                try
                {
                    await WaitUntilAsync(() =>
                    {
                        var rect = CaretInViewport();
                        return box.CaretIndex == index && !rect.IsEmpty && rect.Top >= -2 &&
                            rect.Bottom <= nestedViewport.ActualHeight + 2;
                    }, $"{operation} did not leave the caret visible within the field.");
                }
                finally
                {
                    var rect = CaretInViewport();
                    Console.WriteLine($"{operation}: caret {box.CaretIndex}/{index}, Y {rect.Top:F1}..{rect.Bottom:F1}, " +
                        $"viewport {nestedViewport.ActualHeight:F1}, inner offset {inner.VerticalOffset:F1}.");
                }
            }
            Console.WriteLine($"Overflow viewport: height {nestedViewport.ActualHeight:F1}, line height {lineHeight:F1}.");
            Check(nestedViewport.ActualHeight >= 8 * lineHeight - 1.5 && nestedViewport.ActualHeight <= 8 * lineHeight + 2.5,
                "The capped viewport does not show eight rows of answer text.");
            await CheckVisibleCaret(box.Text.Length, "Typing at the end");
            Check(numbers.PaintedNumbers.LastOrDefault() == 12 && numbers.PaintedNumbers.Count <= 9,
                "Line numbers did not follow the local scrolling viewport.");
            var fieldYAfterTyping = BoxY();
            var textAfterTyping = box.Text;
            var previousLocal = inner.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            box.RaiseEvent(wheel); await Idle();
            Check(inner.VerticalOffset < previousLocal && Math.Abs(BoxY() - fieldYAfterTyping) < 2,
                "Wheel inside the field must scroll it without moving the conversation.");
            inner.ScrollToVerticalOffset(inner.ScrollableHeight / 2);
            await Idle();
            await Edit(EditingCommands.MoveToDocumentStart);
            await CheckVisibleCaret(0, "Moving to the start");
            Check(Math.Abs(BoxY() - fieldYAfterTyping) < 2, "Moving to the start moved the conversation.");
            inner.ScrollToTop();
            await Idle();
            var priorOuter = scroll.VerticalOffset;
            box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Idle();
            Check(inner.VerticalOffset < 2 && scroll.VerticalOffset < priorOuter - 1,
                $"A further upward wheel gesture at the field's top must continue through the outer conversation (local offset {inner.VerticalOffset:F2}, outer {priorOuter:F2}->{scroll.VerticalOffset:F2}).");
            await PlaceBox(fieldYAfterTyping);
            var trackBar = Descendants<ScrollBar>(inner).Single(item => item.Orientation == Orientation.Vertical);
            var track = (Track)trackBar.Template.FindName("PART_Track", trackBar);
            var pageDown = track.IncreaseRepeatButton;
            Check(pageDown.Command is RoutedCommand, "The local scrollbar has no routed page-down command.");
            ((RoutedCommand)pageDown.Command).Execute(pageDown.CommandParameter, pageDown.CommandTarget ?? pageDown);
            await Idle();
            Check(inner.VerticalOffset > 0 && Math.Abs(BoxY() - fieldYAfterTyping) < 2,
                "The actual scrollbar track did not scroll the answer field locally.");
            inner.ScrollToVerticalOffset(inner.ScrollableHeight / 2);
            await Idle();
            await Edit(EditingCommands.MoveToDocumentEnd);
            await CheckVisibleCaret(box.Text.Length, "Moving to the end");
            Check(Math.Abs(BoxY() - fieldYAfterTyping) < 2 && box.Text == textAfterTyping,
                "Moving to the end moved the conversation or changed the answer.");
            Capture(host, Path.Combine(output, "long-form-eight-visible-rows.png"));
            box.Select(original.Length, box.Text.Length - original.Length); await Edit(EditingCommands.Delete);
            Console.WriteLine($"Shrink from overflow: rows {box.LineCount}, local range {inner.ScrollableHeight:F1}, field height {box.ActualHeight:F1}, " +
                $"outer field shift {BoxY() - fieldYAfterTyping:F1}, restored text={box.Text == original}.");
            Check(box.LineCount == 3 && inner.ScrollableHeight < 1 && Math.Abs(box.ActualHeight - initialHeight) < 2 &&
                Math.Abs(BoxY() - fieldYAfterTyping) < 2 && box.Text == original,
                "Shortening the answer did not remove the scrollbar without moving the conversation.");

            fieldYAfterTyping = BoxY();
            box.SelectAll(); Text(string.Concat(Enumerable.Repeat("显示行随宽度改变。", 28)));
            await Idle();
            Console.WriteLine($"Soft-wrap wide: {box.LineCount} visual rows, local range {inner.ScrollableHeight:F1}.");
            Check(numbers.LogicalLineCount == 1 && box.LineCount <= 8 && inner.ScrollableHeight < 1,
                "The soft-wrap fixture must initially fit at the wide width.");
            Check(Math.Abs(BoxY() - fieldYAfterTyping) < 2, "Soft-wrap limited input moved the conversation.");
            var wrapped = box.Text;
            host.Width = 420;
            await Idle();
            await PlaceBox(105);
            Console.WriteLine($"Soft-wrap narrow: {box.LineCount} visual rows, local range {inner.ScrollableHeight:F1}.");
            Check(box.Text == wrapped && numbers.LogicalLineCount == 1 && box.LineCount > 8 && inner.ScrollableHeight > 1 &&
                Math.Abs(heightAtEight - box.ActualHeight) < 2, "Only narrowing a fixed logical line should activate the eight-visible-row limit.");
            fieldYAfterTyping = BoxY();
            box.SelectAll(); Text(string.Concat(Enumerable.Repeat("这个句子在窄窗口自动折行。", 32)));
            await Idle();
            Check(numbers.LogicalLineCount == 1 && box.LineCount > 8 && inner.ScrollableHeight > 1 &&
                Math.Abs(heightAtEight - box.ActualHeight) < 2, "Soft-wrapped visual rows did not activate the eight-row limit.");
            Check(Math.Abs(BoxY() - fieldYAfterTyping) < 2, "Soft-wrap limited input moved the conversation.");
            Capture(host, Path.Combine(output, "long-form-soft-wrap.png"));
            wrapped = box.Text;
            await panel.StopAndSaveAsync();
            var saved = AiStorage.Load(project).Active.Turns.Single(item => item.Id == turn.Id);
            Check(AiForms.Value(AiForms.Draft(saved), "textarea", "long") == wrapped,
                "Saving a clipped field lost part of its content.");
            Console.WriteLine("PASS: multiline growth and shrink keep body positions; eight visible rows cap both explicit and wrapped lines; local caret and wheel work.");
        }
        finally { host.Close(); }
    }
}
