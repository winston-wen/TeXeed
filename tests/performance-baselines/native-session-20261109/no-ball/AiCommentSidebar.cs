using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>Edits the comments of one conversation's shared document.</summary>
internal sealed class AiCommentSidebar : UserControl
{
    private AiCommentDrafts _drafts = new();
    private readonly ListBox _list = new();
    private readonly Button _versions, _close;
    private readonly TextBlock _title = new() { Text = "评论", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private StackPanel _target = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 8) };
    private AiSharedDocument? _document;
    private string _conversation = "", _user = "用户";
    private Action? _changed, _draftChanged;
    private bool _refreshPending;
    private List<string?>? _presented;
    private string? _expandedComment;
    internal event EventHandler? VersionRequested;
    internal event EventHandler? CloseRequested;

    internal AiCommentSidebar()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var header = new DockPanel();
        _versions = Button("版本", () => VersionRequested?.Invoke(this, EventArgs.Empty));
        _close = Button("关闭", () => CloseRequested?.Invoke(this, EventArgs.Empty)); _close.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_versions, Dock.Right); header.Children.Add(_versions);
        DockPanel.SetDock(_close, Dock.Right); header.Children.Add(_close); header.Children.Add(_title);
        var heading = new StackPanel(); heading.Children.Add(header); heading.Children.Add(_status);
        grid.Children.Add(heading);
        _list.BorderThickness = new Thickness(0);
        _list.Background = System.Windows.Media.Brushes.Transparent;
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        _list.ItemContainerStyle = itemStyle;
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(AiCommentSidebarRowView)) };
        Grid.SetRow(_list, 1); grid.Children.Add(_list); Content = grid;
    }

    internal void Bind(AiConversation conversation, string? userName, Action draftChanged, Action documentChanged)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(draftChanged);
        ArgumentNullException.ThrowIfNull(documentChanged);
        if (_conversation != conversation.Id || !ReferenceEquals(_document, conversation.SharedDocument))
        { _list.ItemsSource = null; _expandedComment = null; _presented = null; }
        _conversation = conversation.Id; _document = conversation.SharedDocument; _drafts = conversation.CommentDrafts;
        _user = string.IsNullOrWhiteSpace(userName) ? "用户" : userName.Trim();
        _draftChanged = draftChanged; _changed = documentChanged; Refresh();
    }

    internal bool ShowClose { set => _close.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    internal string TitleText { set => _title.Text = value; }
    internal bool ShowVersions { set => _versions.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    internal bool HasDrafts => _drafts.NewComment.Length > 0 || _drafts.Edits.Values.Any(d => d.Text != d.Baseline) || _drafts.Replies.Values.Any(t => t.Length > 0);

    internal bool BeginComment(string expectedRevision, int start, int length)
    {
        var document = _document;
        if (document is null || document.Revision != expectedRevision || start < 0 || length <= 0 || start > document.Source.Length || length > document.Source.Length - start)
        { ShowStatus("源码已变化或选区无效，请重新选择。"); return false; }
        if (_drafts.Selection is { } previous && _drafts.NewComment.Length > 0 && previous.Revision == document.Revision)
        { ShowStatus("请先保存当前评论，再选择新位置。"); return false; }
        var end = start + length;
        if ((start > 0 && char.IsLowSurrogate(document.Source[start])) || (end < document.Source.Length && char.IsLowSurrogate(document.Source[end])))
        { ShowStatus("选区不能拆开字符，请重新选择。"); return false; }
        _drafts.Selection = new AiCommentSelectionDraft { Revision = expectedRevision, Start = start, Length = length, Quote = document.Source.Substring(start, length) };
        _draftChanged?.Invoke();
        ShowStatus(""); Refresh(); return true;
    }

    internal void Refresh()
    {
        if (_document is not { } document) return;
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox focused && IsAncestorOf(focused)) { _refreshPending = true; return; }
        _refreshPending = false;
        var state = PresentationState(document);
        if (_presented is not null && _presented.SequenceEqual(state)) return;
        _presented = state;
        var items = new List<AiCommentSidebarItem>();
        void Row(Action build) => items.Add(new AiCommentSidebarItem(() => BuildRow(build)));
        if (_drafts.Selection is { } selection)
            Row(() =>
            {
                AddLabel("新评论 · 已选择源码", true); AddQuote(selection.Quote);
                _target.Children.Add(Editor(_drafts.NewComment, value => { _drafts.NewComment = value; _draftChanged?.Invoke(); }));
                _target.Children.Add(Button("添加评论", AddComment));
            });
        var comments = document.Comments.ToArray();
        if (comments.Length == 0 && _drafts.Selection is null) Row(() => AddLabel("暂无评论。在源码中选择文字后添加评论。", false));
        foreach (var comment in comments)
            Row(() =>
            {
                var id = comment.Id;
                var heading = Author(comment.Author) + (comment.Orphaned ? " · 位置已失效" : "") + "\n" + comment.Text;
                var open = Button(heading, () => { _expandedComment = _expandedComment == id ? null : id; Refresh(); });
                open.Content = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap }; open.Tag = id;
                open.Template = CommentRowStyle.Template; open.Padding = new Thickness(0);
                open.HorizontalAlignment = HorizontalAlignment.Stretch; open.HorizontalContentAlignment = HorizontalAlignment.Left;
                open.ToolTip = "展开或收起评论"; _target.Children.Add(open);
                if (_expandedComment != id) return;
                AddQuote(comment.Quote); AddEditable("c:" + id, comment.Text, value => SaveComment(id, value));
                foreach (var reply in comment.Replies.ToArray())
                {
                    var replyId = reply.Id; AddLabel(Author(reply.Author), true);
                    AddEditable("r:" + id + ":" + replyId, reply.Text, value => SaveReply(id, replyId, value));
                }
                _drafts.Replies.TryGetValue(id, out var replyDraft);
                _target.Children.Add(Editor(replyDraft ?? "", value => { _drafts.Replies[id] = value; _draftChanged?.Invoke(); }, "写回复"));
                _target.Children.Add(Button("回复", () => AddReply(id)));
            });
        var validKeys = comments.SelectMany(c => new[] { "c:" + c.Id }.Concat(c.Replies.Select(r => "r:" + c.Id + ":" + r.Id))).ToHashSet();
        foreach (var item in _drafts.Edits.Where(x => !validKeys.Contains(x.Key) && x.Value.Text != x.Value.Baseline).ToArray())
            Row(() => { AddLabel("原评论或回复已移除；草稿仍保留，可复制内容。", false);
                _target.Children.Add(Editor(item.Value.Text, value => { item.Value.Text = value; _draftChanged?.Invoke(); })); });
        var commentIds = comments.Select(c => c.Id).ToHashSet();
        foreach (var item in _drafts.Replies.Where(x => !commentIds.Contains(x.Key) && x.Value.Length > 0).ToArray())
            Row(() => { AddLabel("原评论已移除；回复草稿仍保留，可复制内容。", false);
                _target.Children.Add(Editor(item.Value, value => { _drafts.Replies[item.Key] = value; _draftChanged?.Invoke(); })); });
        _list.ItemsSource = items;
    }

    private List<string?> PresentationState(AiSharedDocument document)
    {
        var selection = _drafts.Selection;
        var values = new List<string?> { _conversation, _expandedComment, selection?.Revision, selection?.Quote, selection?.Start.ToString(), selection?.Length.ToString(), _drafts.NewComment };
        foreach (var comment in document.Comments)
        {
            values.Add(comment.Id); values.Add(comment.Author); values.Add(comment.Text); values.Add(comment.Quote); values.Add(comment.Orphaned ? "orphan" : "located");
            foreach (var reply in comment.Replies) { values.Add(reply.Id); values.Add(reply.Author); values.Add(reply.Text); }
            values.Add(null);
        }
        foreach (var entry in _drafts.Edits.OrderBy(x => x.Key)) { values.Add(entry.Key); values.Add(entry.Value.Baseline); values.Add(entry.Value.Text); }
        values.Add(null);
        foreach (var entry in _drafts.Replies.OrderBy(x => x.Key)) { values.Add(entry.Key); values.Add(entry.Value); }
        return values;
    }

    private UIElement BuildRow(Action build)
    {
        var previous = _target; var row = new StackPanel(); _target = row;
        try { build(); return row; } finally { _target = previous; }
    }

    private static string Author(string? author) => string.IsNullOrWhiteSpace(author) ? "用户" : author;
    private void AddLabel(string text, bool prominent)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, prominent ? 10 : 4, 4, 3) };
        if (prominent) label.FontWeight = FontWeights.SemiBold; else label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _target.Children.Add(label);
    }
    private void AddQuote(string text)
    {
        var quote = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 4) };
        quote.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _target.Children.Add(quote);
    }
    private TextBox Editor(string text, Action<string> changed, string? tooltip = null)
    {
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 0, 4, 4), ToolTip = tooltip };
        input.TextChanged += (_, _) => changed(input.Text);
        input.LostKeyboardFocus += (_, _) => { if (_refreshPending) Dispatcher.BeginInvoke(new Action(Refresh)); };
        return input;
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, _) => { button.Focus(); action(); }; return button;
    }
    private void AddEditable(string key, string current, Func<string, bool> save)
    {
        if (!_drafts.Edits.TryGetValue(key, out var draft)) _drafts.Edits[key] = draft = new AiCommentDraft { Baseline = current, Text = current };
        else if (draft.Text == draft.Baseline) draft.Baseline = draft.Text = current;
        if (draft.Text != draft.Baseline && draft.Baseline != current)
        {
            AddLabel("当前已保存内容：" + current, false);
            _target.Children.Add(Button("以当前内容为基础", () => { draft.Baseline = current; _draftChanged?.Invoke(); ShowStatus("已保留输入，可检查后保存。"); Refresh(); }));
        }
        _target.Children.Add(Editor(draft.Text, value => { draft.Text = value; _draftChanged?.Invoke(); }));
        _target.Children.Add(Button("保存修改", () => { if (save(key)) ShowStatus(""); Refresh(); }));
    }
    private bool SaveComment(string id, string key)
    {
        var comment = _document?.Comments.FirstOrDefault(c => c.Id == id);
        var draft = _drafts.Edits[key];
        if (comment is null || comment.Text != draft.Baseline) { ShowStatus("评论已被其他修改更新；输入仍保留，请核对后处理。"); return false; }
        if (draft.Text == draft.Baseline) return true;
        comment.Text = draft.Text; draft.Baseline = draft.Text; _draftChanged?.Invoke(); _changed?.Invoke(); return true;
    }
    private bool SaveReply(string commentId, string replyId, string key)
    {
        var reply = _document?.Comments.FirstOrDefault(c => c.Id == commentId)?.Replies.FirstOrDefault(r => r.Id == replyId);
        var draft = _drafts.Edits[key];
        if (reply is null || reply.Text != draft.Baseline) { ShowStatus("回复已被其他修改更新；输入仍保留，请核对后处理。"); return false; }
        if (draft.Text == draft.Baseline) return true;
        reply.Text = draft.Text; draft.Baseline = draft.Text; _draftChanged?.Invoke(); _changed?.Invoke(); return true;
    }
    private void AddComment()
    {
        var document = _document;
        if (document is null || _drafts.Selection is not { } selection || string.IsNullOrWhiteSpace(_drafts.NewComment)) { ShowStatus("请填写评论。"); return; }
        if (document.Revision != selection.Revision || selection.Start < 0 || selection.Length <= 0 || selection.Start > document.Source.Length || selection.Length > document.Source.Length - selection.Start || document.Source.Substring(selection.Start, selection.Length) != selection.Quote)
        { ShowStatus("源码已变化，评论输入仍保留；请核对选区。"); return; }
        document.Comments.Add(new AiDocumentComment { Author = _user, Role = "user", Start = selection.Start, Length = selection.Length, Quote = selection.Quote, AnchorRevision = selection.Revision, Text = _drafts.NewComment });
        _drafts.NewComment = ""; _drafts.Selection = null; _draftChanged?.Invoke(); _changed?.Invoke(); ShowStatus(""); Refresh();
    }
    private void AddReply(string id)
    {
        var comment = _document?.Comments.FirstOrDefault(c => c.Id == id);
        if (comment is null) { ShowStatus("评论已被其他修改移除，输入仍保留。"); return; }
        if (!_drafts.Replies.TryGetValue(id, out var text) || string.IsNullOrWhiteSpace(text)) { ShowStatus("请填写回复。"); return; }
        comment.Replies.Add(new AiDocumentReply { Author = _user, Role = "user", Text = text });
        _drafts.Replies.Remove(id); _draftChanged?.Invoke(); _changed?.Invoke(); ShowStatus(""); Refresh();
    }
    private void ShowStatus(string message) => _status.Text = message;
}

internal sealed record AiCommentSidebarItem(Func<UIElement> Build);

public sealed class AiCommentSidebarRowView : ContentControl
{
    public AiCommentSidebarRowView() => DataContextChanged += (_, _) => Content = DataContext is AiCommentSidebarItem item ? item.Build() : null;
}
