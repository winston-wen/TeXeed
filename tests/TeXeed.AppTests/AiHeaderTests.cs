using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiHeaderAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool condition, string error) { if (!condition) throw new Exception(error); checks++; }
        new AiCredentials { ApiKey = "fixture-never-send", UseApiKey = true }.Save();
        using var connection = new AiConnection(new AiFakeHandler());
        var panel = new AiPanel(connection);
        var panelDisposed = false;
        var host = new Window { Owner = owner, Width = 840, Height = 700, Content = panel, Title = "AI 标题悬停回归" };
        try
        {
            var models = (ComboBox)panel.Settings.FindName("Models");
            models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
            await panel.Settings.SaveSelectionAsync(CancellationToken.None);
            host.Show();
            var project = Path.Combine(output, "header-project.tex");
            await panel.SetProjectAsync(project); await Task.Delay(150); host.UpdateLayout();
            var header = (Grid)panel.FindName("ChatHeader");
            var content = (Border)panel.FindName("HeadingContent");
            var peek = (Button)panel.FindName("HeadingPeek");
            var timer = (DispatcherTimer)Field(panel, "_headerCloseTimer")!;
            var book = (AiNotebook)Field(panel, "_book")!;
            // Hit-test the real WPF visual tree, then route boundary events explicitly.
            // Do not depend on or move the user's desktop pointer during timer checks.
            void Boundary(RoutedEvent boundary) => header.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = boundary });
            bool Hits(FrameworkElement target, Point point) => host.InputHitTest(point) is DependencyObject hit && (hit == target || target.IsAncestorOf(hit));
            Invoke(panel, "ResetHeaderHover"); host.UpdateLayout();
            Check(content.Visibility == Visibility.Collapsed, "Header must start collapsed");
            var closedPosition = peek.TranslatePoint(new Point(), host);
            var buttonCenter = peek.TranslatePoint(new Point(26, 9), host);
            Check(Hits(peek, buttonCenter), "Collapsed button cannot be hit");
            Boundary(Mouse.MouseEnterEvent); host.UpdateLayout();
            Check(content.IsVisible && Hits(peek, buttonCenter), "Expansion moved the target out from under the pointer");
            Check((peek.TranslatePoint(new Point(), host) - closedPosition).Length < .1, "Header toggle moved during expansion");
            Check(Hits(header, header.TranslatePoint(new Point(4, 9), host)), "Blank header area is not part of the hover box");
            var icon = (Button)panel.FindName("HistoryButton");
            Check(Hits(header, icon.TranslatePoint(new Point(15, 15), host)), "Header icon is outside the hover box");
            await Task.Delay(650);
            Check(content.IsVisible && !timer.IsEnabled, "Remaining inside the whole header should keep it open");
            Boundary(Mouse.MouseLeaveEvent); await Task.Delay(200);
            Check(content.IsVisible && timer.IsEnabled, "Leaving must keep the header open during the half-second grace period");
            Boundary(Mouse.MouseEnterEvent); await Task.Delay(650);
            Check(content.IsVisible && !timer.IsEnabled, "Reentry must cancel the old collapse deadline");
            Boundary(Mouse.MouseLeaveEvent); await Task.Delay(200);
            Check(content.IsVisible, "Header collapsed before 500 ms");
            await Task.Delay(500); host.UpdateLayout();
            Check(content.Visibility == Visibility.Collapsed && !timer.IsEnabled, "Header did not collapse after leaving the whole box");
            Check((peek.TranslatePoint(new Point(), host) - closedPosition).Length < .1, "Header toggle moved during collapse");

            Boundary(Mouse.MouseEnterEvent); host.UpdateLayout();
            Check(Hits(peek, buttonCenter), "Expanded button cannot be double-clicked at its original position");
            peek.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            Check(book.Active.HeaderPinned, "Double-click did not pin the header");
            Boundary(Mouse.MouseLeaveEvent); await Task.Delay(700);
            Check(content.IsVisible, "Pinned header collapsed on mouse leave");
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Active.HeaderPinned, "Pinned header state did not persist");
            peek.RaiseEvent(CreateKey(host, Key.Space, Keyboard.PreviewKeyDownEvent)); host.UpdateLayout();
            Check(!book.Active.HeaderPinned && content.Visibility == Visibility.Collapsed, "Space did not unpin the header");
            peek.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent)); host.UpdateLayout();
            Check(book.Active.HeaderPinned && content.IsVisible, "Enter did not pin the header");
            peek.RaiseEvent(CreateKey(host, Key.Enter, Keyboard.PreviewKeyDownEvent));

            Boundary(Mouse.MouseEnterEvent); Boundary(Mouse.MouseLeaveEvent);
            Invoke(panel, "ShowCurrentConversation");
            Check(!timer.IsEnabled && content.Visibility == Visibility.Collapsed, "Conversation switch retained a pending hover timeout");
            Boundary(Mouse.MouseEnterEvent); Boundary(Mouse.MouseLeaveEvent);
            host.Content = null; await Task.Delay(50);
            Check(!timer.IsEnabled && !(bool)Field(panel, "_headerTemporary")!, "Unloading retained hover state/timer");
            host.Content = panel; await Task.Delay(100); host.UpdateLayout();
            Boundary(Mouse.MouseEnterEvent); host.UpdateLayout();
            Check(content.IsVisible, "Reopening the panel did not restore hover behavior");
            Capture(host, Path.Combine(output, "ai-header-expanded.png"));
            Boundary(Mouse.MouseLeaveEvent);
            panel.Dispose(); panelDisposed = true;
            Check(!timer.IsEnabled, "Disposing the panel left its hover timer running");
        }
        finally { host.Close(); if (!panelDisposed) panel.Dispose(); }
        Console.WriteLine($"AI header: {checks} checks passed (WPF hit geometry, stationary button, whole-box hover events, delayed collapse, reentry, double-click, keyboard, persistence and lifetime).");
    }
}
