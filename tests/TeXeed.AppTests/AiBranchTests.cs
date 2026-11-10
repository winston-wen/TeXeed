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
    private static async Task CheckAiBranchesAsync(MainWindow owner, string output)
    {
        static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks) => blocks.SelectMany(block => block switch { Paragraph p => [p], Section s => Paragraphs(s.Blocks), _ => [] });
        var project = Path.Combine(output, "branches.tex");
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var host = new Window { Owner = owner, Content = panel, Width = 850, Height = 560 };
        host.Show();
        try
        {
            await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            var root = book.Active;
            ((TextBox)panel.FindName("Composer")).Text = "保留主会话草稿";
            root.Turns.Add(new AiTurn("问题", "# 标题\n\n这里是需要追问的内容。\n\n甲重复乙，丙重复丁。\n\n继续阅读。"));
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var view = views[root.Turns[0].Id].Answers.Single(); await view.RenderTask;
            var paragraph = Paragraphs(view.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("需要追问"));
            view.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            string? opened = null; panel.ConversationOpenRequested += (_, id) => opened = id;
            await panel.BranchFromSelectionAsync(view);
            var child = book.Conversations.Single(c => c.ParentConversationId == root.Id);
            if (opened != child.Id || child.InheritedTurns.Count != 1 || child.BranchAnchor is null || root.Draft != "保留主会话草稿") throw new Exception("Branch failed to preserve origin, context or draft");
            await panel.SelectConversationAsync(child.Id);
            ((TextBox)panel.FindName("Composer")).Text = "保留追问草稿";
            child.Turns.Add(new AiTurn("继续问", "# 回答\n\n子会话中的追问内容。"));
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var childView = views[child.Turns[0].Id].Answers.Single(); await childView.RenderTask;
            var childParagraph = Paragraphs(childView.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("子会话"));
            childView.Selection.Select(childParagraph.ContentStart, childParagraph.ContentEnd);
            await panel.BranchFromSelectionAsync(childView);
            var grandchild = book.Conversations.Single(c => c.ParentConversationId == child.Id);
            if (grandchild.InheritedTurns.Count != 2 || child.Draft != "保留追问草稿") throw new Exception("Nested branch lost inherited context or draft");
            await panel.SelectConversationAsync(root.Id); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!await panel.NavigateBranchSourceAsync(child.BranchAnchor!)) throw new Exception("Cannot return to branch origin");
            views = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            var rootView = views[root.Turns[0].Id].Answers.Single();
            await rootView.RenderTask; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!Descendants<Button>(rootView).Any(button => button.Content?.ToString()?.StartsWith("🗨️") == true)) throw new Exception("Branch link was not shown in source answer");
            Capture(host, Path.Combine(output, "branch-source-link.png"));
            Invoke(panel, "ShowConversationRelations"); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var graph = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "会话关系");
            graph.UpdateLayout(); Capture(graph, Path.Combine(output, "branch-graph.png"));
            var tree = (TreeView)graph.Content;
            var rootNode = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            rootNode.UpdateLayout();
            var childNode = (TreeViewItem)rootNode.ItemContainerGenerator.ContainerFromIndex(0);
            childNode.UpdateLayout();
            var grandchildNode = (TreeViewItem)childNode.ItemContainerGenerator.ContainerFromIndex(0);
            opened = null;
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            if (!graph.IsVisible || opened is not null) throw new Exception("Double-clicking graph background opened a conversation");
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = grandchildNode });
            if (graph.IsVisible || opened != grandchild.Id) throw new Exception("Double-clicking a graph node did not open its conversation");
            for (var i = 0; i < 30; i++) book.Conversations.Add(new AiConversation { Title = "追问 " + i, ParentConversationId = root.Id });
            Invoke(panel, "ShowConversationRelations"); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            graph = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "会话关系"); graph.UpdateLayout();
            tree = (TreeView)graph.Content;
            rootNode = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0); rootNode.UpdateLayout();
            childNode = (TreeViewItem)rootNode.ItemContainerGenerator.ContainerFromIndex(0); childNode.UpdateLayout();
            grandchildNode = (TreeViewItem)childNode.ItemContainerGenerator.ContainerFromIndex(0);
            opened = null;
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = grandchildNode });
            if (graph.IsVisible || opened != grandchild.Id) throw new Exception("Virtualized graph did not open the nested conversation");
            var resumed = Paragraphs(rootView.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("继续阅读"));
            rootView.Selection.Select(resumed.ContentStart, resumed.ContentEnd);
            if (!rootView.GetAnnotationSelection().Quote.Contains("继续阅读")) throw new Exception("Branch link disrupted subsequent text selection");
            var duplicate = Paragraphs(rootView.Document.Blocks).First(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("甲重复乙"));
            var run = duplicate.Inlines.OfType<Run>().First(r => r.Text.Contains("甲重复乙"));
            var start = run.ContentStart.GetPositionAtOffset(run.Text.IndexOf("重复", StringComparison.Ordinal))!;
            rootView.Selection.Select(start, start.GetPositionAtOffset(2)!);
            await panel.BranchFromSelectionAsync(rootView);
            var repeatedBranch = book.Conversations.Last();
            if (repeatedBranch.BranchAnchor?.Quote != "重复" || !await panel.NavigateBranchSourceAsync(repeatedBranch.BranchAnchor, repeatedBranch.BranchContextBefore, repeatedBranch.BranchContextAfter)) throw new Exception("Repeated text could not be located using surrounding context");
            var heading = Paragraphs(rootView.Document.Blocks).First(p => p.Inlines.OfType<Hyperlink>().Any());
            heading.Inlines.OfType<Hyperlink>().First().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            host.UpdateLayout();
            if (!await panel.NavigateBranchSourceAsync(child.BranchAnchor!, child.BranchContextBefore, child.BranchContextAfter)) throw new Exception("Could not reveal origin in collapsed section");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project);
            if (restored.Conversations.Single(c => c.Id == grandchild.Id).InheritedTurns.Count != 2 || restored.Conversations.Single(c => c.Id == root.Id).Draft != "保留主会话草稿") throw new Exception("Reload lost branch context or draft");
            Console.WriteLine("AI branches: nested context, origin navigation and reload passed.");
        }
        finally
        {
            await panel.StopAndSaveAsync();
            host.Content = null;
            panel.Dispose();
            host.Close();
        }
    }
}
