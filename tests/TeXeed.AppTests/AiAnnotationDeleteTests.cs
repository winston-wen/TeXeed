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
    private static async Task CheckAiAnnotationDeleteAsync(Window owner)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var answer = new MarkdownView { ChatMode = true, Width = 700, Height = 350, Markdown = "用于测试批注删除的回答。" };
        var host = new Window { Owner = owner, Content = new AdornerDecorator { Child = answer }, Width = 760, Height = 500, ShowActivated = false };
        var entry = new AiEntry { Title = "AI 回答批注", Number = 1, Question = "已有内容" };
        var entries = new List<AiEntry> { entry }; var pointer = new Point(10, 10);
        using var layer = new AiAnnotationLayer(answer, () => entries, _ => new Point(80, 80), item => { if (item.CanRemoveReference) entries.Remove(item); }, margin: true)
        { AnswerPopupPointerPosition = _ => pointer };
        host.Show(); var popup = (Popup)Field(layer, "_popup")!;
        async Task Settle() { await Task.Delay(80); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
        async Task Open() { layer.Edit(entry); await Settle(); Check(popup.IsOpen, "Annotation popup did not open"); }
        Button Delete() => Descendants<Button>(popup.Child).Single(button => button.Name == "AnnotationDelete");
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await answer.RenderTask; await Settle(); await Open();
            var delete = Delete(); Click(delete); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true, Content: "再点一下删除" }, "First activation did not show a persistent hint");
            await Task.Delay(Math.Max(1200, (int)GetDoubleClickTime() + 600));
            Check(delete.Tag is ToolTip { IsOpen: true }, "The confirmation hint expired prematurely");
            delete.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(!popup.IsOpen && !entries.Contains(entry), "Keyboard second activation did not delete after a long wait");

            entry = new AiEntry { Title = "AI 回答批注", Number = 1, AnswerAnchor = new AiAnswerAnchor("turn", "segment", 0, 4, "来源文字", "revision") };
            entries.Add(entry); pointer = new Point(10, 10); await Settle(); await Open(); delete = Delete(); Click(delete); await Settle();
            var surface = (FrameworkElement)popup.Child; pointer = new Point(-10, -10);
            surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent }); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: false }, "Leaving the popup did not cancel pending deletion");
            pointer = new Point(10, 10); Click(delete); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "First activation after leaving unexpectedly deleted the annotation");
            layer.CloseEditor(); await Settle(); await Open(); delete = Delete(); Click(delete); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "Reopening failed to reset pending deletion");
            Click(delete); await Settle(); Check(!entries.Contains(entry), "Second activation did not delete a reopened empty annotation");

            entry = new AiEntry { Title = "AI 回答批注", Number = 1 }; entry.Images.Add(new AiPastedImage("not-base64")); entries.Add(entry);
            await Settle(); await Open(); delete = Delete(); Click(delete); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "First activation removed a comment with an image");
            entry.Images.Clear(); await Settle(); Check(delete.Tag is ToolTip { IsOpen: false }, "Editing images failed to cancel pending deletion");
            delete.RaiseEvent(CreateKey(host, Key.Space, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(entries.Contains(entry) && delete.Tag is ToolTip { IsOpen: true }, "First keyboard activation did not show a hint");
            delete.RaiseEvent(CreateKey(host, Key.Space, Keyboard.PreviewKeyDownEvent)); await Settle();
            Check(!entries.Contains(entry), "Second keyboard activation did not delete the annotation");

            entry = new AiEntry { Title = "追问出处", Number = 1, AdditionalData = new() { [AiSessionExcerptReferences.OriginLockKey] = System.Text.Json.JsonSerializer.SerializeToElement(true) } };
            entries.Add(entry); await Settle(); await Open(); delete = Delete(); Click(delete); await Settle();
            Check(!delete.IsEnabled && entries.Contains(entry), "Protected source could be deleted");
            Console.WriteLine("PASS: deterministic WPF leave/close reset, long-wait keyboard confirmation, image edits and protected source.");
        }
        finally { host.Close(); }
    }
}
