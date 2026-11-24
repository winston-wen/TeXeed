using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private sealed class RelationNode(AiConversation conversation, bool current)
    {
        public string Id { get; } = conversation.Id;
        public string Title { get; } = conversation.Title + (current ? "（当前）" : "");
        public List<RelationNode> Children { get; } = [];
    }
    internal event EventHandler<string>? ConversationOpenRequested;
    internal event EventHandler<AiConversation>? BranchSourceRequested;
    internal event Action<string, AiAnswerAnchor, string?, string?>? AnswerSourceRequested;
    internal event EventHandler<string>? ConversationRelationsRequested;
    private Action? _refreshBranchNavigation;

    internal void InitializeBranchUi()
    {
        foreach (UIElement element in ChatRoot.Children) Grid.SetRow(element, Grid.GetRow(element) + 1);
        ChatRoot.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        History.Margin = new Thickness(0, 4, 8, 0);
        var bar = new DockPanel { Margin = new Thickness(20, 7, 14, 3), VerticalAlignment = VerticalAlignment.Center, LastChildFill = false };
        var map = new Button { Content = "会话关系", Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        var parent = new Button { Content = "↑ 上游会话", Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        var children = new Button { Content = "↓ 下游会话", Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        parent.Click += (_, _) => { if (_book.Active.ParentConversationId is not null) BranchSourceRequested?.Invoke(this, _book.Active); };
        void UpdateNavigation()
        {
            parent.IsEnabled = _book.Active.ParentConversationId is not null;
            children.IsEnabled = _book.Conversations.Any(c => c.ParentConversationId == _book.Active.Id);
        }
        _refreshBranchNavigation = UpdateNavigation;
        bar.Loaded += (_, _) => UpdateNavigation();
        bar.IsVisibleChanged += (_, _) => { if (bar.IsVisible) UpdateNavigation(); };
        children.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = children, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var child in _book.Conversations.Where(c => c.ParentConversationId == _book.Active.Id))
            {
                var item = new MenuItem { Header = child.Title, ToolTip = child.Title };
                item.Click += (_, _) => OpenRelatedConversation(child.Id); menu.Items.Add(item);
            }
            children.ContextMenu = menu;
            if (menu.Items.Count > 0) menu.IsOpen = true;
        };
        map.Click += (_, _) => ConversationRelationsRequested?.Invoke(this, _book.Active.Id);
        DockPanel.SetDock(map, Dock.Right); bar.Children.Add(map); bar.Children.Add(parent); bar.Children.Add(children);
        ChatRoot.Children.Add(bar); Grid.SetRow(bar, 0);
    }

    private void AddBranchMenu(MarkdownView view, ContextMenu menu)
    {
        var branch = new MenuItem { Header = "开辟新会话" };
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
        var rows = turn.RevisionHistory?.LastOrDefault()?.Rows;
        string Owner(string id, string oldSegment) =>
            rows?.FirstOrDefault(r => r.Links.Any(l => l.Id == id))?.SegmentId ?? oldSegment;
        var branches = _book.Conversations.Where(c => c.ParentConversationId == conversationId && c.BranchAnchor?.TurnId == turn.Id &&
            Owner(c.Id, c.BranchAnchor.SegmentId) == segment.Id).ToArray();
        var localLinks = rows is null ? SameConversationLinks(turn.Id, segment.Id) : AiRevisionText.Segments(turn)
            .SelectMany(s => SameConversationLinks(turn.Id, s.Id)).Where(link => Owner(link.Id, link.Anchor.SegmentId) == segment.Id).ToList();
        if (branches.Length == 0 && localLinks.Count == 0 && view.BranchLinkBlocks.Count == 0) return;
        var branchIds = branches.Select(branch => branch.Id).Concat(localLinks.Select(link => link.Id)).ToHashSet(StringComparer.Ordinal);
        var obsolete = view.BranchLinkBlocks.Where(pair => !branchIds.Contains(pair.Key)).ToArray();
        var visibleTurn = obsolete.Length > 0 && view.IsVisible ? ActiveReadingTurn() : null;
        var previousTop = visibleTurn?.TranslatePoint(new Point(), MessagesScroll).Y;
        var previousOffset = MessagesScroll.VerticalOffset;
        foreach (var (id, _) in obsolete)
        {
            view.RemoveBranchLink(id);
        }
        if (visibleTurn is not null && previousTop is { } top)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!_disposed && _book.Active.Id == conversationId && visibleTurn.IsLoaded && Math.Abs(MessagesScroll.VerticalOffset - previousOffset) < .5)
            {
                var currentTop = visibleTurn.TranslatePoint(new Point(), MessagesScroll).Y;
                MessagesScroll.ScrollToVerticalOffset(Math.Max(0, previousOffset + currentTop - top));
            }
        }
        if (_disposed || _book.Active.Id != conversationId || !view.IsLoaded) return;
        if (rows is not null)
        {
            var savedOrder = rows.Where(row => row.SegmentId == segment.Id).SelectMany(row => row.Links)
                .Select((link, i) => (link.Id, i)).ToDictionary(item => item.Id, item => item.i);
            var actions = branches.Select(branch => (branch.Id, branch.Title, Anchor: branch.BranchAnchor!, Before: branch.BranchContextBefore,
                    After: branch.BranchContextAfter, Open: (Action)(() => OpenRelatedConversation(branch.Id))))
                .Concat(localLinks.Select(link => (link.Id, Title: "[" + link.TargetNumber + "]", link.Anchor, link.Before,
                    link.After, Open: (Action)(() => OpenSameConversationTurn(link.TargetTurnId)))));
            foreach (var action in actions.OrderBy(item => savedOrder.GetValueOrDefault(item.Id, int.MaxValue)))
                view.AddBranchLink(action.Id, action.Title, action.Anchor, action.Before, action.After, action.Open);
            return;
        }
        foreach (var branch in branches)
        {
            var anchor = branch.BranchAnchor!;
            view.AddBranchLink(branch.Id, branch.Title, anchor, branch.BranchContextBefore, branch.BranchContextAfter,
                () => OpenRelatedConversation(branch.Id));
        }
        foreach (var link in localLinks)
            view.AddBranchLink(link.Id, "[" + link.TargetNumber + "]", link.Anchor, link.Before, link.After,
                () => OpenSameConversationTurn(link.TargetTurnId));
    }

    internal void RefreshVisibleBranchLinks()
    {
        foreach (var answer in _answerLayers.Keys.Where(view => view.IsVisible).ToArray()) AddBranchLinks(answer);
    }

    internal async Task BranchFromSelectionAsync(MarkdownView view)
    {
        if (_operation is not null || AnswerTurn(view) is not { } turn || view.DataContext is not AiSegment segment) return;
        var selection = view.GetAnnotationSelection(); var quote = selection.Quote.Trim();
        if (quote.Length == 0 || quote.Length > 40000) return;
        var parent = _book.Active;
        var turnIndex = parent.Turns.IndexOf(turn); if (turnIndex < 0) return;
        var preview = view.SelectedReferenceObject();
        var context = view.BookmarkSelectionContext();
        var table = view.SelectedReferenceTableMarkdown();
        var anchor = new AiAnswerAnchor(turn.Id, segment.Id, selection.Offset, selection.Length, quote, view.Revision)
        { TextOffset = selection.TextOffset, PreviewKind = preview.Kind, PreviewSource = preview.Source, PreviewImageUri = preview.Kind == "image" ? view.SelectedReferenceImageUri() : null, PreviewTableMarkdown = table,
          PreviewParts = preview.Kind is null && table is null ? view.SelectedReferenceParts() : null,
          PreviewNumber = preview.Number, PreviewBefore = context.Before, PreviewAfter = context.After };
        await SaveAsync();
        var snapshot = parent.Snapshot();
        var branch = new AiConversation { ParentConversationId = parent.Id, BranchAnchor = anchor, BranchContextBefore = context.Before, BranchContextAfter = context.After,
            Title = string.Concat(quote.EnumerateRunes().Take(16)),
            SelectedModelId = snapshot.SelectedModelId ?? Settings.SelectedModel?.Id,
            SelectedReasoningEffort = snapshot.SelectedReasoningEffort ?? Settings.SelectedEffort,
            InheritedTurns = snapshot.InheritedTurns.Concat(snapshot.Turns.Take(turnIndex + 1))
                .Select(t => t with { UserRevisionDraft = null, RevisionRequest = null }).ToList(),
            Entries = [AiSessionExcerptReferences.Create(parent.Id, anchor, context.Before, context.After, protectOrigin: true)] };
        _book.Conversations.Add(branch);
        foreach (var answer in _answerLayers.Keys.ToArray()) AddBranchLinks(answer);
        _refreshBranchNavigation?.Invoke();
        if (_store is not null) await _store.SaveAsync(); else await SaveAsync();
        OpenRelatedConversation(branch.Id);
    }

    private bool OpenAnswerReference(AiEntry entry)
    {
        if (entry.AnswerAnchor is not { } anchor ||
            !AiSessionExcerptReferences.TryResolve(_book, entry, out var source, out var before, out var after) || source is null) return false;
        var active = _book.Active;
        if (source.Id == active.Id)
        {
            string? sourceId = null;
            foreach (var turn in active.Turns)
            {
                var index = turn.SentAnnotations.FindIndex(item => item.Id == entry.Id);
                if (index < 0) continue;
                sourceId = "turn:" + turn.Id + ":" + index;
                break;
            }
            _ = NavigateAnswerReferenceAsync(anchor, before, after, sourceId);
            return true;
        }
        if (active.ParentConversationId == source.Id && AiAnswerAnchor.SamePayload(active.BranchAnchor, anchor) && BranchSourceRequested is not null)
        {
            BranchSourceRequested(this, active);
            return true;
        }
        if (AnswerSourceRequested is null) return false;
        AnswerSourceRequested(source.Id, anchor, before, after);
        return true;
    }

    private async Task NavigateAnswerReferenceAsync(AiAnswerAnchor anchor, string? before, string? after, string? sourceId)
    {
        try { if (!await NavigateBranchSourceAsync(anchor, before, after, sourceId)) StatusText.Text = "无法定位引用出处，原回答可能已改变。"; }
        catch (Exception ex) { ShowError(ex); }
    }

    internal async Task<bool> NavigateBranchSourceAsync(AiAnswerAnchor anchor, string? before = null, string? after = null, string? sourceId = null)
    {
        CancelLayoutAnchor();
        var conversation = _book.Active;
        var turn = conversation.Turns.FirstOrDefault(t => t.Id == anchor.TurnId);
        if (turn is not null && _revisionEditors.ContainsKey(turn.Id)) CloseRevisionEditor(turn.Id);
        var index = turn is null ? -1 : RevealableTurnIndex(turn);
        if (turn is null || index < 0) return false;
        CancelReadingRestore();
        var readingVersion = _readingRestoreVersion;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != readingVersion || !IsVisible) return true;
        VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != readingVersion || !IsVisible) return true;
        if (_disposed || _book.Active != conversation || !_turnViews.TryGetValue(turn.Id, out var turnView)) return false;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != readingVersion || !IsVisible) return true;
        var latest = turn.RevisionHistory?.LastOrDefault();
        if (latest is not null && sourceId is null)
            sourceId = RevisionReferences(turn).FirstOrDefault(r => AiAnswerAnchor.SamePayload(r.Anchor, anchor))?.Id;
        var owner = latest?.Rows.FirstOrDefault(row => row.Links.Any(link => link.Id == sourceId))?.SegmentId ?? anchor.SegmentId;
        var view = turnView.Answers.FirstOrDefault(answer => answer.DataContext is AiSegment s && s.Id == owner);
        if (view is null) return false;
        await view.RenderTask;
        if (_readingRestoreVersion != readingVersion || !IsVisible) return true;
        if (_disposed || _book.Active != conversation) return false;
        var pointer = sourceId is null ? null : view.RevisionPointer(sourceId, reveal: true);
        pointer ??= view.ResolveBranchAnchor(anchor, before, after);
        History.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != readingVersion || !IsVisible) return true;
        if (pointer is null || view.BookmarkPoint(pointer) is not { } point) return false;
        var position = view.TranslatePoint(point, MessagesScroll);
        var centered = MessagesScroll.VerticalOffset + position.Y - MessagesScroll.ViewportHeight / 2;
        MessagesScroll.ScrollToVerticalOffset(Math.Clamp(centered, 0, MessagesScroll.ScrollableHeight));
        return true;
    }

    private void OpenRelatedConversation(string id)
    {
        if (ConversationOpenRequested is not null) ConversationOpenRequested(this, id);
        else _ = SelectConversationAsync(id);
    }

    internal TreeView CreateConversationRelations(Action<string> open)
    {
        var tree = new TreeView { Margin = new Thickness(12), FontSize = 15 };
        var conversations = _book.Conversations.ToArray();
        var byId = conversations.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var root = _book.Active;
        var visited = new HashSet<string>();
        while (visited.Add(root.Id) && root.ParentConversationId is { } parent && byId.TryGetValue(parent, out var next) && !visited.Contains(next.Id)) root = next;
        var selected = new HashSet<string> { root.Id };
        var childrenByParent = conversations.Where(c => c.ParentConversationId is not null).GroupBy(c => c.ParentConversationId!).ToDictionary(g => g.Key, g => g.ToArray());
        var pending = new Queue<string>(); pending.Enqueue(root.Id);
        while (pending.TryDequeue(out var parent))
            foreach (var child in childrenByParent.GetValueOrDefault(parent, []))
                if (selected.Add(child.Id)) pending.Enqueue(child.Id);
        var all = conversations.Where(c => selected.Contains(c.Id)).ToArray();
        if (all.Length <= 24)
        {
            TreeViewItem MakeSmallNode(AiConversation conversation, HashSet<string> ancestors)
            {
                var node = new TreeViewItem { Header = conversation.Title + (conversation.Id == _book.Active.Id ? "（当前）" : ""), Tag = conversation.Id, IsExpanded = true, Padding = new Thickness(2, 5, 2, 5) };
                if (!ancestors.Add(conversation.Id)) return node;
                foreach (var child in all.Where(c => c.ParentConversationId == conversation.Id && !ancestors.Contains(c.Id)))
                    node.Items.Add(MakeSmallNode(child, new HashSet<string>(ancestors)));
                return node;
            }
            tree.Items.Add(MakeSmallNode(root, []));
        }
        else
        {
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(tree, true);
        var style = new Style(typeof(TreeViewItem));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, true));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 5, 2, 5)));
        tree.Resources.Add(typeof(TreeViewItem), style);
        var template = new HierarchicalDataTemplate(typeof(RelationNode)) { ItemsSource = new Binding(nameof(RelationNode.Children)) };
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(RelationNode.Title)));
        template.VisualTree = label;
        tree.ItemTemplate = template;
        var nodes = all.ToDictionary(c => c.Id, c => new RelationNode(c, c.Id == _book.Active.Id));
        var children = all.Where(c => c.ParentConversationId is not null).GroupBy(c => c.ParentConversationId!).ToDictionary(g => g.Key, g => g.ToArray());
        RelationNode MakeNode(AiConversation conversation, HashSet<string> ancestors)
        {
            var node = nodes[conversation.Id];
            if (!ancestors.Add(conversation.Id)) return node;
            foreach (var child in children.GetValueOrDefault(conversation.Id, []).Where(c => !ancestors.Contains(c.Id)))
                node.Children.Add(MakeNode(child, new HashSet<string>(ancestors)));
            return node;
        }
        tree.Items.Add(MakeNode(root, []));
        }
        tree.MouseDoubleClick += (_, e) =>
        {
            for (var current = e.OriginalSource as DependencyObject; current is not null && current != tree; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is TreeViewItem item && (item.Tag as string ?? (item.DataContext as RelationNode)?.Id) is { } id) { e.Handled = true; open(id); break; }
        };
        return tree;
    }
}
