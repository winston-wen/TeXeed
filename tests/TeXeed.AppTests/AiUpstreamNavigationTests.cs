using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiUpstreamNavigationAsync(MainWindow owner, string output)
    {
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        { Paragraph p => [p], Section s => Paragraphs(s.Blocks), _ => [] });
        static Paragraph ParagraphWith(MarkdownView view, string text) => Paragraphs(view.Document.Blocks)
            .First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains(text, StringComparison.Ordinal));
        static void Fold(MarkdownView view, string heading)
        {
            var paragraph = ParagraphWith(view, heading);
            var link = paragraph.Inlines.OfType<Hyperlink>().First();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        }
        static void SelectPhrase(MarkdownView view, string paragraphText, string phrase)
        {
            var paragraph = ParagraphWith(view, paragraphText);
            var run = paragraph.Inlines.OfType<Run>().First(r => r.Text.Contains(phrase, StringComparison.Ordinal));
            var start = run.ContentStart.GetPositionAtOffset(run.Text.IndexOf(phrase, StringComparison.Ordinal))!;
            view.Selection.Select(start, start.GetPositionAtOffset(phrase.Length)!);
        }
        static string Key(AiTurn turn, string source, string heading) =>
            turn.DisplaySegments[0].Id + ":" + MarkdownView.BookmarkFragments(source).Single(f => f.Kind == AiBookmarkKind.Heading && f.Description == heading).Position;

        var project = Path.Combine(output, "upstream-focused-folds.tex");
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        panel.InitializeBranchUi();
        panel.ConversationOpenRequested += (_, _) => { };
        var host = new Window { Owner = owner, Content = panel, Width = 850, Height = 560 };
        host.Show();
        try
        {
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var parent = book.Active;
            ((TextBox)panel.FindName("Composer")).Text = "原会话草稿不变";
            var source = "# 前兄弟\n\n第一处上下文是重复概念，另一段结尾。\n\n" + new string('甲', 900) +
                "\n\n# 目标父层\n\n" + new string('前', 2200) + "\n\n## 目标中层\n\n中层说明。\n\n### 目标深层\n\n目标上下文特有的重复概念，后面还有唯一说明。\n\n" +
                new string('后', 3000) + "\n\n## 目标兄弟\n\n不应该被展开。\n\n# 后兄弟\n\n同样保持收起。";
            var turn = new AiTurn("问题", source); parent.Turns.Add(turn);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[turn.Id].Answers.Single(); await answer.RenderTask;
            Fold(answer, "前兄弟"); host.UpdateLayout();
            SelectPhrase(answer, "目标上下文特有", "重复概念");
            await panel.BranchFromSelectionAsync(answer);
            var child = book.Conversations.Single(c => c.ParentConversationId == parent.Id);
            var oldAnchor = child.BranchAnchor ?? throw new Exception("Missing branch origin");
            Fold(answer, "目标兄弟"); Fold(answer, "后兄弟"); Fold(answer, "目标深层"); Fold(answer, "目标中层"); Fold(answer, "目标父层");
            var unrelatedKeys = new[] { Key(turn, source, "前兄弟"), Key(turn, source, "目标兄弟"), Key(turn, source, "后兄弟") };
            var targetKeys = new[] { Key(turn, source, "目标父层"), Key(turn, source, "目标中层"), Key(turn, source, "目标深层") };
            if (unrelatedKeys.Concat(targetKeys).Any(key => !turn.CollapsedSections.GetValueOrDefault(key))) throw new Exception("Fixture did not collapse all expected sections");
            await panel.SelectConversationAsync(child.Id);
            ((TextBox)panel.FindName("Composer")).Text = "追问草稿不变";
            await panel.SelectConversationAsync(parent.Id); host.UpdateLayout();
            if (!await panel.NavigateBranchSourceAsync(oldAnchor, child.BranchContextBefore, child.BranchContextAfter)) throw new Exception("Could not locate the legacy branch anchor");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (unrelatedKeys.Any(key => !turn.CollapsedSections.GetValueOrDefault(key)) || targetKeys.Any(key => turn.CollapsedSections.GetValueOrDefault(key)))
                throw new Exception("Origin navigation changed unrelated folds or failed to reveal ancestors");
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            answer = views[turn.Id].Answers.Single();
            var pointer = answer.ResolveBranchAnchor(oldAnchor, child.BranchContextBefore, child.BranchContextAfter) ?? throw new Exception("Cannot resolve navigated origin");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            var point = answer.BookmarkPoint(pointer) ?? throw new Exception("Missing source layout point");
            var y = answer.TranslatePoint(point, scroll).Y;
            if (scroll.VerticalOffset <= 1 || scroll.VerticalOffset >= scroll.ScrollableHeight - 1 || Math.Abs(y - scroll.ViewportHeight / 2) > 4)
                throw new Exception($"Origin not centered in a scrollable middle section: y={y}, viewport={scroll.ViewportHeight}, offset={scroll.VerticalOffset}, max={scroll.ScrollableHeight}");

            ((TextBlock)panel.FindName("StatusText")).Text = "";
            var pendingNavigation = panel.NavigateBranchSourceAsync(oldAnchor, child.BranchContextBefore, child.BranchContextAfter);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            const double userOffset = 120;
            scroll.ScrollToVerticalOffset(userOffset); host.UpdateLayout();
            if (!await pendingNavigation) throw new Exception("User-interrupted source navigation incorrectly reported a missing anchor");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (Math.Abs(scroll.VerticalOffset - userOffset) > 1 || ((TextBlock)panel.FindName("StatusText")).Text.Length != 0)
                throw new Exception("Source navigation overwrote the user's scroll or reported a false error");
            pendingNavigation = panel.NavigateBranchSourceAsync(oldAnchor, child.BranchContextBefore, child.BranchContextAfter);
            scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(scroll), Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            const double keyOffset = 240;
            scroll.ScrollToVerticalOffset(keyOffset); host.UpdateLayout();
            if (!await pendingNavigation) throw new Exception("Space-interrupted navigation incorrectly reported a missing anchor");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (Math.Abs(scroll.VerticalOffset - keyOffset) > 1 || ((TextBlock)panel.FindName("StatusText")).Text.Length != 0)
                throw new Exception("Source navigation overwrote space-key navigation or reported a false error");

            Fold(answer, "目标深层"); Fold(answer, "目标中层"); Fold(answer, "目标父层");
            var savedFolds = new Dictionary<string, bool>(turn.CollapsedSections);
            if (await panel.NavigateBranchSourceAsync(oldAnchor)) throw new Exception("Ambiguous legacy anchor without context navigated to an arbitrary duplicate");
            if (savedFolds.Any(pair => turn.CollapsedSections.GetValueOrDefault(pair.Key) != pair.Value)) throw new Exception("Ambiguous anchor changed fold states");
            if (await panel.NavigateBranchSourceAsync(oldAnchor with { Revision = "changed" }, child.BranchContextBefore, child.BranchContextAfter))
                throw new Exception("Changed document unexpectedly accepted old origin");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project);
            if (restored.Conversations.Single(c => c.Id == child.Id).BranchAnchor != oldAnchor || restored.Conversations.Single(c => c.Id == child.Id).Draft != "追问草稿不变")
                throw new Exception("Origin navigation changed the saved branch or draft");

            var edges = new AiConversation { Title = "居中边界" }; book.Conversations.Add(edges);
            await panel.SelectConversationAsync(edges.Id);
            var edgeTurn = new AiTurn("边界问题", "# 首部\n\n首个定位点。\n\n" + new string('中', 8000) + "\n\n末尾定位点。"); edges.Turns.Add(edgeTurn);
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!; var edgeView = views[edgeTurn.Id].Answers.Single(); await edgeView.RenderTask;
            SelectPhrase(edgeView, "首个定位点", "首个定位点"); await panel.BranchFromSelectionAsync(edgeView); var first = book.Conversations.Last();
            SelectPhrase(edgeView, "末尾定位点", "末尾定位点"); await panel.BranchFromSelectionAsync(edgeView); var last = book.Conversations.Last();
            if (!await panel.NavigateBranchSourceAsync(first.BranchAnchor!, first.BranchContextBefore, first.BranchContextAfter)) throw new Exception("Top source failed");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            if (scroll.VerticalOffset > 1) throw new Exception($"Top source was not clamped at the start: {scroll.VerticalOffset}");
            if (!await panel.NavigateBranchSourceAsync(last.BranchAnchor!, last.BranchContextBefore, last.BranchContextAfter)) throw new Exception("Bottom source failed");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1) throw new Exception($"Bottom source was not clamped at the end: {scroll.VerticalOffset}/{scroll.ScrollableHeight}");
            var earlier = panel.NavigateBranchSourceAsync(first.BranchAnchor!, first.BranchContextBefore, first.BranchContextAfter);
            var later = panel.NavigateBranchSourceAsync(last.BranchAnchor!, last.BranchContextBefore, last.BranchContextAfter);
            if (!(await earlier) || !(await later)) throw new Exception("Superseded source navigation reported an incorrect failure");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1) throw new Exception("Earlier navigation overrode the later bottom target");
            earlier = panel.NavigateBranchSourceAsync(last.BranchAnchor!, last.BranchContextBefore, last.BranchContextAfter);
            later = panel.NavigateBranchSourceAsync(first.BranchAnchor!, first.BranchContextBefore, first.BranchContextAfter);
            if (!(await earlier) || !(await later)) throw new Exception("Reverse superseded navigation reported an incorrect failure");
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (scroll.VerticalOffset > 1) throw new Exception("Earlier navigation overrode the later top target");
            Console.WriteLine("PASS: upstream navigation preserves unrelated folds, resolves legacy context, centers origin and clamps both ends.");
        }
        finally { await panel.StopAndSaveAsync(); host.Content = null; host.Close(); }
    }
}
