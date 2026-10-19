using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        var snapshot = new AiEntry { Number = 1, Question = "随机函数：随机选择整张函数表", Sources = [new("main.tex", 1, 1, 0, "原文 **加粗** 与 $x^2$。", "revision")] };
        var pdf = new AiEntry { Number = 2, Question = "查看 PDF 圈选", PdfBlocks = [new(2, 0, 0, 1, 1, "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a70sAAAAASUVORK5CYII=")] };
        var turn = new AiTurn("这些问题", "## 第一节\n\n正文包含 $x^2$。\n\n### 子标题\n\n子节正文。\n\n## 第二节\n\n其他正文。")
        { Prompt = "这些问题", SentAt = sent, AnsweredAt = sent.AddSeconds(54), SentAnnotations = [snapshot, pdf] };
        book.Turns.Add(turn); Invoke(panel, "RefreshTranscript"); await Task.Delay(200); host.UpdateLayout();
        var view = Descendants<AiTurnView>(panel).Single();
        Check(turn.Footer == "发送: 2026-10-16 (+8) 10:44:09, 答复: 54 秒后", "Timestamp format/duration changed");
        Check(AiTimestamp.Delay(sent, sent.AddSeconds(91)) == "1 分 31 秒后", "Minute duration missing");
        Check(!Descendants<TextBlock>(view).Any(t => t.Text == "答复"), "Legacy answer label remains");
        view.ShowPreview(snapshot, 1); host.UpdateLayout();
        Check(Descendants<MarkdownView>(view).Any(v => v.Markdown == snapshot.Sources[0].Text), "Source original was not rendered");
        view.ShowPreview(pdf, 2); host.UpdateLayout();
        Check(Descendants<Image>(view).Any(i => i.Source is not null), "PDF snapshot did not load");
        view.ClosePreview();
        var answer = view.Answers.Single();
        var heading = Descendants<Button>(answer).First(b => b.Content is TextBlock text && new TextRange(text.ContentStart, text.ContentEnd).Text.Contains("第一节"));
        heading.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); host.UpdateLayout();
        Check(turn.CollapsedSections.Values.Any(x => x), "Heading collapse was not recorded");
        Check(!new TextRange(answer.Document.ContentStart, answer.Document.ContentEnd).Text.Contains("子节正文"), "Collapsed heading body still visible");
        view.Toggle(); Check(turn.Collapsed, "Turn did not collapse");
        await panel.SaveAsync(); var persisted = AiStorage.Load(project).Turns.Single();
        Check(persisted.Collapsed && persisted.CollapsedSections.Values.Any(x => x), "Fold state did not persist");
        Check(persisted.SentAnnotations[1].PdfBlocks[0].Page == 2, "PDF snapshot was not saved with turn");
        view.Toggle(); heading.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); host.UpdateLayout();
        await AwaitMathAsync(answer);
        Capture(host, Path.Combine(output, "ai-reading-expanded.png"));
        host.Width = 420; await Task.Delay(80); host.UpdateLayout();
        Capture(host, Path.Combine(output, "ai-reading-narrow.png")); host.Width = 820;
        ((TextBox)panel.FindName("Composer")).Text = "保留主编辑区草稿";
        var one = new AiEntry { Question = "仅发送本条" }; var two = new AiEntry { Question = "另一条保留" };
        panel.AddEntry(one); panel.AddEntry(two); await panel.SubmitEntryAsync(one);
        Check(((TextBox)panel.FindName("Composer")).Text == "保留主编辑区草稿", "Single annotation send cleared main draft");
        Check(book.Entries.Single().Id == two.Id && book.Entries[0].Number == 1, "Other pending annotation lost or numbering stale");
        Check(book.Turns.Last().SentAnnotations.Single().Number == 1, "Sent annotations not numbered per turn");
        Check(book.Turns.Last().Prompt == "", "Single annotation included main draft");
        await panel.SaveAsync();
        var legacyProject = Path.Combine(output, "legacy-reading.tex");
        var legacyEntry = new AiEntry { Number = 8, Title = "历史圈点", Question = "旧批注", State = "已完成", Sources = [new("old.tex", 2, 2, 0, "原始内容", "old-revision")] };
        var legacy = new AiNotebook(); legacy.Entries.Add(legacyEntry); legacy.Turns.Add(new("旧统一说明\n#8 · 历史圈点：旧批注", "旧回答"));
        AiStorage.Save(legacyProject, legacy); await panel.SetProjectAsync(legacyProject); await Task.Delay(80);
        var restored = (AiNotebook)Field(panel, "_book")!;
        Check(restored.Turns.Single().SentAnnotations.Single().Number == 1 && restored.Turns.Single().Prompt == "旧统一说明", "Legacy snapshots were not recovered/renumbered");
        Check(restored.Turns.Single().Question.Contains("#8") && restored.Entries.Count == 0, "Legacy migration overwrote original question or retained completed entry");
        Check(restored.Turns.Single().Footer.EndsWith("答复: 未记录"), "Legacy answer was reported as unanswered");
        await panel.SaveAsync(); Check(AiStorage.Load(legacyProject).Turns.Single().SentAnnotations.Single().Sources[0].Text == "原始内容", "Migrated snapshot lost source");
        host.Close();
        Console.WriteLine($"AI reading: {checks} checks passed (native layout, snapshots, folds, persistence, isolated single send).");
    }
}
