using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiAnnotationDragAsync(Window owner, string output)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var answer = new MarkdownView { ChatMode = true, Width = 780, Height = 450, Markdown = "这是可以批注的回答。\n\n拖动批注时正文和草稿保持可用。" };
        var scroll = new ScrollViewer { Content = answer, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = new Window { Owner = owner, Content = new AdornerDecorator { Child = scroll }, Width = 900, Height = 650 };
        var entry = new AiEntry { Title = "AI 回答批注", Number = 1, Question = "原有草稿" };
        var entries = new List<AiEntry> { entry }; var commits = 0; var confirms = 0;
        using var layer = new AiAnnotationLayer(answer, () => entries, _ => new Point(80, 80), item => { if (item.CanRemoveReference) entries.Remove(item); }, margin: true,
            confirm: item => { item.Selected = true; confirms++; }, commit: () => commits++);
        host.Show();
        var popup = (Popup)Field(layer, "_popup")!;
        async Task Settle() { await Task.Delay(100); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
        async Task Open() { layer.Edit(entry); await Settle(); Check(popup.IsOpen, "Answer annotation popup did not open"); }
        async Task ClickDelete(Button button)
        {
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            Check(SetCursorPos((int)point.X, (int)point.Y), "Cannot position pointer on delete button");
            await Task.Delay(80); GetCursorPos(out var actual);
            Check(Math.Abs(actual.X - point.X) < 4 && Math.Abs(actual.Y - point.Y) < 4, "Pointer moved before delete click");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); await Settle();
        }
        var cursor = new NativePoint(); GetCursorPos(out cursor); var mouseDown = false;
        try
        {
            await answer.RenderTask; await Settle(); await Open();
            var surface = (FrameworkElement)popup.Child;
            var header = Descendants<Grid>(surface).Single(grid => grid.Name == "AnnotationDragHeader");
            var input = Descendants<TextBox>(surface).Single();
            Check(popup.PlacementTarget == host && input.Text == "原有草稿", "Popup did not use the answer window or retain the draft");
            host.Activate(); await Settle();
            var start = header.PointToScreen(new Point(Math.Min(40, header.ActualWidth / 3), header.ActualHeight / 2));
            var initial = surface.PointToScreen(new Point());
            Console.WriteLine($"Annotation drag fixture: start={start}, popup={initial}, window={host.PointToScreen(new Point())}, hostActive={host.IsActive}, header={header.ActualWidth:F1}x{header.ActualHeight:F1}");
            Check(SetCursorPos((int)start.X, (int)start.Y), "Cannot position pointer on annotation title");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouseDown = true; await Task.Delay(40);
            Check(SetCursorPos((int)(start.X - 120), (int)(start.Y + 80)), "Cannot move pointer while dragging annotation");
            await Task.Delay(80); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); mouseDown = false; await Settle();
            var moved = surface.PointToScreen(new Point());
            Check(popup.IsOpen && moved.X < initial.X - 30 && moved.Y > initial.Y + 20 && input.Text == entry.Question,
                $"Dragging the header failed or lost the draft: {initial} -> {moved}");
            input.Text = "拖动后继续输入"; await Settle();
            Check(entry.Question == input.Text && popup.IsOpen, "Editing after dragging did not update the shared draft");
            host.Width = 520; host.Height = 360; await Settle(); await Settle();
            var topLeft = host.PointFromScreen(surface.PointToScreen(new Point()));
            var bottomRight = host.PointFromScreen(surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight)));
            Check(popup.IsOpen && topLeft.X >= -1 && topLeft.Y >= -1 && bottomRight.X <= host.ActualWidth + 1 && bottomRight.Y <= host.ActualHeight + 1,
                $"Resizing hid or closed the popup: {topLeft} / {bottomRight}, window {host.ActualWidth}x{host.ActualHeight}");
            SaveLiteralVisual(surface, Path.Combine(output, "ai-annotation-drag-after-resize.png"));
            Descendants<Button>(surface).Single(button => button.Name == "AnnotationConfirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(!popup.IsOpen && confirms == 1 && commits == 1 && entries.Contains(entry) && entry.Question == "拖动后继续输入", "Confirm after drag did not save and close locally");
            await Open();
            var reopened = (FrameworkElement)popup.Child;
            Check(Descendants<TextBox>(reopened).Single().Text == entry.Question, "Reopening after drag lost the draft");
            var delete = Descendants<Button>(reopened).Single(button => button.Name == "AnnotationDelete");
            delete.Focus();
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true, Content: "再点一下删除" }, "First delete activation did not display the confirmation hint");
            await Task.Delay(Math.Max(1200, (int)GetDoubleClickTime() + 600));
            Check(delete.Tag is ToolTip { IsOpen: true }, "Delete confirmation did not remain visible long enough to read");
            delete.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(!popup.IsOpen && entries.Count == 0 && confirms == 1, "Second activation after the double-click interval did not remove the annotation");

            entry = new AiEntry { Title = "AI 回答批注", Number = 1, AnswerAnchor = new AiAnswerAnchor("turn", "segment", 0, 4, "来源文字", "revision") };
            entries.Add(entry); await Settle(); await Open();
            layer.CloseEditor(); await Settle(); await Open();
            delete = Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
            await ClickDelete(delete);
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true, Content: "再点一下删除" }, "First physical click removed an empty annotation or omitted the hint");
            SaveLiteralVisual((ToolTip)delete.Tag, Path.Combine(output, "ai-annotation-delete-hint.png"));
            await Task.Delay(Math.Max(1200, (int)GetDoubleClickTime() + 600));
            Check(delete.Tag is ToolTip { IsOpen: true }, "Physical click hint expired before the second click");
            await ClickDelete(delete);
            Check(!popup.IsOpen && !entries.Contains(entry), "Second physical click did not remove the empty annotation");

            entry = new AiEntry { Title = "AI 回答批注", Number = 1, AnswerAnchor = new AiAnswerAnchor("turn", "segment", 0, 4, "来源文字", "revision") };
            entry.Images.Add(new AiPastedImage("not-base64")); entries.Add(entry); await Settle(); await Open();
            delete = Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
            delete.Focus();
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "A single click removed a comment containing an image");
            entry.Images.Clear(); await Settle();
            Check(delete.Tag is ToolTip { IsOpen: false }, "Changing an image did not cancel pending deletion");
            delete.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "First keyboard activation did not show the hint");
            delete.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(!popup.IsOpen && !entries.Contains(entry), "Second keyboard activation did not remove the annotation");

            entry = new AiEntry { Title = "AI 回答批注", Number = 1 }; entries.Add(entry); await Settle(); await Open();
            delete = Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
            await ClickDelete(delete);
            Check(delete.Tag is ToolTip { IsOpen: true }, "Deletion was not pending before closing");
            layer.CloseEditor(); await Settle(); await Open();
            delete = Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
            await ClickDelete(delete);
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "Reopening did not reset pending deletion");
            var outside = host.PointToScreen(new Point(10, host.ActualHeight - 20));
            SetCursorPos((int)outside.X, (int)outside.Y); await Settle();
            Check(delete.Tag is ToolTip { IsOpen: false }, "Leaving the popup did not cancel pending deletion");
            await ClickDelete(delete);
            Check(popup.IsOpen && entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "First activation after leaving unexpectedly deleted the annotation");
            await ClickDelete(delete);
            Check(!popup.IsOpen && !entries.Contains(entry), "Second activation after returning did not delete the annotation");

            entry = new AiEntry { Title = "追问出处", Number = 1, AnswerAnchor = new AiAnswerAnchor("turn", "segment", 0, 4, "来源文字", "revision"),
                AdditionalData = new() { [AiSessionExcerptReferences.OriginLockKey] = System.Text.Json.JsonSerializer.SerializeToElement(true) } };
            entries.Add(entry); await Settle(); await Open();
            delete = Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(!delete.IsEnabled && popup.IsOpen && entries.Contains(entry), "Protected source reference allowed deletion");
            Console.WriteLine("PASS: physical answer annotation drag, resize, draft, persistent two-step deletion and protected source.");
        }
        finally
        {
            if (mouseDown) mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(cursor.X, cursor.Y); host.Close();
        }
    }
}
