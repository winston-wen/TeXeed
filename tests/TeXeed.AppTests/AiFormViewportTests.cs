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
    private static async Task CheckAiFormViewportAsync(Window owner, string output)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var project = Path.Combine(output, "form-viewport-" + Guid.NewGuid().ToString("N") + ".tex");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(project);
        var book = (AiNotebook)Field(panel, "_book")!;
        var before = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"前文第 {i} 段：这是一段需要保持阅读位置的说明文字。"));
        var after = string.Join("\n\n", Enumerable.Range(0, 50).Select(i => $"后文第 {i} 段：继续阅读需要保持当前位置。"));
        var source = AiForms.WithSubmission(new AiTurn("练习", before + "\n\n表格前的定位文字。\n\n| 函数 | 输入 | 第二格 |\n| --- | --- | --- |\n| Key(w) | {{text:key}} | {{text:help}} |\n\n行内输入 {{text:inline}} 之后的正文。\n\n多行输入：\n\n{{textarea:reason}}\n\n" + after + "\n\n{{text:offscreen}}"), null);
        book.Turns.Add(source);
        var host = new Window { Owner = owner, Width = 900, Height = 680, Content = panel, Topmost = true }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[source.Id].Answers.Single(); await answer.RenderTask;
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var boxes = Descendants<TextBox>(answer).Where(box => AutomationProperties.GetName(box) is "key" or "help" or "inline" or "reason" or "offscreen")
                .ToDictionary(box => AutomationProperties.GetName(box));
            Check(boxes.Count == 5, "Missing table, inline, multiline or distant fields");
            var events = new List<string>();
            void Trace(FrameworkElement node, string label) => node.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) => events.Add($"{label}: origin={e.OriginalSource?.GetType().Name}/{(e.OriginalSource as FrameworkElement)?.Name} target={e.TargetObject.GetType().Name}/{(e.TargetObject as FrameworkElement)?.Name}#{e.TargetObject.GetHashCode()} rect={e.TargetRect} handled={e.Handled} offset={scroll.VerticalOffset:F1}")), true);
            var tableScroll = Descendants<ScrollViewer>(answer).Single(s => s.Content is MarkdownView nested && Descendants<TextBox>(nested).Contains(boxes["key"]));
            Trace(boxes["key"], "box"); Trace((MarkdownView)tableScroll.Content, "table-view"); Trace(tableScroll, "table-scroll"); Trace(answer, "answer"); Trace(scroll, "messages");
            async Task Idle() { await Task.Delay(100); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
            async Task Position(TextBox box)
            {
                host.UpdateLayout(); var y = box.TranslatePoint(new Point(), scroll).Y;
                scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + y - scroll.ViewportHeight / 2, 0, scroll.ScrollableHeight));
                await Idle();
                var current = box.TranslatePoint(new Point(), scroll).Y;
                Check(current >= 0 && current < scroll.ViewportHeight, $"Field was not positioned in viewport: {current:F1}");
            }
            async Task Click(TextBox box)
            {
                var beforeClick = scroll.VerticalOffset;
                host.Activate(); var point = box.PointToScreen(new Point(Math.Min(10, box.ActualWidth / 2), Math.Min(10, box.ActualHeight / 2)));
                Console.WriteLine($"Click {AutomationProperties.GetName(box)} screen={point} size={box.ActualWidth:F1}x{box.ActualHeight:F1} active={host.IsActive} visible={box.IsVisible}");
                Capture(host, Path.Combine(output, "form-viewport-before-click.png"));
                Check(SetCursorPos((int)point.X, (int)point.Y), "Cannot position physical pointer");
                await Task.Delay(100);
                GetCursorPos(out var cursor); Console.WriteLine($"Pointer={cursor.X},{cursor.Y}, WPF over={Mouse.DirectlyOver?.GetType().Name}, focused={Keyboard.FocusedElement?.GetType().Name}");
                if (Math.Abs(cursor.X - point.X) < 3 && Math.Abs(cursor.Y - point.Y) < 3)
                { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); await Task.Delay(50); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
                await Idle();
                Console.WriteLine($"After click focus={Keyboard.FocusedElement?.GetType().Name} offset={scroll.VerticalOffset:F1}, y={box.TranslatePoint(new Point(), scroll).Y:F1}; events: {string.Join(" | ", events)}");
                if (!box.IsKeyboardFocusWithin) { Console.WriteLine("Pointer moved during injection; using WPF focus."); scroll.ScrollToVerticalOffset(beforeClick); await Idle(); box.Focus(); await Idle(); }
                Check(box.IsKeyboardFocusWithin, "Physical click did not focus field " + AutomationProperties.GetName(box));
            }
            async Task Key(byte key)
            {
                keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); await Idle();
            }
            foreach (var name in new[] { "key", "help", "inline", "reason" })
            {
                var box = boxes[name]; await Position(box);
                var beforeY = box.TranslatePoint(new Point(), scroll).Y; var beforeOffset = scroll.VerticalOffset;
                await Click(box);
                var focusY = box.TranslatePoint(new Point(), scroll).Y;
                await Key(0x31); await Key(0x32);
                var afterY = box.TranslatePoint(new Point(), scroll).Y;
                Console.WriteLine($"Form viewport {name}: text=[{box.Text}], offset {beforeOffset:F1} -> {scroll.VerticalOffset:F1}, field {beforeY:F1} -> {focusY:F1} -> {afterY:F1}; events: {string.Join(" | ", events)}");
                Check(Math.Abs(focusY - beforeY) < 3 && Math.Abs(afterY - beforeY) < 3, "Focusing or typing moved the visible form and its surrounding answer");
                Check(box.Text.Contains("12"), "Physical keyboard input did not reach " + name);
                if (name == "key")
                {
                    var helpY = boxes["help"].TranslatePoint(new Point(), scroll).Y;
                    await Key(0x09);
                    Console.WriteLine($"Tab result focus={Keyboard.FocusedElement?.GetType().Name}/{(Keyboard.FocusedElement as FrameworkElement)?.Name}, helpFocus={boxes["help"].IsKeyboardFocusWithin}, helpY={helpY:F1}->{boxes["help"].TranslatePoint(new Point(), scroll).Y:F1}, offset={scroll.VerticalOffset:F1}");
                    Check(boxes["help"].IsKeyboardFocusWithin && Math.Abs(boxes["help"].TranslatePoint(new Point(), scroll).Y - helpY) < 3,
                        "Tab to the adjacent table field moved the reading position or failed to focus");
                    await Task.Delay(900); await Idle();
                    Check(Math.Abs(boxes["help"].TranslatePoint(new Point(), scroll).Y - helpY) < 3, "Autosave moved the focused table field");
                }
                events.Clear();
            }
            host.Width = 420; host.UpdateLayout(); await Idle();
            Check(tableScroll.ScrollableWidth > 0, "Narrow table fixture did not overflow horizontally");
            tableScroll.ScrollToHorizontalOffset(0); await Idle();
            await Position(boxes["key"]); await Click(boxes["key"]);
            await Key(0x09);
            var helpX = boxes["help"].TranslatePoint(new Point(), tableScroll).X;
            Console.WriteLine($"Narrow Tab: helpFocus={boxes["help"].IsKeyboardFocusWithin}, horizontal={tableScroll.HorizontalOffset:F1}/{tableScroll.ScrollableWidth:F1}, helpX={helpX:F1}, width={boxes["help"].ActualWidth:F1}, viewport={tableScroll.ViewportWidth:F1}");
            Check(boxes["help"].IsKeyboardFocusWithin && tableScroll.HorizontalOffset > 0 && helpX < tableScroll.ViewportWidth && helpX + boxes["help"].ActualWidth > 0,
                "Tab did not scroll a clipped table field into view horizontally");
            keybd_event(0x10, 0, 0, UIntPtr.Zero); await Key(0x09); keybd_event(0x10, 0, 2, UIntPtr.Zero); await Idle();
            Check(boxes["key"].IsKeyboardFocusWithin, "Shift+Tab did not return to the preceding table field");
            host.Width = 900; host.UpdateLayout(); await Idle();
            await Position(boxes["reason"]); await Click(boxes["reason"]);
            await Key(0x09);
            var distant = boxes["offscreen"];
            var distantY = distant.TranslatePoint(new Point(), scroll).Y;
            Check(distant.IsKeyboardFocusWithin && distantY >= -1 && distantY + distant.ActualHeight <= scroll.ViewportHeight + 1,
                $"Tab did not bring a distant field into view: y={distantY:F1}, viewport={scroll.ViewportHeight:F1}");
            await Key(0x31);
            var wheelPoint = scroll.PointToScreen(new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2));
            Check(SetCursorPos((int)wheelPoint.X, (int)wheelPoint.Y), "Cannot position pointer for wheel"); await Task.Delay(100);
            var wheelBefore = scroll.VerticalOffset; mouse_event(0x0800, 0, 0, 120, UIntPtr.Zero); await Idle();
            Check(scroll.VerticalOffset < wheelBefore, "Physical mouse wheel no longer scrolls the conversation");
            await panel.StopAndSaveAsync();
            var restored = AiStorage.Load(project);
            Check(AiForms.Draft(restored.Active.Turns.Single()).Count == 5, "Form edits were not retained after save and reload");
            await panel.SetProjectAsync(project); host.UpdateLayout(); await Idle();
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            answer = views[source.Id].Answers.Single(); await answer.RenderTask; host.UpdateLayout(); await Idle();
            var reopened = Descendants<TextBox>(answer).Single(box => AutomationProperties.GetName(box) == "key");
            Check(reopened.Text.Contains("12"), "Reopening did not restore the table field");
            await Position(reopened); var reopenedY = reopened.TranslatePoint(new Point(), scroll).Y;
            await Click(reopened); await Key(0x33);
            Check(reopened.Text.Contains("3") && Math.Abs(reopened.TranslatePoint(new Point(), scroll).Y - reopenedY) < 3,
                "Editing the restored form moved the answer");
            var longBox = Descendants<TextBox>(answer).Single(box => AutomationProperties.GetName(box) == "reason");
            longBox.Text = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"第 {i} 行的长答案"));
            host.UpdateLayout(); await Idle();
            Check(longBox.ActualHeight > scroll.ViewportHeight, "Long multiline fixture did not exceed the viewport");
            await Position(longBox); await Click(longBox);
            async Task ControlKey(byte key)
            {
                keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); await Idle();
            }
            double CaretY()
            {
                var caret = longBox.GetRectFromCharacterIndex(longBox.CaretIndex);
                Check(!caret.IsEmpty, "Multiline caret has no layout");
                return longBox.TranslatePoint(caret.TopLeft, scroll).Y;
            }
            await ControlKey(0x24); await Key(0x31);
            Check(CaretY() >= 0 && CaretY() < scroll.ViewportHeight && longBox.Text.StartsWith("1", StringComparison.Ordinal), "Editing at the beginning of a long field moved the caret out of view");
            await ControlKey(0x23); await Key(0x32);
            var bottomCaret = CaretY();
            Check(bottomCaret >= 0 && bottomCaret < scroll.ViewportHeight && longBox.Text.EndsWith("2", StringComparison.Ordinal), "Editing at the end of a long field moved the caret out of view");
            var longWheel = scroll.PointToScreen(new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2));
            Check(SetCursorPos((int)longWheel.X, (int)longWheel.Y), "Cannot position pointer for long-field wheel"); await Task.Delay(100);
            var longBefore = scroll.VerticalOffset; mouse_event(0x0800, 0, 0, 120, UIntPtr.Zero); await Idle();
            Check(scroll.VerticalOffset < longBefore, "Wheel did not scroll while a long multiline field was focused");
            await ControlKey(0x24);
            Check(CaretY() >= 0 && CaretY() < scroll.ViewportHeight, "Returning to the first caret did not bring it into view");
            Console.WriteLine("PASS: physical form focus and typing preserve visible answer position and saved values.");
        }
        finally { host.Close(); }
    }
}
