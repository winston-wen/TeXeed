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
        var largeNumbers = new AiHeadingNumbers([int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue - 1]);
        Check(largeNumbers.Next(6) == string.Join('.', Enumerable.Repeat(int.MaxValue, 6)), "Large multi-digit heading counters were truncated");
        var rollover = new AiHeadingNumbers([9, 99, 0, 0, 0, 0]);
        Check(rollover.Next(2) == "9.100" && rollover.Next(1) == "10" && rollover.Next(2) == "10.1", "Heading counters did not carry or reset at multiple digits");
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
            var source = "# 首个标题\n\n## 重复标题甲\n\n锚点内容甲，用户稍后定位。\n\n行内代码 `inline.tex` 和行内公式 $x_i$ 也可定位。\n\n| 项目 | 内容 |\n| --- | --- |\n| 甲 | 表格选区 |\n\n$$a+b$$\n\n## 重复标题乙\n\n$$c+d\\tag{A}$$\n\n```tex\n# 伪标题\n$$not-a-formula$$\n```\n\n" +
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
            Check(defaults.Select(b => b.Label).SequenceEqual(["1:1", "1:1.1", "1:eq1", "1:1.2", "1:eqA"]), "Default labels, source order, duplicate headings or tags are wrong");
            Check(defaults[1].Description.Contains("重复标题甲") && defaults[3].Description.Contains("重复标题乙"), "Duplicate heading descriptions are lost");
            Check(defaults.All(b => !b.Description.Contains("伪标题") && !b.Description.Contains("not-a-formula")), "Code block was indexed as a bookmark");
            Check(panel.GetBookmarks().Select(b => b.Id).SequenceEqual(panel.GetBookmarks().Select(b => b.Id)), "Repeated indexes are unstable");
            var numberingTurn = book.Turns[0];
            var numberingFirst = numberingTurn.Segments[0]; var numberingOriginal = numberingFirst.Text;
            numberingFirst.Text = "## 1. 已有标题\n\n### 深一级\n\n## 下一标题\n\n```md\n# 不是标题\n```";
            numberingTurn.Segments.Add(new AiSegment { Id = "numbering-activity", Kind = "activity", Text = "工具过程" });
            numberingTurn.Segments.Add(new AiSegment { Id = "numbering-next", Kind = "final", Text = "# 新上级\n\n## 2. 已有编号" });
            var numbered = panel.GetBookmarks().Where(b => b.TurnIndex == 0 && b.Kind == AiBookmarkKind.Heading).ToArray();
            Check(numbered.Select(b => b.Label).SequenceEqual(["1:0.1", "1:0.1.1", "1:0.2", "1:1", "1:1.1"]), "Heading counters did not cross answer segments or handle missing ancestors");
            var numberingView = new AiTurnView { TurnNumber = 1, Turn = numberingTurn };
            var rendered = numberingView.Answers.SelectMany(a => Paragraphs(a.Document.Blocks)).Where(p => p.Inlines.FirstInline is Hyperlink)
                .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim()).ToArray();
            Check(rendered.Length == numbered.Length && rendered.Zip(numbered).All(pair => pair.First.StartsWith("[" + pair.Second.Label + "]")), "Displayed markers differ from bookmark numbers across segments");
            Check(rendered[0].Contains("1. 已有标题") && rendered[4].Contains("2. 已有编号"), "Existing heading numbers were altered");
            numberingView.TurnNumber = 9;
            Check(new TextRange(Paragraphs(numberingView.Answers[1].Document.Blocks).First().ContentStart, Paragraphs(numberingView.Answers[1].Document.Blocks).First().ContentEnd).Text.StartsWith("[9:1]"), "Changing turn number lost the cross-segment numbering");
            numberingFirst.Text = "# 长回答\n\n" + new string('长', 4500);
            numberingTurn.Segments[2].Text = "## 延迟排版后的章节";
            var longNumberingView = new AiTurnView { TurnNumber = 1, Turn = numberingTurn };
            await Task.WhenAll(longNumberingView.Answers.Select(view => view.RenderTask));
            var followingHeading = Paragraphs(longNumberingView.Answers[1].Document.Blocks).First();
            Check(new TextRange(followingHeading.ContentStart, followingHeading.ContentEnd).Text.StartsWith("[1:1.1]"), "Asynchronous rendering changed the next segment's numbering");
            numberingFirst.Text = numberingOriginal; numberingTurn.Segments.RemoveRange(1, 2);
            var changed = book.Turns[79].Segments[0]; var original = changed.Text;
            changed.Text += "\n\n### 新增标题";
            Check(panel.GetBookmarks().Any(b => b.TurnIndex == 79 && b.Label == "80:1.2.1"), "Changed answer did not refresh its cached index");
            changed.Text = original;
            Check(panel.GetBookmarks().All(b => b.TurnIndex != 79 || b.Label != "80:1.2.1"), "Stale cached heading remained after an answer changed");

            var target = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:1.1" && b.Description.Contains("甲"));
            var outer = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:1");
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
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var markerLayers = (Dictionary<MarkdownView, AiBookmarkMarkerLayer>)Field(panel, "_bookmarkMarkerLayers")!;
            Check(markerLayers.TryGetValue(answer, out var answerMarker) && answerMarker.Positions.Count == 1, "Answer bookmark did not show its margin marker");
            Check(panel.AddBookmarkFromSelection(captured!, "同一行第二标签"), "Second bookmark could not be added");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(answerMarker!.Positions.Count == 1 && !answerMarker.IsHitTestVisible, "Same-line markers must merge and preserve text selection");
            Check(panel.RemoveBookmark(panel.GetBookmarks().Single(b => b.Label == "同一行第二标签")), "Second bookmark could not be removed");
            var laterParagraph = Paragraphs(answer.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("第 0 段合成正文"));
            var laterRun = Runs(laterParagraph.Inlines).Single(r => r.Text.Contains("第 0 段合成正文"));
            answer.Selection.Select(laterRun.ContentStart, laterRun.ContentEnd);
            var laterSelection = panel.CaptureBookmarkSelection(answer);
            Check(laterSelection is not null && panel.AddBookmarkFromSelection(laterSelection, "折叠后仍可见"), "Cannot create later-section bookmark");
            var repeatedOffset = laterRun.Text.IndexOf("段合成正文", StringComparison.Ordinal);
            var repeatedStart = laterRun.ContentStart.GetPositionAtOffset(repeatedOffset)!;
            answer.Selection.Select(repeatedStart, repeatedStart.GetPositionAtOffset("段合成正文".Length)!);
            var repeatedSelection = panel.CaptureBookmarkSelection(answer);
            Check(repeatedSelection is not null && panel.AddBookmarkFromSelection(repeatedSelection, "重复选文"), "Cannot create repeated-text bookmark");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(answerMarker.Positions.Count == 1, "Bookmarks on the same visible line did not merge after an earlier fold");
            var repeatedSaved = book.Active.Bookmarks.Single(b => b.Label == "重复选文");
            var repeatedMarker = answer.BookmarkMarkerPoint(repeatedSaved);
            var repeatedRect = repeatedStart.GetCharacterRect(LogicalDirection.Forward);
            Check(repeatedMarker is { } position && Math.Abs(position.Y - repeatedRect.Y) < 1,
                "A repeated quote did not use its stored paragraph context to recover its exact position");
            var ambiguousLegacy = repeatedSaved with { Id = "legacy-ambiguous", ContextBefore = null, ContextAfter = null };
            Check(answer.BookmarkMarkerPoint(ambiguousLegacy) is null, "An old ambiguous quote must not be drawn at a guessed position");
            book.Active.Bookmarks.Add(ambiguousLegacy);
            var ambiguousTarget = panel.GetBookmarks().Single(b => b.Id == ambiguousLegacy.Id);
            Check(!await panel.NavigateBookmarkAsync(ambiguousTarget), "An ambiguous old bookmark jumped to an unrelated repeated quote");
            book.Active.Bookmarks.Remove(ambiguousLegacy);
            answer.Selection.Select(laterRun.ContentStart, laterRun.ContentEnd);
            var createdWhileFolded = panel.CaptureBookmarkSelection(answer);
            Check(createdWhileFolded is not null && panel.AddBookmarkFromSelection(createdWhileFolded, "折叠时创建"), "Cannot bookmark while an earlier section is collapsed");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            var reopenedTarget = panel.GetBookmarks().Single(b => b.Label == "折叠时创建");
            Check(await panel.NavigateBookmarkAsync(reopenedTarget), "Cannot navigate after reopening an earlier section");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var reopenedMarker = answer.BookmarkMarkerPoint(book.Active.Bookmarks.Single(b => b.Id == reopenedTarget.Id));
            Check(reopenedMarker is { } reopened && Math.Abs(reopened.Y - laterRun.ContentStart.GetCharacterRect(LogicalDirection.Forward).Y) < 1,
                "A bookmark created with previous sections folded jumped after they were reopened");
            Check(panel.RemoveBookmark(reopenedTarget), "Cannot remove bookmark created while folded");
            Check(panel.RemoveBookmark(panel.GetBookmarks().Single(b => b.Label == "折叠后仍可见")), "Cannot remove later-section bookmark");
            Check(panel.RemoveBookmark(panel.GetBookmarks().Single(b => b.Label == "重复选文")), "Cannot remove repeated-text bookmark");
            var inlineParagraph = Paragraphs(answer.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("行内代码"));
            var inlineCode = Runs(inlineParagraph.Inlines).Single(r => r.Text == "inline.tex");
            answer.Selection.Select(inlineCode.ContentStart, inlineCode.ContentEnd);
            var inlineSelection = panel.CaptureBookmarkSelection(answer);
            Check(inlineSelection is not null && panel.AddBookmarkFromSelection(inlineSelection, "行内代码标签"), "Inline code selection could not be bookmarked");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(answerMarker.Positions.Count == 2, "Inline code bookmark did not appear on its own line");
            var inlineFormula = inlineParagraph.Inlines.OfType<InlineUIContainer>().Single();
            answer.Selection.Select(inlineFormula.ElementStart, inlineFormula.ElementEnd);
            var formulaSelection = panel.CaptureBookmarkSelection(answer);
            Check(formulaSelection is not null && panel.AddBookmarkFromSelection(formulaSelection, "行内公式标签"), "Inline formula selection could not be bookmarked");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(answerMarker.Positions.Count == 2, "Same-line inline code and formula bookmarks must share one marker");
            Check(panel.RemoveBookmark(panel.GetBookmarks().Single(b => b.Label == "行内代码标签")), "Cannot remove inline code bookmark");
            Check(panel.RemoveBookmark(panel.GetBookmarks().Single(b => b.Label == "行内公式标签")), "Cannot remove inline formula bookmark");
            var messages = (ScrollViewer)panel.FindName("MessagesScroll");
            var benchmarkOffset = messages.VerticalOffset;
            async Task<object> MarkerCost()
            {
                host.Width = 850; messages.ScrollToVerticalOffset(benchmarkOffset);
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                async Task<(double Ms, long Bytes, double[] Times, long[] Allocations)> Measure(Func<int, Task> action)
                {
                    await action(0); var times = new List<double>(); var allocations = new List<long>();
                    for (var i = 1; i <= 7; i++)
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
                        await action(i); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        timer.Stop(); times.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                    }
                    return (times.Order().ElementAt(3), allocations.Order().ElementAt(3), times.ToArray(), allocations.ToArray());
                }
                var scroll = await Measure(i => { messages.ScrollToVerticalOffset(Math.Max(0, messages.VerticalOffset + (i % 2 == 0 ? -12 : 12))); return Task.CompletedTask; });
                var resize = await Measure(i => { host.Width = i % 2 == 0 ? 850 : 820; return Task.CompletedTask; });
                return new { ScrollMs = scroll.Ms, ResizeMs = resize.Ms, ScrollBytes = scroll.Bytes, ResizeBytes = resize.Bytes,
                    ScrollSamplesMs = scroll.Times, ResizeSamplesMs = resize.Times, ScrollSamplesBytes = scroll.Allocations, ResizeSamplesBytes = resize.Allocations,
                    Controls = Descendants<Control>(panel).Count(c => c.IsVisible && VisibleBounds(c, panel) is { Width: > 0, Height: > 0 }) };
            }
            var originalBookmark = book.Active.Bookmarks.Single(b => b.Label == "回答锚点");
            book.Active.Bookmarks.Remove(originalBookmark); Invoke(panel, "RefreshBookmarkMarkers");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var noMarkerCost = await MarkerCost();
            book.Active.Bookmarks.Add(originalBookmark);
            var many = new List<AiUserBookmark>();
            foreach (var paragraph in Paragraphs(answer.Document.Blocks).Where(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("段合成正文")).Take(30))
            {
                var run = Runs(paragraph.Inlines).First(r => r.Text.Contains("段合成正文"));
                answer.Selection.Select(run.ContentStart, run.ContentEnd);
                if (panel.CaptureBookmarkSelection(answer) is { } item)
                    many.Add(new AiUserBookmark { Label = "性能标签", Anchor = item.Anchor, FoldKeys = [.. item.FoldKeys] });
            }
            Check(many.Count == 30, "Could not construct the synthetic dense-label fixture");
            book.Active.Bookmarks.AddRange(many); Invoke(panel, "RefreshBookmarkMarkers");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(markerLayers.TryGetValue(answer, out var denseLayer) && denseLayer.Positions.Count >= 30, "Dense labels did not create the expected visible anchors");
            var manyMarkerCost = await MarkerCost();
            File.WriteAllText(Path.Combine(output, "bookmark-marker-performance.json"), System.Text.Json.JsonSerializer.Serialize(new { scenario = "120 turns, 31 labels in one answer; medians of 7 warm samples", noMarkerCost, manyMarkerCost }));
            foreach (var item in many) book.Active.Bookmarks.Remove(item);
            Invoke(panel, "RefreshBookmarkMarkers");
            var saved = panel.GetBookmarks().Single(b => b.Label == "回答锚点");
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            Check(await panel.NavigateBookmarkAsync(saved), "Cannot navigate to a saved selection inside a folded heading");
            Check(!turn.CollapsedSections[target.SegmentId + ":" + target.Position], "Bookmark did not reopen the target section");
            var visibleDuplicate = new Paragraph(new Run(saved.Anchor!.Quote));
            var laterSection = (Section)laterParagraph.Parent;
            laterSection.Blocks.InsertBefore(laterParagraph, visibleDuplicate);
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            var legacyHidden = book.Active.Bookmarks.Single(b => b.Id == saved.Id) with { Id = "legacy-hidden", Label = "旧标签折叠恢复", FoldKeys = [], ContextBefore = null, ContextAfter = null };
            Check(answer.BookmarkMarkerPoint(legacyHidden) is null, "A visible duplicate must not be mistaken for an old bookmark in a closed section");
            book.Active.Bookmarks.Add(legacyHidden);
            Check(!await panel.NavigateBookmarkAsync(panel.GetBookmarks().Single(b => b.Id == legacyHidden.Id)), "An ambiguous legacy bookmark jumped to a visible duplicate");
            laterSection.Blocks.Remove(visibleDuplicate);
            toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); host.UpdateLayout();
            Check(await panel.NavigateBookmarkAsync(panel.GetBookmarks().Single(b => b.Id == legacyHidden.Id)), "A legacy bookmark without fold keys could not reveal its hidden section");
            Check(!turn.CollapsedSections[target.SegmentId + ":" + target.Position], "Legacy bookmark failed to reopen the target section");
            book.Active.Bookmarks.Remove(legacyHidden);
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
            var tableScroll = Descendants<ScrollViewer>(answer).First(scroll => scroll.Content == tableView);
            tableView.Width = Math.Max(900, tableView.ActualWidth); host.UpdateLayout();
            tableScroll.ScrollToHorizontalOffset(tableScroll.ScrollableWidth); host.UpdateLayout();
            var horizontalBefore = tableScroll.HorizontalOffset;
            Check(horizontalBefore > 0 && answer.BookmarkMarkerPoint(book.Active.Bookmarks.Single(b => b.Id == tableTarget.Id)) is not null,
                "A horizontally hidden table selection must retain its row marker");
            Check(tableScroll.HorizontalOffset == horizontalBefore, "Rendering a table marker changed the horizontal viewport");
            var formulaTarget = panel.GetBookmarks().Single(b => b.TurnIndex == 60 && b.Label == "61:eqA");
            Check(await panel.NavigateBookmarkAsync(formulaTarget), "Cannot navigate to a tagged display formula");

            var questionView = turnView.Questions[0];
            var questionParagraph = (Paragraph)questionView.Document.Blocks.FirstBlock!;
            questionView.Selection.Select(questionParagraph.ContentStart, questionParagraph.ContentEnd);
            var question = panel.CaptureBookmarkSelection(questionView);
            Check(question is not null && question.Anchor.SegmentId.StartsWith("@question:"), "User question selection was not captured separately");
            Check(panel.AddBookmarkFromSelection(question!, "问题锚点"), "Cannot save a question bookmark");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(markerLayers.TryGetValue(questionView, out var questionMarker) && questionMarker.Positions.Count == 1, "Question bookmark did not show its marker");
            var questionTarget = panel.GetBookmarks().Single(b => b.Label == "问题锚点");
            Check(await panel.NavigateBookmarkAsync(questionTarget), "Cannot navigate to a user question bookmark");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(host, Path.Combine(output, "bookmark-marker-question.png"));
            await panel.SaveAsync();
            var loaded = AiStorage.Load(project);
            Check(loaded.Active.Bookmarks.Count == 3 && loaded.Draft == "保留草稿", "Bookmark persistence changed the saved draft or lost bookmarks");
            Check(panel.RemoveBookmark(questionTarget) && panel.GetBookmarks().All(b => b.Id != questionTarget.Id), "User bookmark deletion failed");
            Check(!markerLayers.ContainsKey(questionView), "Removing the last label did not remove the marker layer");
            var lastHeading = panel.GetBookmarks().First(b => b.TurnIndex == 119 && b.Kind == AiBookmarkKind.Heading);
            Check(await panel.NavigateBookmarkAsync(lastHeading), "Cannot reveal 120th turn");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var lastView = views[book.Turns[119].Id].Questions[0];
            var lastParagraph = (Paragraph)lastView.Document.Blocks.FirstBlock!;
            lastView.Selection.Select(lastParagraph.ContentStart, lastParagraph.ContentEnd);
            var lastSelection = panel.CaptureBookmarkSelection(lastView);
            Check(lastSelection is not null && panel.AddBookmarkFromSelection(lastSelection, "三位轮次问题标签"), "Cannot add bookmark to 120th question");
            var lastTarget = panel.GetBookmarks().Single(b => b.Label == "三位轮次问题标签");
            Check(await panel.NavigateBookmarkAsync(lastTarget), "Cannot navigate to 120th question");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(host, Path.Combine(output, "bookmark-marker-question-120.png"));
            Check(panel.RemoveBookmark(lastTarget), "Cannot remove 120th question bookmark");
            var originalConversation = book.Active.Id;
            await panel.NewConversationAsync();
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(markerLayers.Count == 0, "Bookmark markers leaked into a new conversation");
            await panel.SelectConversationAsync(originalConversation);
            Check(await panel.NavigateBookmarkAsync(saved), "Could not restore a saved answer bookmark after switching conversations");
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var restoredAnswer = views[turn.Id].Answers.Single();
            Check(markerLayers.TryGetValue(restoredAnswer, out var restoredLayer) && restoredLayer.Positions.Count > 0, "Saved marker did not return after switching conversations");
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
            Check(legacyTarget.Label == "121:0.1" && await panel.NavigateBookmarkAsync(legacyTarget), "Legacy answer without segments cannot be indexed and located");
            await panel.NewConversationAsync();
            Check(((System.Collections.IDictionary)Field(panel, "_bookmarkCache")!).Count == 0, "Switching conversations retained the old default index");
            var legacy = System.Text.Json.JsonSerializer.Deserialize<AiConversation>("{\"Title\":\"旧会话\",\"Draft\":\"旧草稿\"}")!;
            Check(legacy.Bookmarks.Count == 0 && legacy.BookmarkBallX is null && legacy.BookmarkSort == "document" && legacy.Draft == "旧草稿", "Old session migration lost data");
            Console.WriteLine($"AI bookmarks: {checks} checks passed (ordered duplicate headings, formulas, code exclusion, offscreen/folded navigation, captured answer and question selections, persistence and old sessions).");
        }
        finally { host.Close(); }
    }
}
