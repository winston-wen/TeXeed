using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiCompactAsync(MainWindow owner, string output)
    {
        var checks = 0; void Check(bool ok, string error) { checks++; if (!ok) throw new Exception(error); }
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Width = 620, Height = 850, Content = panel, Title = "AI 编辑栏与过程记录验证" };
        host.Show(); await panel.SetProjectAsync(Path.Combine(output, "compact.tex"));
        var input = (TextBox)panel.FindName("Composer"); var numbers = (TextBoxLineNumbers)panel.FindName("ComposerLines");
        await Task.Delay(80); host.UpdateLayout();
        Check(Math.Abs(input.ActualHeight - 56) < .1, "Composer does not default to two 20px lines plus padding");
        panel.Visibility = Visibility.Collapsed; host.UpdateLayout(); panel.Visibility = Visibility.Visible; host.UpdateLayout(); await Task.Delay(50);
        Check(Math.Abs(input.ActualHeight - 56) < .1, "Switching away from a conversation changed its editor height");
        Check(numbers.ActualWidth < 25 && numbers.BorderBrush is not null, "Line-number gutter is oversized or lacks its dividing rule");
        input.Text = string.Concat(Enumerable.Repeat("行号不应给自动折行编号。", 24));
        input.CaretIndex = 0; input.ScrollToHome(); await Task.Delay(80); host.UpdateLayout();
        Check(input.LineCount > 2 && numbers.LogicalLineCount == 1, "Soft wraps became logical line numbers");
        Check(numbers.PaintedNumbers.SequenceEqual(new[] { 1 }), "Soft-wrapped continuation has a phantom line number");
        input.Text = "第一行\r\n第二行\n第三行\r第四行"; await Task.Delay(80);
        Check(numbers.LogicalLineCount == 4, "Mixed newline styles changed logical numbering");
        var grip = (Thumb)panel.FindName("ComposerResize");
        void Drag(double dy) { grip.RaiseEvent(new DragDeltaEventArgs(0, dy) { RoutedEvent = Thumb.DragDeltaEvent }); host.UpdateLayout(); }
        Drag(-80); Check(input.ActualHeight > 120, "Editor cannot be dragged taller");
        Drag(500); Check(Math.Abs(input.ActualHeight - 36) < .1, "Editor cannot shrink to one line");
        Drag(-10000); Check(input.ActualHeight <= panel.ActualHeight * .65 + 1, "Resizing consumed the whole conversation viewport");
        input.Height = 56; input.Text = string.Join('\n', Enumerable.Range(1, 80).Select(i => "逻辑行 " + i));
        input.CaretIndex = input.Text.Length; host.UpdateLayout(); input.ScrollToEnd(); await Task.Delay(80); host.UpdateLayout();
        Check(numbers.PaintedNumbers.Contains(80) && !numbers.PaintedNumbers.Contains(1), "Logical numbers did not follow editor scrolling: " + string.Join(',', numbers.PaintedNumbers) + "; offset=" + input.VerticalOffset);
        input.Text = "行号右对齐，分界清晰。此处自动折行仍属于第一行，默认显示两行，可拖动顶部边缘调整高度。"; input.ScrollToHome();
        var segment = new AiSegment { Kind = "activity", Text = "已读取源码 · main.tex", Details = "main.tex:1\n\\documentclass{article}" };
        var other = new AiSegment { Kind = "activity", Text = "编译成功，PDF 预览已更新。" };
        var book = (AiNotebook)Field(panel, "_book")!;
        book.Turns.Add(new("检查公式排版", "完成。") { SentAt = DateTimeOffset.Now, Segments = [segment, other, new() { Kind = "final", Text = "已完成公式校对。" }] });
        Invoke(panel, "RefreshTranscript"); await Task.Delay(100); host.UpdateLayout();
        var rows = Descendants<AiActivityView>(panel).Where(v => v.IsVisible).ToArray();
        Check(rows.Length == 2, "Settled activities did not use the shared text disclosure");
        var row = rows.Single(v => ReferenceEquals(v.DataContext, segment)); var header = (TextBlock)row.Header;
        var link = header.Inlines.OfType<Hyperlink>().Single();
        string Label() => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text));
        Check(header.Inlines.OfType<Run>().First().Text == segment.DisplayTime && Label().EndsWith("（已收起）"), "Time is not leftmost or collapsed state is missing: " + Label() + " / expected=" + segment.DisplayTime + " / actual=" + header.Inlines.OfType<Run>().First().Text);
        Check(!Descendants<ToggleButton>(row).Any() && !Descendants<System.Windows.Shapes.Path>(row).Any(), "A separate disclosure button/arrow remains");
        Check(Math.Abs(rows[1].TranslatePoint(new Point(), panel).Y - rows[0].TranslatePoint(new Point(0, rows[0].ActualHeight), panel).Y - 17) < .5, "Collapsed rows do not have a one-line gap");
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(row.IsExpanded && Label().EndsWith("（已展开）") && ((TextBox)row.Content).Text.Contains("documentclass"), "Clicking the text did not show saved details/state");
        segment.Details = "更新后的完整工具输出";
        Check(((TextBox)row.Content).Text == segment.Details, "Streaming detail updates were lost");
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(!row.IsExpanded && Label().EndsWith("（已收起）"), "Clicking the text did not collapse its details");
        var plain = rows.Single(v => ReferenceEquals(v.DataContext, other));
        ((TextBlock)plain.Header).Inlines.OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(plain.IsExpanded && ((TextBox)plain.Content).Text == other.Text, "An activity without extra details cannot show its complete saved text");
        plain.IsExpanded = false;
        var live = (ObservableCollection<AiSegment>)Field(panel, "_liveSegments")!; live.Add(segment);
        ((StackPanel)panel.FindName("LiveContainer")).Visibility = Visibility.Visible;
        host.UpdateLayout(); await Task.Delay(80);
        Check(Descendants<AiActivityView>((ItemsControl)panel.FindName("LiveSegments")).Any(), "Live output still uses the old boxed activity template");
        ((StackPanel)panel.FindName("LiveContainer")).Visibility = Visibility.Collapsed;
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        { TeXeed.App.Services.ThemeManager.Apply(theme); host.UpdateLayout(); await Task.Delay(50); Capture(host, Path.Combine(output, "ai-compact-" + theme + ".png")); }
        TeXeed.App.Services.ThemeManager.Apply("ristretto"); host.Close();
        Console.WriteLine($"AI compact UI: {checks} checks passed (logical lines, scroll, resize, plain activity text, details and live parity).");
    }
}
