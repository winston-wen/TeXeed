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
    private static async Task CheckRevisionSourcesAsync(MainWindow owner, string output)
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        var project = Path.Combine(output, "revision-source.tex");
        var store = new AiWorkspaceStore(project, new AiNotebook());
        var root = store.Book.Active;
        const string source = "# 概念\n\n保留甲乙。\n\n## 旧出处\n\n用来追问的文字。\n\n# 另一节\n\n结尾。";
        root.Turns.Add(new("问题", source));
        using var panel = new AiPanel();
        panel.Bind(store, root.Id);
        var host = new Window { Owner = owner, Content = panel, Width = 880, Height = 780, Left = -2800, Top = -2600, ShowActivated = false };
        host.Show();
        async Task Layout()
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        MarkdownView Answer() => ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!)[root.Turns[0].Id].Answers.Single();
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(b => b is Paragraph p ? new[] { p }.AsEnumerable() :
            b is Section section ? Paragraphs(section.Blocks) : []);
        try
        {
            await Layout();
            var view = Answer();
            var paragraph = Paragraphs(view.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("用来追问"));
            view.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            panel.ConversationOpenRequested += (_, _) => { };
            await panel.BranchFromSelectionAsync(view);
            var child = store.Book.Conversations.Single(c => c.ParentConversationId == root.Id);
            var anchor = child.BranchAnchor!;
            var origin = AiSessionExcerptReferences.Create(root.Id, anchor, null, null);
            root.Turns.Add(new AiTurn("相同选区在本会话追问", "答复") { SentAnnotations = [origin] });
            root.Bookmarks.Add(new() { Anchor = anchor, Label = "我的书签" });
            Invoke(panel, "RefreshTranscript"); await Layout();
            var draft = panel.BeginRevision(root.Turns[0]);
            var from = draft.Rows.Single(r => r.Text == "用来追问的文字。");
            var foldPositions = MarkdownView.BookmarkFragments(source).Where(f => f.Kind == AiBookmarkKind.Heading).Select(f => f.Position).ToArray();
            root.Turns[0].CollapsedSections[from.SegmentId + ":" + foldPositions[0]] = true;
            root.Turns[0].CollapsedSections[from.SegmentId + ":" + foldPositions[2]] = true;
            Require(from.Links.Count == 3 && from.Links.Any(l => l.Id == child.Id), "Branch, local excerpt and bookmark were not captured");
            var next = AiRevisionText.Clone(draft.Rows);
            var recipient = next.FindIndex(r => r.Text == "保留甲乙。");
            next[recipient] = next[recipient] with { Text = "这里接续下文。", Links = from.Links.Select(l => l with { Offset = 2 }).ToList() };
            next.RemoveAll(r => r.Id == from.Id || r.Text == "旧出处");
            next.Insert(recipient, new("added-heading", from.SegmentId, "新标题", 2, false, []));
            await panel.CommitRevisionAsync(draft.TurnId, draft.Version, next, "user");
            await Layout(); view = Answer();
            Require(root.Turns[0].Answer.Contains("## 新标题") && !root.Turns[0].Answer.Contains("旧出处"), "Latest Markdown was not applied");
            Require(child.InheritedTurns[0].Answer == source && anchor.Quote == "用来追问的文字。", "Original branch evidence changed");
            Require(await panel.NavigateBranchSourceAsync(anchor, sourceId: child.Id), "Failed to return from branch to revised source");
            await Layout();
            Require(root.Turns[0].CollapsedSections.Values.Count(folded => folded) == 1, "Navigation must open only the new owner and preserve unrelated folds");
            Require(view.BranchLinkInlines.Count == 2, "Both cross-session and local links must remain visible");
            var bubble = view.BranchLinkInlines[child.Id];
            Require(view.CopySource(bubble.ElementStart.Paragraph!.ContentStart, bubble.ElementStart).Trim() == "这里" &&
                view.CopySource(bubble.ElementEnd, bubble.ElementEnd.Paragraph!.ContentEnd).Trim() == "接续下文。", "Bubble is not between the intended words");
            Require(await panel.NavigateBookmarkAsync(panel.GetBookmarks().Single(b => b.Kind == AiBookmarkKind.User)), "Bookmark failed after revision");
            var oldVersion = false;
            try { await panel.CommitRevisionAsync(draft.TurnId, draft.Version, next, "ai"); }
            catch (InvalidOperationException) { oldVersion = true; }
            Require(oldVersion, "A stale edit overwrote a saved revision");
            var now = panel.BeginRevision(root.Turns[0]);
            var further = AiRevisionText.Clone(now.Rows);
            further[0] = further[0] with { Text = "概念（已整理）" };
            await panel.CommitRevisionAsync(now.TurnId, now.Version, further, "ai", "fixture-command");
            await panel.CommitRevisionAsync(now.TurnId, now.Version, further, "ai", "fixture-command");
            var saved = AiStorage.Load(project).Conversations.Single(c => c.Id == root.Id).Turns[0];
            Require(saved.RevisionHistory is { } history && history.Select(r => r.Author).SequenceEqual(["ai", "user", "ai"]) && history[0].Sources.Values.Single() == source,
                "Reopened history lost the original, authors or duplicate-command protection");
            await Layout(); view = Answer();
            var revisedParagraph = Paragraphs(view.Document.Blocks).Single(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("接续下文"));
            view.Selection.Select(revisedParagraph.ContentStart, revisedParagraph.ContentEnd);
            var unfinished = panel.BeginRevision(root.Turns[0]);
            unfinished.Rows[0] = unfinished.Rows[0] with { Text = "只在上游保留的未完成改稿" };
            root.Turns[0].UserRevisionDraft = unfinished;
            await panel.BranchFromSelectionAsync(view);
            Require(store.Book.Conversations.Last().InheritedTurns[0] is { UserRevisionDraft: null } inherited && inherited.Answer == saved.Answer &&
                root.Turns[0].UserRevisionDraft == unfinished, "A new branch inherited the wrong version or a private unfinished draft");
            Console.WriteLine("PASS: original and latest ancestors, inline dual references, bookmark, optimistic concurrency, authors and idempotent reopen");

            var first = new AiSegment { Kind = "text", Text = "# 被删的标题\n\n## 子标题\n\n仍然保留。" };
            var second = new AiSegment { Kind = "final", Text = "第二片段的出处。" };
            var mixed = new AiTurn("多个回答片段", second.Text) { Segments = [first, second] };
            root.Turns.Add(mixed);
            AiConversation Branch(AiSegment segment, string quote) => new()
            {
                ParentConversationId = root.Id, Title = quote,
                BranchAnchor = new AiAnswerAnchor(mixed.Id, segment.Id, 0, quote.Length, quote, AiStorage.Hash(segment.Text)) { TextOffset = 0 }
            };
            var titleBranch = Branch(first, "被删的标题");
            var secondBranch = Branch(second, second.Text);
            store.Book.Conversations.AddRange([titleBranch, secondBranch]);
            Invoke(panel, "RefreshTranscript"); await Layout();
            var mixedDraft = panel.BeginRevision(mixed);
            var changedRows = AiRevisionText.Clone(mixedDraft.Rows);
            changedRows[0] = changedRows[0] with { Text = "", Level = 0, Links = [changedRows[0].Links.Single() with { Offset = 0 }] };
            var secondLink = changedRows[^1].Links.Single();
            changedRows[^2].Links.Add(secondLink with { Offset = changedRows[^2].Text.Length });
            changedRows.RemoveAt(changedRows.Count - 1);
            await panel.CommitRevisionAsync(mixed.Id, mixedDraft.Version, changedRows, "user"); await Layout();
            Require(await panel.NavigateBranchSourceAsync(titleBranch.BranchAnchor!, sourceId: titleBranch.Id), "Reference-only first paragraph disappeared");
            Require(await panel.NavigateBranchSourceAsync(secondBranch.BranchAnchor!, sourceId: secondBranch.Id), "Reference did not follow its owner into another answer segment");
            var mixedView = ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!)[mixed.Id];
            var firstView = mixedView.Answers.Single(a => ((AiSegment)a.DataContext).Id == first.Id);
            Require(firstView.BranchLinkInlines[titleBranch.Id].ElementStart.Paragraph != firstView.BranchLinkInlines[secondBranch.Id].ElementStart.Paragraph &&
                firstView.Markdown.Contains("## 子标题") && root.Turns.Last().Answer.Contains("仍然保留。") && mixedView.Answers.Sum(v => v.BranchLinkInlines.Count) == 2,
                "Deleting a heading did not preserve its children and one owner per reference");
            Console.WriteLine("PASS: first reference-only container, deleted heading with retained children, cross-segment migration");
        }
        finally { host.Close(); }
        var material = "# 标题\n\n正文 $a+b$ 在后。\n\n\\[\na=b\n\\]\n\n|甲|乙|\n|---|---|\n|1|2|\n\n正文 {{text:input}}";
        var rows = AiRevisionText.Parse("fixture", material);
        Require(rows.Count(r => r.Locked) == 2 && AiRevisionText.Source(rows) == material, "Mixed formula/table Markdown failed to round-trip");
        var adjacent = AiRevisionText.Parse("adjacent", "# 甲\n## 乙").Select(row => row with { Level = 0 }).ToArray();
        Require(AiRevisionText.Parse("adjacent", AiRevisionText.Source(adjacent)).Count == 2, "Demoting adjacent headings merged two paragraph owners on reopen");
        const string definitions = "\r\n  前文  \r\n\r\n[r]: https://example.com\r\n\r\n后段 [r]。\r\n\r\n[s]: https://example.org\r\n  \r\n";
        var definitionRows = AiRevisionText.Parse("links", definitions);
        Require(AiRevisionText.Source(definitionRows) == definitions && definitionRows.Count(r => r.Locked) == 2, "Markdown link definitions or whitespace lost on import");
        definitionRows[0] = definitionRows[0] with { Text = "修改前文。" };
        Require(AiRevisionText.Source(definitionRows) == definitions.Replace("  前文  ", "修改前文。"), "Editing prose truncated another paragraph's reference destination");
        const string formatted = "其中**关键**取决于 \\(a+b\\)。";
        var visible = AiRevisionText.SelectionText(formatted);
        Require(visible.Text == "其中关键取决于 $a+b$。" && visible.Ends[visible.Text.IndexOf("键", StringComparison.Ordinal)] == formatted.IndexOf("键", StringComparison.Ordinal) + 1,
            "Displayed rich excerpt could not be mapped back into the source");
        Require(MarkdownView.MapRevisionOffset("看[甲](甲)甲乙", "看甲甲乙", 7) == 2, "A Markdown destination displaced the visible reference by one character");
        var edited = AiRevisionText.Clone(rows);
        edited[1] = edited[1] with { Text = "改了 $a+c$ 在后。" };
        var blocked = false;
        try { AiRevisionText.Normalize(new("turn", "version", rows), edited); }
        catch (InvalidOperationException) { blocked = true; }
        Require(blocked, "Protected inline formula was editable");
        Console.WriteLine("PASS: headings, text, display/inline math, table and exercise round-trip");
        await CheckCompositeRevisionSourcesAsync(owner, output);
    }
}
