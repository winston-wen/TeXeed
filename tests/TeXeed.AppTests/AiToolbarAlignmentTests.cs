using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiToolbarAlignmentAsync(MainWindow owner, string output)
    {
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        panel.InitializeBranchUi();
        var host = new Window { Owner = owner, Content = panel, Width = 850, Height = 560 };
        host.Show();
        try
        {
            await panel.SetProjectAsync(Path.Combine(output, "toolbar.tex"));
            var book = (AiNotebook)Field(panel, "_book")!;
            var longAnswer = string.Join("\n\n", Enumerable.Range(0, 45).Select(i =>
                $"第 {i + 1} 段：这里用长回答模拟用户滚动阅读，并且故意将上一行文字滚动到视口边界之外。"));
            book.Turns.Add(new AiTurn("模拟阅读中的长回答", longAnswer));
            Invoke(panel, "RefreshTranscript");
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var view in Descendants<MarkdownView>(panel)) await view.RenderTask;
            host.UpdateLayout();
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            scroll.ScrollToVerticalOffset(330);
            host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (scroll.VerticalOffset <= 0) throw new Exception("Long-answer fixture did not scroll");
            foreach (var scale in new[] { 1d, 1.5d })
            {
                panel.LayoutTransform = new ScaleTransform(scale, scale);
                foreach (var live in new[] { false, true })
                {
                    ((FrameworkElement)panel.FindName("LiveContainer")).Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                    host.UpdateLayout();
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var history = (FrameworkElement)panel.FindName("History");
                    var end = history.TranslatePoint(new Point(0, 0), panel).Y;
                    var expected = end / 2;
                    foreach (var label in new[] { "↑ 上游会话", "↓ 下游会话", "会话关系" })
                    {
                        var button = Descendants<Button>(panel).Single(b => Equals(b.Content, label));
                        var text = Descendants<TextBlock>(button).Single();
                        var dpi = VisualTreeHelper.GetDpi(text);
                        var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                            text.FontSize, Brushes.White, dpi.PixelsPerDip);
                        var ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
                        var center = text.TranslatePoint(new Point(0, 0), panel).Y + ink.Top + ink.Height / 2;
                        Console.WriteLine($"Toolbar scale={scale} live={live} dpi={dpi.PixelsPerDip:F2} {label}: glyph={center:F2}, boundary center={expected:F2}, viewport={end:F2}");
                        if (Math.Abs(center - expected) > 2.5)
                            throw new Exception($"Toolbar glyph {label} is not centered in the region above the reading viewport");
                    }
                    Capture(host, Path.Combine(output, $"toolbar-{scale}-{live}.png"));
                }
            }
        }
        finally { await panel.StopAndSaveAsync(); host.Content = null; host.Close(); }
    }
}
