using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckAiPinsAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var document = new Border { Height = 1400, Background = ThemeManager.Brush("EditorBackground") };
        var scroll = new ScrollViewer { Content = document };
        var outside = new Button { Content = "文档外的焦点" };
        var layout = new DockPanel(); DockPanel.SetDock(outside, Dock.Bottom); layout.Children.Add(outside); layout.Children.Add(scroll);
        var host = new Window { Owner = owner, Content = new AdornerDecorator { Child = layout }, Width = 680, Height = 620, Title = "图钉编辑框" };
        var entry = new AiEntry { Title = "PDF 第 2–2 页 · 区域", Number = 1 };
        var entries = new List<AiEntry> { entry }; var committed = 0; var sent = 0;
        var anchor = new Point(100, 100);
        using var layer = new AiAnnotationLayer(document, () => entries, _ => anchor, e => entries.Remove(e),
            send: e => { if (!string.IsNullOrWhiteSpace(e.Question)) { sent++; entries.Remove(e); } return Task.CompletedTask; }, commit: () => committed++);
        host.Show();
        async Task Settle() { host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); await Task.Delay(40); host.UpdateLayout(); }
        var popup = (Popup)Field(layer, "_popup")!;
        async Task Open() { layer.Edit(entry); await Settle(); Check(popup.IsOpen, "Clicking a pin did not leave its editor open"); }
        try
        {
            await Settle(); await Open();
            Check(popup.StaysOpen, "Pin editor still captures outside clicks for dismissal");
            var input = Descendants<TextBox>(popup.Child).Single();
            var numbers = Descendants<TextBoxLineNumbers>(popup.Child).Single();
            var buttons = Descendants<Button>(popup.Child).ToArray();
            Check(buttons.Length == 2 && buttons[0].Name == "AnnotationDelete" && buttons[1].Name == "AnnotationSend", "Header must contain only delete then send icons");
            Check(buttons.All(b => b.Content is System.Windows.Shapes.Path) && buttons[0].TranslatePoint(new Point(), (UIElement)popup.Child).X < buttons[1].TranslatePoint(new Point(), (UIElement)popup.Child).X, "Header icon order is wrong");
            Check(buttons.All(b => b.TranslatePoint(new Point(), (UIElement)popup.Child).Y < input.TranslatePoint(new Point(), (UIElement)popup.Child).Y), "Actions remain below the editor");
            Check(input.BorderThickness == new Thickness(0) && input.Padding == new Thickness(8) && numbers.ActualWidth < 25, "Editor gutter does not match the AI composer");
            input.Text = new string('字', 100); input.ScrollToHome(); await Settle();
            Check(entry.Question == input.Text && numbers.LogicalLineCount == 1 && numbers.PaintedNumbers.SequenceEqual(new[] { 1 }), "Soft wrapping changed line numbers or shared annotation text");
            outside.Focus(); await Settle(); Check(popup.IsOpen && committed == 0, "Focus loss dismissed or submitted the annotation");
            popup.Child.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Settle(); Check(popup.IsOpen, "Escape dismissed the annotation without a document change");
            input.Text = string.Join('\n', Enumerable.Range(1, 40).Select(i => "逻辑行 " + i)); input.ScrollToEnd(); await Settle();
            Check(numbers.LogicalLineCount == 40 && numbers.PaintedNumbers.Contains(40) && popup.IsOpen, "Editor scrolling misaligned numbers or dismissed the popup");
            entry.Question = "第一行\n第二行：继续修改同一份批注。"; input.ScrollToHome(); await Settle();
            Check(input.Text == entry.Question && popup.IsOpen, "External edits did not update the same open annotation");
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(theme); await Settle();
                Check(popup.IsOpen, "An unchanged document view closed its editor during a palette change");
                SaveLiteralVisual((FrameworkElement)popup.Child, Path.Combine(output, "pin-editor-" + theme + ".png"));
            }
            scroll.ScrollToVerticalOffset(30); await Settle();
            Check(!popup.IsOpen && committed == 1 && sent == 0 && entries.Contains(entry), "Document scroll must save and close without sending or freezing the annotation");
            await Open(); Check(Descendants<TextBox>(popup.Child).Single().Text == entry.Question, "Reopening lost saved text");
            host.Width += 30; await Settle(); Check(!popup.IsOpen && committed == 2, "Resizing did not close and save");
            await Open(); document.Margin = new Thickness(12, 0, 0, 0); await Settle();
            Check(!popup.IsOpen && committed == 3, "Moving document region did not close and save");
            await Open(); document.Visibility = Visibility.Collapsed; await Settle();
            Check(!popup.IsOpen && committed == 4, "Hidden document left a floating editor");
            document.Visibility = Visibility.Visible; await Settle(); await Open();
            Descendants<Button>(popup.Child).Single(b => b.Name == "AnnotationSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(sent == 1 && !popup.IsOpen && entries.Count == 0, "Send icon did not send this annotation and close");
            entries.Add(entry); await Open();
            Descendants<Button>(popup.Child).Single(b => b.Name == "AnnotationDelete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(entries.Count == 0 && !popup.IsOpen && sent == 1, "Delete icon sent content or left the editor open");
        }
        finally { host.Close(); ThemeManager.Apply("ristretto"); }
        Console.WriteLine($"PASS: {checks} pin-editor checks: stable focus, shared editable content, internal logical lines, header icons, both themes, view-change save/close, send and delete.");
    }
}
