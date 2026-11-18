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
    private static async Task CheckAiTableViewportAsync(Window owner, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var project = Path.Combine(output, "multiple-table-viewport-" + Guid.NewGuid().ToString("N") + ".tex");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler())); await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        string Section(int i) => string.Join("\n\n", Enumerable.Range(0, 35).Select(n => $"第 {i} 组说明 {n}：这里是用于定位的普通正文。")) +
            $"\n\n| 项目 | 公式 | 链接 | 输入 | 空白 |\n| --- | --- | --- | --- | --- |\n| 第{i}表文字 | $x+{i}$ | [第{i}表链接](#missing) | {{{{text:field{i}}}}} |  |";
        var turn = AiForms.WithSubmission(new AiTurn("测试", string.Join("\n\n", Enumerable.Range(1, 3).Select(Section)) + "\n\n" + new string('后', 2000)), null);
        book.Turns.Add(turn);
        var host = new Window { Owner = owner, Width = 900, Height = 680, Content = panel, Topmost = true }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var answers = ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!)[turn.Id].Answers;
            var answer = answers.Single(); await answer.RenderTask; host.UpdateLayout(); await AwaitMathAsync(answer); host.UpdateLayout();
            var outer = (ScrollViewer)panel.FindName("MessagesScroll");
            var tables = Descendants<ScrollViewer>(answer).Where(scroll => scroll.Content is MarkdownView)
                .OrderBy(scroll => scroll.TranslatePoint(new Point(), answer).Y).ToArray();
            Check(tables.Length == 3, "Expected three independent table viewports");
            var events = new List<string>();
            void Trace(FrameworkElement node, string label) => node.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) => events.Add($"{label}: source={e.OriginalSource?.GetType().Name}/{(e.OriginalSource as FrameworkElement)?.Name}, target={e.TargetObject.GetType().Name}/{(e.TargetObject as FrameworkElement)?.Name}, rect={e.TargetRect}, handled={e.Handled}, focus={Keyboard.FocusedElement?.GetType().Name}, offset={outer.VerticalOffset:F1}")), true);
            Trace(answer, "answer"); Trace(outer, "outer");
            foreach (var table in tables) { Trace(table, "table-scroll"); Trace((MarkdownView)table.Content, "table-view"); }
            async Task Idle() { await Task.Delay(100); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
            async Task Place(ScrollViewer table)
            {
                host.UpdateLayout(); var y = table.TranslatePoint(new Point(), outer).Y;
                outer.ScrollToVerticalOffset(Math.Clamp(outer.VerticalOffset + y - outer.ViewportHeight / 2 + table.ActualHeight / 2, 0, outer.ScrollableHeight));
                await Idle();
                Check(table.TranslatePoint(new Point(), outer).Y is var now && now >= -table.ActualHeight && now < outer.ViewportHeight, "Table could not be placed in the viewport");
            }
            async Task Click(ScrollViewer table, FrameworkElement target, Point local, string label)
            {
                await Place(table); events.Clear(); var before = table.TranslatePoint(new Point(), outer).Y; var offset = outer.VerticalOffset;
                host.Activate(); var point = target.PointToScreen(local);
                var placed = false;
                for (var attempt = 0; attempt < 5 && !placed; attempt++)
                {
                    Check(SetCursorPos((int)point.X, (int)point.Y), "Cannot position physical pointer"); await Task.Delay(15);
                    GetCursorPos(out var cursor); placed = Math.Abs(cursor.X - point.X) < 4 && Math.Abs(cursor.Y - point.Y) < 4;
                }
                Check(placed, "Pointer was moved externally before the test click");
                Console.WriteLine($"Physical target={point}, over={Mouse.DirectlyOver?.GetType().Name}, hostActive={host.IsActive}");
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); await Idle();
                var after = table.TranslatePoint(new Point(), outer).Y;
                Console.WriteLine($"Table click {label}: tableY {before:F1}->{after:F1}, offset {offset:F1}->{outer.VerticalOffset:F1}, focus={Keyboard.FocusedElement?.GetType().Name}; {string.Join(" | ", events)}");
                Check(Math.Abs(after - before) < 3, "Clicking " + label + " moved the answer viewport");
            }
            async Task ClickText(int tableIndex)
            {
                var table = tables[tableIndex]; var nested = (MarkdownView)table.Content;
                var native = (Table)nested.Document.Blocks.FirstBlock!;
                var cell = native.RowGroups[0].Rows[1].Cells[0]; var paragraph = (Paragraph)cell.Blocks.FirstBlock!;
                var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!;
                var end = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!;
                table.ScrollToHorizontalOffset(0); host.UpdateLayout();
                var rect = start.GetCharacterRect(LogicalDirection.Forward);
                Check(!rect.IsEmpty, "Table text had no character layout");
                await Click(table, nested, new Point(rect.X + Math.Min(rect.Width / 2, 4), rect.Y + rect.Height / 2), $"table {tableIndex + 1} text");
                Check(nested.IsKeyboardFocusWithin, "Click did not focus table text");
                nested.Selection.Select(start, end);
                var quote = nested.CopyForClipboard(start, end).Trim();
                Check(quote == $"第{tableIndex + 1}表文字" && answer.GetAnnotationSelection().Quote.Trim() == quote, "Table selection or copied quote changed");
            }
            var second = tables[1]; var tableView = (MarkdownView)second.Content;
            if (Environment.GetCommandLineArgs().Contains("--table-keyboard-baseline"))
            {
                var paragraph = (Paragraph)((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[0].Blocks.FirstBlock!;
                await Place(second); tableView.Focus();
                tableView.Selection.Select(paragraph.ContentStart, paragraph.ContentStart); await Idle(); await Place(second);
                outer.ScrollToVerticalOffset(outer.VerticalOffset + outer.ViewportHeight); await Idle();
                Check(second.TranslatePoint(new Point(), outer).Y < -second.ActualHeight, "Test did not scroll the focused table out of view");
                events.Clear(); keybd_event(0x27, 0, 0, UIntPtr.Zero); keybd_event(0x27, 0, 2, UIntPtr.Zero); await Idle();
                var caret = tableView.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                var caretY = tableView.TranslatePoint(caret.TopLeft, outer).Y;
                Console.WriteLine($"Baseline off-screen keyboard: caret={caretY:F1}, viewport={outer.ViewportHeight:F1}, offset={outer.VerticalOffset:F1}, focus={Keyboard.FocusedElement?.GetType().Name}; {string.Join(" | ", events)}");
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--table-menu-only"))
            {
                var menu = answer.ContextMenu ?? throw new Exception("Answer context menu is missing");
                var items = menu.Items.OfType<MenuItem>().Where(item => !Equals(item.Header, "退出批注模式")).ToArray();
                Check(items.Select(item => item.Header?.ToString()).SequenceEqual(new[] { "批注到当前会话", "开辟新会话", "添加标签", "复制所选", "复制所在段落" }), "Answer menu order or labels changed");
                await Place(second); second.ScrollToHorizontalOffset(0); await Idle();
                var paragraph = (Paragraph)((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[0].Blocks.FirstBlock!;
                var rect = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                var point = tableView.PointToScreen(new Point(rect.X + 2, rect.Y + rect.Height / 2));
                host.Activate(); Check(SetCursorPos((int)point.X, (int)point.Y), "Cannot position pointer for context menu"); await Task.Delay(25);
                GetCursorPos(out var cursor); Check(Math.Abs(cursor.X - point.X) < 4 && Math.Abs(cursor.Y - point.Y) < 4, "Pointer moved externally before context menu");
                mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); await Idle();
                Check(menu.IsOpen && items[4].IsEnabled, "Right click did not open paragraph copy menu");
                items[4].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(Clipboard.GetText().Trim() == "第2表文字", "Paragraph copy did not use the right-clicked table cell");
                menu.IsOpen = false;
                var formulaParagraph = (Paragraph)((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[1].Blocks.FirstBlock!;
                var formulaRect = formulaParagraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                answer.CaptureContextParagraph(tableView.TranslatePoint(new Point(formulaRect.X + 2, formulaRect.Y + formulaRect.Height / 2), answer));
                answer.CopyContextParagraph();
                Check(Clipboard.GetText().Contains("$x+2$"), "Table formula paragraph was not copied as TeX");
                var paragraphs = answer.Document.Blocks.OfType<Paragraph>().Take(3).ToArray();
                Check(paragraphs.Length == 3, "Answer paragraphs were not rendered");
                outer.ScrollToVerticalOffset(0); await Idle();
                answer.Selection.Select(paragraphs[0].ContentStart, paragraphs[1].ContentEnd);
                var selectionBefore = answer.CopyForClipboard(answer.Selection.Start, answer.Selection.End);
                var thirdRect = paragraphs[2].ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                answer.CaptureContextParagraph(new Point(thirdRect.X + 2, thirdRect.Y + thirdRect.Height / 2));
                answer.CopyContextParagraph();
                Check(Clipboard.GetText().Trim() == "第 1 组说明 2：这里是用于定位的普通正文。", "Copy paragraph used the selection instead of the clicked paragraph");
                Check(answer.CopyForClipboard(answer.Selection.Start, answer.Selection.End) == selectionBefore, "Copying the clicked paragraph changed a cross-paragraph selection");
                Console.WriteLine("PASS: answer menu labels, physical right-click paragraph copy, TeX and cross-paragraph selection.");
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--table-drag-only"))
            {
                second.ScrollToHorizontalOffset(0); await Place(second);
                var cell = ((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[0];
                var paragraph = (Paragraph)cell.Blocks.FirstBlock!;
                var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!;
                var end = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!;
                var left = start.GetCharacterRect(LogicalDirection.Forward); var right = end.GetCharacterRect(LogicalDirection.Backward);
                Check(!left.IsEmpty && !right.IsEmpty, "Table selection character geometry is unavailable");
                var from = tableView.PointToScreen(new Point(left.Left + 1, left.Top + left.Height / 2));
                var to = tableView.PointToScreen(new Point(right.Right + 1, right.Top + right.Height / 2));
                var y = second.TranslatePoint(new Point(), outer).Y;
                host.Activate(); Check(SetCursorPos((int)from.X, (int)from.Y), "Cannot position pointer for selection"); await Task.Delay(20);
                GetCursorPos(out var cursor); Check(Math.Abs(cursor.X - from.X) < 4 && Math.Abs(cursor.Y - from.Y) < 4, "Pointer moved externally before selecting text");
                Console.WriteLine($"Drag start from={from} to={to}, over={Mouse.DirectlyOver?.GetType().Name}, tableY={y:F1}, rects={left}/{right}");
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                try { await Task.Delay(40); Check(SetCursorPos((int)to.X, (int)to.Y), "Cannot move selection pointer"); await Task.Delay(50); }
                finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
                await Idle();
                var copied = tableView.CopyForClipboard(tableView.Selection.Start, tableView.Selection.End).Trim();
                Console.WriteLine($"Physical drag from={from} to={to}, copied='{copied}', quote='{answer.GetAnnotationSelection().Quote}', focus={Keyboard.FocusedElement?.GetType().Name}");
                Check(copied == "第2表文字" && answer.GetAnnotationSelection().Quote.Trim() == copied, "Real drag selection or copied quote is incorrect");
                Check(Math.Abs(second.TranslatePoint(new Point(), outer).Y - y) < 3, "Dragging a table selection moved the viewport");
                var bookmark = panel.CaptureBookmarkSelection(answer);
                Check(bookmark is not null && panel.AddBookmarkFromSelection(bookmark, "表格标签"), "A table selection could not become a bookmark");
                panel.AnnotateAnswer(answer);
                Check(book.Entries.Any(entry => entry.AnswerAnchor?.Quote.Trim() == copied), "A table selection could not become a reference");
                Console.WriteLine("PASS: physical table text selection, copy, bookmark and annotation preserve the reading viewport.");
                return;
            }
            var field = Descendants<TextBox>(tableView).Single(box => AutomationProperties.GetName(box) == "field2");
            if (!Environment.GetCommandLineArgs().Contains("--table-blank-only"))
            {
                await ClickText(1); await ClickText(0); await ClickText(2); await ClickText(1);
                var keyY = second.TranslatePoint(new Point(), outer).Y;
                keybd_event(0x27, 0, 0, UIntPtr.Zero); keybd_event(0x27, 0, 2, UIntPtr.Zero); await Idle();
                Check(tableView.IsKeyboardFocusWithin && Math.Abs(second.TranslatePoint(new Point(), outer).Y - keyY) < 3, "Keyboard movement in table text moved the answer viewport");
                var formula = Descendants<ChatFormula>(tableView).FirstOrDefault() ?? throw new Exception("Missing table formula");
                second.ScrollToHorizontalOffset(0); await Click(second, formula, new Point(formula.ActualWidth / 2, formula.ActualHeight / 2), "table 2 formula");
                var link = ((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[2].Blocks.OfType<Paragraph>().Single().Inlines.OfType<Hyperlink>().Single();
                second.ScrollToHorizontalOffset(second.ScrollableWidth); await Idle();
                var linkRect = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                await Click(second, tableView, new Point(linkRect.X + 3, linkRect.Y + linkRect.Height / 2), "table 2 link");
                await Click(second, field, new Point(Math.Min(8, field.ActualWidth / 2), field.ActualHeight / 2), "table 2 form field");
                Check(field.IsKeyboardFocusWithin, "Table form field lost focus");
            }
            second.ScrollToHorizontalOffset(second.ScrollableWidth); await Idle();
            var blank = ((Table)tableView.Document.Blocks.FirstBlock!).RowGroups[0].Rows[1].Cells[4];
            var blankPoint = ((Paragraph)blank.Blocks.FirstBlock!).ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Check(!blankPoint.IsEmpty, "Blank table cell has no caret layout");
            await Click(second, tableView, new Point(blankPoint.X + 2, blankPoint.Y + blankPoint.Height / 2), "table 2 blank cell");
            Check(tableView.IsKeyboardFocusWithin, "Blank table cell did not focus the table");
            await Place(second);
            var wheelBefore = outer.VerticalOffset;
            var wheelPoint = second.PointToScreen(new Point(20, Math.Min(20, second.ActualHeight / 2)));
            Check(SetCursorPos((int)wheelPoint.X, (int)wheelPoint.Y), "Cannot position pointer for wheel"); await Task.Delay(25);
            GetCursorPos(out var wheelCursor); Check(Math.Abs(wheelCursor.X - wheelPoint.X) < 4 && Math.Abs(wheelCursor.Y - wheelPoint.Y) < 4, "Pointer was moved externally before the wheel");
            mouse_event(0x0800, 0, 0, unchecked((uint)-120), UIntPtr.Zero); await Idle();
            Check(outer.VerticalOffset > wheelBefore && outer.VerticalOffset - wheelBefore < 100, "Physical wheel did not scroll the conversation normally");
            await Place(second); var beforeInterruption = outer.VerticalOffset;
            field.Focus(); tableView.Focus();
            var interruptedWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            tableView.RaiseEvent(interruptedWheel); await Idle();
            Check(outer.VerticalOffset > beforeInterruption && outer.VerticalOffset - beforeInterruption < 100,
                "Deferred table focus correction overrode a user wheel gesture");
            Console.WriteLine("PASS: multiple table text focus, selection, formula, link and form clicks retain the reading viewport.");
        }
        finally { host.Close(); }
    }
}
