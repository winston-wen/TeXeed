using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiReferenceNavigationAsync(MainWindow owner, string output)
    {
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch
        {
            Paragraph p => [p], Section s => Paragraphs(s.Blocks),
            System.Windows.Documents.List l => l.ListItems.SelectMany(i => Paragraphs(i.Blocks)), _ => []
        });
        static IEnumerable<Hyperlink> Links(InlineCollection inlines) => inlines.SelectMany(i =>
            i is Hyperlink h ? [h] : i is Span s ? Links(s.Inlines) : []);
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        panel.InitializeBranchUi();
        var host = new Window { Owner = owner, Content = panel, Width = 850, Height = 560 };
        host.Show();
        try
        {
            var project = Path.Combine(output, "reference-navigation.tex");
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var parent = book.Active;
            parent.Turns.Add(new AiTurn("起始问题", "# 章节\n\n前文。\n\n这里的 **Module** 是需要追问的定义。\n\n后文。\n\n" + new string('后', 7600)));
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var answer = views[parent.Turns[0].Id].Answers.Single(); await answer.RenderTask;
            var paragraph = Paragraphs(answer.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("需要追问"));
            answer.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            string? opened = null; panel.ConversationOpenRequested += (_, id) => opened = id;
            await panel.BranchFromSelectionAsync(answer);
            var child = book.Conversations.Single(c => c.ParentConversationId == parent.Id);
            if (opened != child.Id || !Descendants<Button>(answer).Any(b => Equals(b.Content, "🗨️ " + child.Title)))
                throw new Exception("A new branch did not immediately create a link in the already-open parent answer");
            child.Title = "查 Module 定义";
            panel.RefreshVisibleBranchLinks(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants<Button>(answer).Any(b => Equals(b.Content, "🗨️ 查 Module 定义")))
                throw new Exception("The source link did not reflect the renamed child");

            var endingParagraph = Paragraphs(answer.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim() == "后文。");
            answer.Selection.Select(paragraph.ContentStart, endingParagraph.ContentEnd);
            await panel.BranchFromSelectionAsync(answer);
            var spanning = book.Conversations.Last();
            if (!spanning.BranchAnchor!.Quote.Contains('\n') || endingParagraph.NextBlock is not BlockUIContainer { Child: Button endingLink } ||
                !Equals(endingLink.Content, "🗨️ " + spanning.Title))
                throw new Exception("A multi-paragraph reference did not place its link after the final quoted paragraph");

            var firstHeading = Paragraphs(answer.Document.Blocks).First(p => p.Inlines.OfType<Hyperlink>().Any());
            firstHeading.Inlines.OfType<Hyperlink>().First().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            answer = views[parent.Turns[0].Id].Answers.Single(); await answer.RenderTask;
            var collapsedHeading = Paragraphs(answer.Document.Blocks).First(p => p.Inlines.OfType<Hyperlink>().Any());
            collapsedHeading.Inlines.OfType<Hyperlink>().First().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants<Button>(answer).Any(b => Equals(b.Content, "🗨️ 查 Module 定义")))
                throw new Exception("Expanding a saved folded section did not restore the source link");
            Capture(host, Path.Combine(output, "branch-parent-reference.png"));

            child.Turns.Add(new AiTurn("请说明", "回答") { Prompt = "请说明", SentAnnotations = [new AiEntry { AnswerAnchor = child.BranchAnchor }] });
            await panel.SelectConversationAsync(child.Id); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var turnView = views[child.Turns[0].Id];
            foreach (var q in turnView.Questions) await q.RenderTask;
            var link = turnView.Questions.SelectMany(q => Paragraphs(q.Document.Blocks)).SelectMany(p => Links(p.Inlines)).Single();
            string? sourceId = null; AiAnswerAnchor? requested = null;
            panel.AnswerSourceRequested += (id, anchor, _, _) => { sourceId = id; requested = anchor; };
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            if (sourceId != parent.Id || requested != child.BranchAnchor)
                throw new Exception("Clicking a sent answer reference did not request its origin");
            await panel.SelectConversationAsync(parent.Id); host.UpdateLayout();
            if (!await panel.NavigateBranchSourceAsync(requested!, child.BranchContextBefore, child.BranchContextAfter))
                throw new Exception("The referenced origin could not be located after reopening the parent");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project);
            if (restored.Conversations.Single(c => c.Id == child.Id).BranchAnchor != child.BranchAnchor)
                throw new Exception("Existing branch anchor was not preserved on reload");
            await CallAsync(owner, "InitializeAiProjectAsync", project);
            ((FrameworkElement)owner.FindName("Welcome")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)owner.FindName("Workspace")).Visibility = Visibility.Visible;
            await CallAsync(owner, "OpenAiConversationAsync", child.Id);
            owner.UpdateLayout(); await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var workspacePanel = (AiPanel)Field(owner, "_aiPanel")!;
            var workspaceViews = (Dictionary<string, AiTurnView>)Field(workspacePanel, "_turnViews")!;
            var workspaceTurn = workspaceViews[child.Turns[0].Id];
            foreach (var q in workspaceTurn.Questions) await q.RenderTask;
            var workspaceLink = workspaceTurn.Questions.SelectMany(q => Paragraphs(q.Document.Blocks)).SelectMany(p => Links(p.Inlines)).Single();
            workspaceLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            for (var i = 0; i < 100 && ((AiPanel)Field(owner, "_aiPanel")!).ConversationId != parent.Id; i++)
            { await Task.Delay(20); owner.UpdateLayout(); }
            if (((AiPanel)Field(owner, "_aiPanel")!).ConversationId != parent.Id)
                throw new Exception("Clicking the reference did not switch the actual workspace to the source conversation");
            var actualSource = (AiPanel)Field(owner, "_aiPanel")!;
            var visibleAtSource = false;
            for (var i = 0; i < 100 && !visibleAtSource; i++)
            {
                owner.UpdateLayout();
                var sourceViews = (Dictionary<string, AiTurnView>)Field(actualSource, "_turnViews")!;
                if (sourceViews.TryGetValue(parent.Turns[0].Id, out var sourceTurn) && sourceTurn.Answers.FirstOrDefault() is { } sourceAnswer && sourceAnswer.RenderTask.IsCompletedSuccessfully)
                {
                    var location = sourceAnswer.ResolveUserBookmark(new AiUserBookmark { Anchor = child.BranchAnchor, ContextBefore = child.BranchContextBefore, ContextAfter = child.BranchContextAfter });
                    if (location is not null && sourceAnswer.BookmarkPoint(location) is { } point)
                    {
                        var viewport = (ScrollViewer)actualSource.FindName("MessagesScroll");
                        var y = sourceAnswer.TranslatePoint(point, viewport).Y;
                        visibleAtSource = viewport.VerticalOffset > 0 && y >= 0 && y <= 50;
                    }
                }
                if (!visibleAtSource) await Task.Delay(20);
            }
            if (!visibleAtSource) throw new Exception("The workspace did not scroll the reference origin into the expected viewport position");
            var sourceTurnView = ((Dictionary<string, AiTurnView>)Field(actualSource, "_turnViews")!)[parent.Turns[0].Id];
            var sourceAnswerView = sourceTurnView.Answers.Single();
            if (!sourceAnswerView.BranchLinkBlocks.ContainsKey(child.Id) || !sourceAnswerView.BranchLinkBlocks.ContainsKey(spanning.Id))
                throw new Exception("The two source links were not restored together");
            var sourceScroll = (ScrollViewer)actualSource.FindName("MessagesScroll");
            sourceScroll.ScrollToVerticalOffset(260); owner.UpdateLayout();
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var beforeTop = sourceTurnView.TranslatePoint(new Point(), sourceScroll).Y;
            Invoke(owner, "CloseTabs", TabWorkspace.AiConversationKey(child.Id), TabCloseScope.This);
            for (var i = 0; i < 200 && ((HashSet<string>)Field(owner, "_closingAi")!).Contains(child.Id); i++) await Task.Delay(10);
            await CallAsync(owner, "DeleteAiConversationsAsync", (object)new[] { child.Id });
            for (var i = 0; i < 100 && sourceAnswerView.BranchLinkBlocks.ContainsKey(child.Id); i++)
            { await Task.Delay(20); owner.UpdateLayout(); }
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); owner.UpdateLayout();
            if (sourceAnswerView.BranchLinkBlocks.ContainsKey(child.Id) || !sourceAnswerView.BranchLinkBlocks.ContainsKey(spanning.Id))
                throw new Exception("Deleting one child did not remove only its source link");
            var afterTop = sourceTurnView.TranslatePoint(new Point(), sourceScroll).Y;
            if (Math.Abs(afterTop - beforeTop) > 1)
                throw new Exception($"Removing the source link displaced the reading viewport: {beforeTop} to {afterTop}");
            Console.WriteLine("AI reference navigation: current parent, folded section, rename, click and reload passed.");
        }
        finally { await panel.StopAndSaveAsync(); host.Content = null; panel.Dispose(); host.Close(); }
    }
}
