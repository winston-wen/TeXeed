using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiBookmarksAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        {
            Paragraph paragraph => [paragraph],
            Section section => Paragraphs(section.Blocks),
            _ => []
        });
        IEnumerable<Run> Runs(InlineCollection inlines) => inlines.SelectMany(i => i is Run r ? [r] : i is Span s ? Runs(s.Inlines) : []);
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Content = panel, Width = 850, Height = 560, Title = "AI 书签 · 合成会话" };
        host.Show();
        try
        {
            var project = Path.Combine(output, "bookmarks.tex");
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var source = "# 首个标题\n\n## 重复标题甲\n\n锚点内容甲，用户稍后定位。\n\n| 项目 | 内容 |\n| --- | --- |\n| 甲 | 表格选区 |\n\n$$a+b$$\n\n## 重复标题乙\n\n$$c+d\\tag{A}$$\n\n```tex\n# 伪标题\n$$not-a-formula$$\n```\n\n" +
                string.Join("\n\n", Enumerable.Range(0, 45).Select(i => $"第 {i} 段合成正文，验证较长对话在索引和虚拟化时不会遗漏内容。"));
            for (var i = 0; i < 120; i++)
                book.Turns.Add(new AiTurn("用户问题第 " + i + " 轮", source) { SentAt = DateTimeOffset.UnixEpoch, Segments = [new AiSegment { Id = "final-" + i, Kind = "final", Text = source }] });
            ((TextBox)panel.FindName("Composer")).Text = "保留草稿"; Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var visibleBefore = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 });
            var bytes = GC.GetAllocatedBytesForCurrentThread(); var watch = System.Diagnostics.Stopwatch.StartNew();
            var initial = panel.GetBookmarks();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); watch.Stop();
            var firstMs = watch.Elapsed.TotalMilliseconds; var firstBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            bytes = GC.GetAllocatedBytesForCurrentThread(); watch.Restart(); var reused = panel.GetBookmarks();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); watch.Stop();
            var reusedMs = watch.Elapsed.TotalMilliseconds; var reusedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            var visibleAfter = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 });
            var performance = new { turns = book.Turns.Count, sourceCharacters = source.Length * book.Turns.Count, bookmarks = initial.Count,
                firstMs, firstBytes, reusedMs, reusedBytes, visibleBefore, visibleAfter };
            File.WriteAllText(Path.Combine(output, "bookmark-index-performance.json"), System.Text.Json.JsonSerializer.Serialize(performance));
            Console.WriteLine("Bookmark index performance: " + System.Text.Json.JsonSerializer.Serialize(performance));
            Check(initial.Count == reused.Count && visibleBefore == visibleAfter, "Indexing changed the visible conversation controls");
            var defaults = initial.Where(b => b.TurnIndex == 0).ToArray();
            Check(defaults.Select(b => b.Label).SequenceEqual(["1:h1", "1:h2", "1:eq1", "1:h2", "1:eqA"]), "Default labels, source order, duplicate headings or tags are wrong");
            Check(defaults[1].Description.Contains("重复标题甲") && defaults[3].Description.Contains("重复标题乙"), "Duplicate heading descriptions are lost");
            Check(defaults.All(b => !b.Description.Contains("伪标题") && !b.Description.Contains("not-a-formula")), "Code block was indexed as a bookmark");
            Check(panel.GetBookmarks().Select(b => b.Id).SequenceEqual(panel.GetBookmarks().Select(b => b.Id)), "Repeated indexes are unstable");
            var changed = book.Turns[79].Segments[0]; var original = changed.Text;
            changed.Text += "\n\n### 新增标题";
            Check(panel.GetBookmarks().Any(b => b.TurnIndex == 79 && b.Label == "80:h3"), "Changed answer did not refresh its cached index");
            changed.Text = original;
            Check(panel.GetBookmarks().All(b => b.TurnIndex != 79 || b.Label != "80:h3"), "Stale cached heading remained after an answer changed");

            var target = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:h2" && b.Description.Contains("甲"));
            var outer = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:h1");
            var turn = book.Turns[60]; turn.Collapsed = true;
            turn.CollapsedSections[outer.SegmentId + ":" + outer.Position] = true;
            turn.CollapsedSections[target.SegmentId + ":" + target.Position] = true;
            Check(await panel.NavigateBookmarkAsync(target), "Cannot navigate to an offscreen folded heading");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!turn.Collapsed, "Bookmark did not expand the turn");
            Check(!turn.CollapsedSections[outer.SegmentId + ":" + outer.Position], "Bookmark did not reopen its folded ancestor");
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var turnView = views[turn.Id];
            var answer = turnView.Answers.Single();
            var heading = Paragraphs(answer.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("重复标题甲"));
            var toggle = heading.Inlines.OfType<Hyperlink>().Single();
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            var body = Paragraphs(answer.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("锚点内容甲"));
            var text = Runs(body.Inlines).Single(r => r.Text.Contains("锚点内容甲"));
            answer.Selection.Select(text.ContentStart, text.ContentEnd);
            var captured = panel.CaptureBookmarkSelection(answer);
            Check(captured is not null && captured.FoldKeys.Count > 0, "Answer selection did not capture its folded section");
            answer.Selection.Select(text.ContentStart, text.ContentStart);
            Check(panel.AddBookmarkFromSelection(captured!, "回答锚点"), "Captured answer selection was lost after selection changed");
            var saved = panel.GetBookmarks().Single(b => b.Label == "回答锚点");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            Check(await panel.NavigateBookmarkAsync(saved), "Cannot navigate to a saved selection inside a folded heading");
            Check(!turn.CollapsedSections[target.SegmentId + ":" + target.Position], "Bookmark did not reopen the target section");
            var tableView = Descendants<MarkdownView>(answer).Single();
            var table = (Table)tableView.Document.Blocks.FirstBlock!;
            var cell = table.RowGroups[0].Rows[1].Cells[1];
            var cellRun = Runs(((Paragraph)cell.Blocks.FirstBlock!).Inlines).Single(r => r.Text.Contains("表格选区"));
            tableView.Focus(); tableView.Selection.Select(cellRun.ContentStart, cellRun.ContentEnd); host.UpdateLayout();
            Check(ReferenceEquals(tableView.ContextMenu, answer.ContextMenu), "Scrollable table does not expose the answer context menu");
            var tableSelection = panel.CaptureBookmarkSelection(answer);
            Check(tableSelection is not null && tableSelection.Anchor.Quote.Contains("表格选区"), "Table text selection was not captured");
            Check(panel.AddBookmarkFromSelection(tableSelection!, "表格锚点"), "Cannot save a table bookmark");
            var tableTarget = panel.GetBookmarks().Single(b => b.Label == "表格锚点");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            Check(await panel.NavigateBookmarkAsync(tableTarget), "Cannot navigate to a table selection inside a folded section");
            var formulaTarget = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:eqA");
            Check(await panel.NavigateBookmarkAsync(formulaTarget), "Cannot navigate to a tagged display formula");

            var questionView = turnView.Questions[0];
            var questionParagraph = (Paragraph)questionView.Document.Blocks.FirstBlock!;
            questionView.Selection.Select(questionParagraph.ContentStart, questionParagraph.ContentEnd);
            var question = panel.CaptureBookmarkSelection(questionView);
            Check(question is not null && question.Anchor.SegmentId.StartsWith("@question:"), "User question selection was not captured separately");
            Check(panel.AddBookmarkFromSelection(question!, "问题锚点"), "Cannot save a question bookmark");
            var questionTarget = panel.GetBookmarks().Single(b => b.Label == "问题锚点");
            Check(await panel.NavigateBookmarkAsync(questionTarget), "Cannot navigate to a user question bookmark");
            await panel.SaveAsync();
            var loaded = AiStorage.Load(project);
            Check(loaded.Active.Bookmarks.Count == 3 && loaded.Draft == "保留草稿", "Bookmark persistence changed the saved draft or lost bookmarks");
            Check(panel.RemoveBookmark(questionTarget) && panel.GetBookmarks().All(b => b.Id != questionTarget.Id), "User bookmark deletion failed");
            var pending = new AiTurn("实时普通提问", "[本轮未完成]") { SentAt = DateTimeOffset.UtcNow, Prompt = "实时普通提问", Incomplete = true };
            var liveQuestion = (MarkdownView)panel.FindName("LiveQuestion");
            liveQuestion.Markdown = pending.DisplayQuestion;
            ((FrameworkElement)panel.FindName("LiveContainer")).Visibility = Visibility.Visible;
            typeof(AiPanel).GetField("_foldingLiveTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, pending);
            var liveParagraph = (Paragraph)liveQuestion.Document.Blocks.FirstBlock!;
            liveQuestion.Selection.Select(liveParagraph.ContentStart, liveParagraph.ContentEnd);
            Check(panel.CaptureBookmarkSelection(liveQuestion) is null, "Live bookmark was offered before the accepted question was journaled");
            var checkpoint = await AiTurnCheckpoint.StartAsync(project, book.Active.Id, book.Active.Title, pending, new AiStreamBuffer());
            typeof(AiPanel).GetField("_liveBookmarkCheckpointReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, true);
            var liveSelection = panel.CaptureBookmarkSelection(liveQuestion);
            var annotated = pending with { SentAnnotations = [new AiEntry { Question = "圈点说明" }] };
            typeof(AiPanel).GetField("_foldingLiveTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, annotated);
            Check(panel.CaptureBookmarkSelection(liveQuestion) is null && liveSelection is not null && !panel.AddBookmarkFromSelection(liveSelection, "不应保存"),
                "A live question with annotations accepted an unstable bookmark");
            typeof(AiPanel).GetField("_foldingLiveTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, pending);
            Check(liveSelection is not null && panel.AddBookmarkFromSelection(liveSelection, "实时问题标签"), "Stable live question cannot be bookmarked");
            var liveTarget = panel.GetBookmarks().Single(b => b.Label == "实时问题标签");
            Check(await panel.NavigateBookmarkAsync(liveTarget), "Cannot navigate to a live question bookmark");
            await panel.SaveAsync();
            var recovered = AiStorage.Load(project);
            Check(recovered.Active.Bookmarks.Any(b => b.Label == "实时问题标签") && recovered.Turns.Any(t => t.Id == pending.Id), "Checkpoint recovery lost the live question bookmark");
            await checkpoint.StopAsync();
            book.Turns.Add(pending with { Answer = "实时轮次已结束", AnsweredAt = DateTimeOffset.UtcNow, Incomplete = false });
            typeof(AiPanel).GetField("_foldingLiveTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, null);
            typeof(AiPanel).GetField("_liveBookmarkCheckpointReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, false);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            Check(await panel.NavigateBookmarkAsync(panel.GetBookmarks().Single(b => b.Label == "实时问题标签")), "Live question bookmark did not survive completion");
            await panel.SaveAsync(); checkpoint.Commit();
            await panel.DeleteTurnAsync(pending.Id);
            Check(book.Active.Bookmarks.All(b => b.Label != "实时问题标签"), "Deleting a turn left an orphaned user bookmark");
            var legacyTurn = new AiTurn("旧提问", "## 旧回答标题\n\n旧正文。\n\n$$x+y$$");
            book.Turns.Add(legacyTurn); Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            var legacyTarget = panel.GetBookmarks().Single(b => b.TurnId == legacyTurn.Id && b.Kind == AiBookmarkKind.Heading);
            Check(legacyTarget.Label == "121:h2" && await panel.NavigateBookmarkAsync(legacyTarget), "Legacy answer without segments cannot be indexed and located");
            await panel.NewConversationAsync();
            Check(((System.Collections.IDictionary)Field(panel, "_bookmarkCache")!).Count == 0, "Switching conversations retained the old default index");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<AiConversation>("{\"Title\":\"旧会话\",\"Draft\":\"旧草稿\"}")!;
            Check(legacy.Bookmarks.Count == 0 && legacy.BookmarkBallX is null && legacy.BookmarkSort == "document" && legacy.Draft == "旧草稿", "Old session migration lost data");
            Console.WriteLine($"AI bookmarks: {checks} checks passed (ordered duplicate headings, formulas, code exclusion, offscreen/folded navigation, captured answer and question selections, persistence and old sessions).");
        }
        finally { host.Close(); }
    }
}
