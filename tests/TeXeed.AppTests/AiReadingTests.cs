using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiReadingAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool condition, string error) { if (!condition) throw new Exception(error); checks++; }
        new AiCredentials { ApiKey = "fixture-never-send", UseApiKey = true }.Save();
        var handler = new AiFakeHandler(); using var connection = new AiConnection(handler); using var panel = new AiPanel(connection);
        var host = new Window { Owner = owner, Width = 820, Height = 900, Content = panel, Title = "AI 对话 · 正式界面验证" };
        host.Show();
        var project = Path.Combine(output, "reading-project.tex");
        await panel.SetProjectAsync(project);
        var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        await panel.Settings.SaveSelectionAsync(CancellationToken.None);
        var book = (AiNotebook)Field(panel, "_book")!; book.Turns.Clear(); book.Entries.Clear();
        var sent = DateTimeOffset.Parse("2026-10-16T10:44:09+08:00");
        var snapshot = new AiEntry { Number = 1, Question = "合成例题：计算六面骰子的平均点数", Sources = [new("main.tex", 1, 1, 0, "合成正文 **加粗** 与 $x^2$。", "revision")] };
        var pdf = new AiEntry { Number = 2, Question = "查看 PDF 圈选", PdfBlocks = [new(2, 0, 0, 1, 1, "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a70sAAAAASUVORK5CYII=")] };
        var turn = new AiTurn("这些问题", "## 第一节\n\n正文包含 $x^2$。\n\n### 子标题\n\n子节正文。\n\n## 第二节\n\n其他正文。")
        { Prompt = "这些问题", SentAt = sent, AnsweredAt = sent.AddSeconds(54), SentAnnotations = [snapshot, pdf] };
        book.Turns.Add(turn); Invoke(panel, "RefreshTranscript"); await Task.Delay(200); host.UpdateLayout();
        var view = Descendants<AiTurnView>(panel).Single();
        Check(turn.SentTime == "2026-10-16 (+8) 10:44:09 发送" && turn.AnswerTime == "2026-10-16 (+8) 10:45:03 答复", "Absolute timestamps changed");
        Check(AiTimestamp.Delay(sent, sent.AddSeconds(91)) == "1 分 31 秒后", "Minute duration missing");
        Check(!Descendants<TextBlock>(view).Any(t => t.Text == "答复"), "Legacy answer label remains");
        var answer = view.Answers.Single();
        var heading = ((Paragraph)((Section)answer.Document.Blocks.First()).Blocks.First()).Inlines.OfType<Hyperlink>().Single();
        heading.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        Check(turn.CollapsedSections.Values.Any(x => x), "Heading collapse was not recorded");
        Check(!new TextRange(answer.Document.ContentStart, answer.Document.ContentEnd).Text.Contains("子节正文"), "Collapsed heading body still visible");
        view.Toggle(); Check(turn.Collapsed, "Turn did not collapse");
        await panel.SaveAsync(); var persisted = AiStorage.Load(project).Turns.Single();
        Check(persisted.Collapsed && persisted.CollapsedSections.Values.Any(x => x), "Fold state did not persist");
        Check(persisted.SentAnnotations[1].PdfBlocks[0].Page == 2, "PDF snapshot was not saved with turn");
        view.Toggle(); heading.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
        await AwaitMathAsync(answer);
        Capture(host, Path.Combine(output, "ai-reading-expanded.png"));
        host.Width = 420; await Task.Delay(80); host.UpdateLayout();
        Capture(host, Path.Combine(output, "ai-reading-narrow.png")); host.Width = 820;
        var legacyProject = Path.Combine(output, "legacy-reading.tex");
        var legacyEntry = new AiEntry { Number = 8, Title = "历史圈点", Question = "旧批注", State = "已完成", Sources = [new("old.tex", 2, 2, 0, "原始内容", "old-revision")] };
        var legacy = new AiNotebook(); legacy.Entries.Add(legacyEntry); legacy.Turns.Add(new("旧统一说明\n#8 · 历史圈点：旧批注", "旧回答"));
        AiStorage.Save(legacyProject, legacy); await panel.SetProjectAsync(legacyProject); await panel.SaveAsync();
        var restored = AiStorage.Load(legacyProject);
        Check(restored.Entries.Single().Question == "旧批注" && restored.Entries.Single().Sources.Single().Text == "原始内容", "Legacy draft was changed or deleted");
        Check(restored.Turns.Single().Question == legacy.Turns.Single().Question && restored.Turns.Single().SentAnnotations.Count == 0, "Legacy turn was rewritten");
        // A tall question keeps the same folded turn at the viewport top.
        // Expanded -> folded must hide the existing bar without another scroll.
        var contextBook = (AiNotebook)Field(panel, "_book")!;
        contextBook.Turns.Clear(); contextBook.Entries.Clear();
        var contextTurn = new AiTurn(string.Join("\n\n", Enumerable.Repeat("滚动阅读提示测试问题", 12)), "保持展开时可显示悬浮问题提示。");
        contextBook.Turns.Add(contextTurn);
        contextBook.Turns.Add(new("后续问答", string.Join("\n\n", Enumerable.Repeat("后续正文用于提供滚动空间。", 40))));
        Invoke(panel, "RefreshTranscript"); await Task.Delay(150); host.UpdateLayout();
        var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
        var bar = (Border)panel.FindName("ReadingContext");
        scroll.ScrollToVerticalOffset(120); await Task.Delay(80); host.UpdateLayout();
        Invoke(panel, "UpdateReadingContext");
        Check(bar.IsVisible && ((TextBlock)panel.FindName("ReadingContextTitle")).Text == "滚动阅读提示测试问题", "Expanded turn no longer shows its reading context");
        var contextView = Descendants<AiTurnView>(panel).Single(v => v.Turn == contextTurn);
        contextView.Toggle(); host.UpdateLayout(); await Task.Delay(80);
        Check(bar.Visibility == Visibility.Collapsed, "Folding kept the floating reading context visible");
        scroll.ScrollToVerticalOffset(160); await Task.Delay(80); host.UpdateLayout();
        Invoke(panel, "UpdateReadingContext");
        Check(bar.Visibility == Visibility.Collapsed, "Scrolling a folded question showed its floating context");
        await panel.SaveAsync(); Invoke(panel, "RefreshTranscript"); await Task.Delay(80);
        scroll.ScrollToVerticalOffset(120); host.UpdateLayout(); Invoke(panel, "UpdateReadingContext");
        Check(bar.Visibility == Visibility.Collapsed, "Restoring a folded question revived its floating context");
        contextView = Descendants<AiTurnView>(panel).Single(v => v.Turn == contextTurn);
        contextView.Toggle(); host.UpdateLayout(); await Task.Delay(80);
        Check(bar.IsVisible, "Expanding a question did not restore the reading context");
        host.Close();
        Console.WriteLine($"AI reading: {checks} checks passed (native layout, snapshots, folds, persistence, zero-request annotation confirmation, composer batch send).");
    }
}
