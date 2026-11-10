using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal event EventHandler<string>? ConversationOpenRequested;
    internal event EventHandler<AiConversation>? BranchSourceRequested;

    internal void InitializeBranchUi()
    {
        foreach (UIElement element in ChatRoot.Children) Grid.SetRow(element, Grid.GetRow(element) + 1);
        ChatRoot.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        var bar = new DockPanel { Margin = new Thickness(20, 3, 14, 3) };
        var map = new Button { Content = "会话关系", Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Right };
        var parent = new Button { Content = "← 返回提问出处", Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
        parent.Click += (_, _) => { if (_book.Active.ParentConversationId is not null) BranchSourceRequested?.Invoke(this, _book.Active); };
        parent.Loaded += (_, _) => parent.Visibility = _book.Active.ParentConversationId is null ? Visibility.Collapsed : Visibility.Visible;
        map.Click += (_, _) => ShowConversationRelations();
        DockPanel.SetDock(map, Dock.Right); bar.Children.Add(map); bar.Children.Add(parent);
        ChatRoot.Children.Add(bar); Grid.SetRow(bar, 0);
    }

    private void AddBranchMenu(MarkdownView view, ContextMenu menu)
    {
        var branch = new MenuItem { Header = "从所选内容追问" };
        menu.Opened += (_, _) => branch.IsEnabled = _operation is null && AnswerTurn(view) is not null && view.HasBookmarkSelection;
        branch.Click += async (_, _) =>
        {
            try { await BranchFromSelectionAsync(view); }
            catch (Exception ex) { ShowError(ex); }
        };
        menu.Items.Add(branch);
    }

    private async void AddBranchLinks(MarkdownView view)
    {
        if (AnswerTurn(view) is not { } turn || view.DataContext is not AiSegment segment) return;
        var conversationId = _book.Active.Id;
        await view.RenderTask;
        if (_disposed || _book.Active.Id != conversationId || !view.IsLoaded) return;
        foreach (var branch in _book.Conversations.Where(c => c.ParentConversationId == conversationId && c.BranchAnchor?.TurnId == turn.Id && c.BranchAnchor.SegmentId == segment.Id).ToArray())
        {
            var anchor = branch.BranchAnchor!;
            var pointer = view.ResolveUserBookmark(new AiUserBookmark { Anchor = anchor, ContextBefore = branch.BranchContextBefore, ContextAfter = branch.BranchContextAfter });
            var paragraph = pointer?.Paragraph;
            var blocks = paragraph?.Parent switch { Section section => section.Blocks, FlowDocument document => document.Blocks, _ => null };
            if (paragraph is null || blocks is null || blocks.OfType<BlockUIContainer>().Any(b => Equals(b.Tag, branch.Id))) continue;
            var link = new Button { Content = "🗨️ " + branch.Title, FontSize = view.FontSize, FontFamily = view.FontFamily,
                HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
            link.SetResourceReference(Control.ForegroundProperty, "ChatFocus");
            link.Click += (_, _) => OpenRelatedConversation(branch.Id);
            blocks.InsertAfter(paragraph, new BlockUIContainer(link) { Tag = branch.Id, Margin = new Thickness(0, 2, 0, 2) });
        }
    }

    internal async Task BranchFromSelectionAsync(MarkdownView view)
    {
        if (_operation is not null || AnswerTurn(view) is not { } turn || view.DataContext is not AiSegment segment) return;
        var selection = view.GetAnnotationSelection(); var quote = selection.Quote.Trim();
        if (quote.Length == 0 || quote.Length > 40000) return;
        var parent = _book.Active;
        var turnIndex = parent.Turns.IndexOf(turn); if (turnIndex < 0) return;
        var anchor = new AiAnswerAnchor(turn.Id, segment.Id, selection.Offset, selection.Length, quote, view.Revision) { TextOffset = selection.TextOffset };
        var context = view.BookmarkSelectionContext();
        await SaveAsync();
        var snapshot = parent.Snapshot();
        var branch = new AiConversation { ParentConversationId = parent.Id, BranchAnchor = anchor, BranchContextBefore = context.Before, BranchContextAfter = context.After,
            Title = string.Concat(quote.EnumerateRunes().Take(16)),
            InheritedTurns = snapshot.InheritedTurns.Concat(snapshot.Turns.Take(turnIndex + 1)).ToList(),
            Entries = [new AiEntry { Title = "追问出处", AnswerAnchor = anchor }] };
        _book.Conversations.Add(branch);
        if (_store is not null) await _store.SaveAsync(); else await SaveAsync();
        OpenRelatedConversation(branch.Id);
    }

    internal async Task<bool> NavigateBranchSourceAsync(AiAnswerAnchor anchor, string? before = null, string? after = null)
    {
        var conversation = _book.Active;
        var turn = conversation.Turns.FirstOrDefault(t => t.Id == anchor.TurnId);
        var index = turn is null ? -1 : _transcriptItems.IndexOf(turn);
        if (turn is null || index < 0) return false;
        _readingRestoreVersion++; _restoringReading = false;
        VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_disposed || _book.Active != conversation || !_turnViews.TryGetValue(turn.Id, out var turnView)) return false;
        if (turn.Collapsed) turnView.Toggle();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var view = turnView.Answers.FirstOrDefault(answer => answer.DataContext is AiSegment s && s.Id == anchor.SegmentId);
        if (view is null) return false;
        await view.RenderTask;
        if (_disposed || _book.Active != conversation || view.Revision != anchor.Revision) return false;
        view.RevealAllBookmarkFolds();
        var pointer = view.ResolveUserBookmark(new AiUserBookmark { Anchor = anchor, ContextBefore = before, ContextAfter = after });
        History.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (pointer is null || view.BookmarkPoint(pointer) is not { } point) return false;
        var position = view.TranslatePoint(point, MessagesScroll);
        MessagesScroll.ScrollToVerticalOffset(Math.Max(0, MessagesScroll.VerticalOffset + position.Y - 24));
        return true;
    }

    private void OpenRelatedConversation(string id)
    {
        if (ConversationOpenRequested is not null) ConversationOpenRequested(this, id);
        else _ = SelectConversationAsync(id);
    }

    private void ShowConversationRelations()
    {
        var window = new Window { Owner = Window.GetWindow(this), Title = "会话关系", Width = 480, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var tree = new TreeView { Margin = new Thickness(12), FontSize = 15 };
        var all = _book.Conversations.ToArray();
        TreeViewItem MakeNode(AiConversation conversation, HashSet<string> ancestors)
        {
            var node = new TreeViewItem { Header = conversation.Title, Tag = conversation.Id, IsExpanded = true, Padding = new Thickness(2, 5, 2, 5) };
            if (!ancestors.Add(conversation.Id)) return node;
            foreach (var child in all.Where(c => c.ParentConversationId == conversation.Id && !ancestors.Contains(c.Id)))
                node.Items.Add(MakeNode(child, new HashSet<string>(ancestors)));
            return node;
        }
        foreach (var root in all.Where(c => c.ParentConversationId is null || all.All(p => p.Id != c.ParentConversationId))) tree.Items.Add(MakeNode(root, []));
        tree.MouseDoubleClick += (_, e) =>
        {
            for (var current = e.OriginalSource as DependencyObject; current is not null && current != tree; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is TreeViewItem { Tag: string id }) { e.Handled = true; window.Close(); OpenRelatedConversation(id); break; }
        };
        window.Content = tree; window.Show();
    }
}
