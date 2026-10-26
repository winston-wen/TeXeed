using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class AiHistoryView : UserControl
{
    private const double RowHeight = 36;
    private readonly AiNotebook _book;
    private readonly Func<IReadOnlyCollection<string>, Task> _delete;
    private readonly Func<string, string, Task> _rename;
    private readonly AiHistoryPage _page;
    private readonly Func<string, bool> _isOpen;
    private readonly Action<string> _open;
    private readonly DispatcherTimer _openTimer = new();
    private string? _openId;
    private TextBox? _editor;
    private string? _editingId;
    private bool _busy;
    internal AiHistoryPage Page => _page;
    public string? SelectedConversationId { get; private set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    internal AiHistoryView(AiNotebook book, Func<IReadOnlyCollection<string>, Task> delete, Func<string, string, Task> rename, Func<string, bool> isOpen, Action<string> open)
    {
        _book = book; _delete = delete; _rename = rename; _page = new(book); _isOpen = isOpen; _open = open;
        InitializeComponent();
        _openTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (_openId is { } id && _editor is null) Open(id); };
        Unloaded += async (_, _) => { _openTimer.Stop(); await CommitRenameAsync(); };
        IsVisibleChanged += async (_, _) => { if (!IsVisible) { _openTimer.Stop(); await CommitRenameAsync(); } };
        Refresh(); Loaded += (_, _) => SearchBox.Focus();
    }
    internal static IReadOnlyList<AiConversation> Search(AiNotebook book, string query) => AiHistoryPage.Search(book, query);
    private void Refresh(bool reset = false) { _openTimer.Stop(); _page.Refresh(SearchBox.Text, reset); if (_editor is null) Render(); }
    internal void RefreshExternal() => Refresh();
    private void Render()
    {
        _page.Selected.RemoveWhere(id => _isOpen(id));
        ConversationList.Items.Clear();
        foreach (var conversation in _page.Visible)
        {
            var row = new Grid { Height = RowHeight, Tag = conversation.Id };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = _page.Selected.Contains(conversation.Id), VerticalAlignment = VerticalAlignment.Center };
            check.IsEnabled = !_isOpen(conversation.Id);
            check.ToolTip = check.IsEnabled ? "选择此会话" : "会话窗口已打开，关闭后才能删除"; ToolTipService.SetShowOnDisabled(check, true);
            System.Windows.Automation.AutomationProperties.SetHelpText(check, check.ToolTip.ToString());
            System.Windows.Automation.AutomationProperties.SetName(check, "选择对话：" + conversation.Title);
            check.Click += (_, _) => { _openTimer.Stop(); if (check.IsChecked == true) _page.Selected.Add(conversation.Id); else _page.Selected.Remove(conversation.Id); UpdateSelection(); };
            var title = new TextBlock { Text = conversation.Title, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = conversation.Title, Cursor = Cursors.Hand, Focusable = true };
            title.SetResourceReference(ForegroundProperty, conversation.Id == _book.ActiveConversationId ? "Accent" : "Ink");
            title.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; title.Focus(); _openTimer.Stop();
                if (e.ClickCount == 2) BeginRename(conversation.Id);
                else { _openId = conversation.Id; _openTimer.Start(); }
            };
            title.KeyDown += (_, e) => { if (e.Key == Key.F2) { BeginRename(conversation.Id); e.Handled = true; } else if (e.Key == Key.Enter) { Open(conversation.Id); e.Handled = true; } };
            Grid.SetColumn(title, 1); row.Children.Add(check); row.Children.Add(title);
            if (_isOpen(conversation.Id)) { var status = new TextBlock { Text = "已打开", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,4,0), ToolTip = "会话窗口已打开，关闭后才能删除" }; status.SetResourceReference(ForegroundProperty,"Muted"); Grid.SetColumn(status,2); row.Children.Add(status); }
            ConversationList.Items.Add(row);
        }
        PageBox.Text = _page.PageNumber.ToString(); ResultCount.Text = $"页，共 {_page.PageCount} 页、{_page.Matches.Count} 条";
        EmptyText.Visibility = _page.Matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }
    private void UpdateSelection() { SelectedCount.Text = $"已选中 {_page.Selected.Count} 条"; DeleteButton.IsEnabled = !_busy && _page.Selected.Count > 0; }
    internal void BeginRename(string id)
    {
        _openTimer.Stop();
        if (_busy || _editor is not null) return;
        var row = ConversationList.Items.Cast<Grid>().FirstOrDefault(r => (string)r.Tag == id);
        var conversation = _book.Conversations.FirstOrDefault(c => c.Id == id);
        if (row is null || conversation is null) return;
        _editingId = id; ((TextBlock)row.Children[1]).Visibility = Visibility.Collapsed;
        _editor = new TextBox { Text = conversation.Title, FontSize = 16, MaxLength = 200, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 4, 2) };
        Grid.SetColumn(_editor, 1); row.Children.Add(_editor);
        var accept = new Button { Content = "✓", ToolTip = "提交标题", Width = 30, Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(accept, "提交标题");
        accept.Click += async (_, _) => await CommitRenameAsync(); Grid.SetColumn(accept, 3); row.Children.Add(accept);
        _editor.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await CommitRenameAsync(); } else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); } };
        _editor.Focus(); _editor.SelectAll();
    }
    internal async Task CommitRenameAsync()
    {
        if (_editor is null || _editingId is null || _busy) return;
        var name = _editor.Text.Trim(); if (name.Length == 0) { _editor.Focus(); return; }
        var id = _editingId; _busy = true; DialogBody.IsEnabled = false;
        try { await _rename(id, name); _editor = null; _editingId = null; Refresh(); }
        catch (Exception ex) { ResultCount.Text = "改名未完成：" + ex.Message; }
        finally { _busy = false; DialogBody.IsEnabled = true; UpdateSelection(); }
    }
    private void CancelRename() { _editor = null; _editingId = null; Render(); }
    private async void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _openTimer.Stop();
        if (_editor is null || _busy) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null) { if (ReferenceEquals(source, _editor)) return; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); }
        e.Handled = true;
        await CommitRenameAsync(); // An outside click submits, without also opening/deleting a row.
    }
    private void Open(string id)
    {
        _openTimer.Stop(); if (_busy || !_book.Conversations.Any(c => c.Id == id)) return;
        SelectedConversationId = id; _open(id);
    }
    private async void Search_Changed(object sender, TextChangedEventArgs e) { if (ConversationList is null) return; _openTimer.Stop(); await CommitRenameAsync(); Refresh(true); }
    private async void Search_Click(object sender, RoutedEventArgs e) { await CommitRenameAsync(); Refresh(true); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await CommitRenameAsync(); Refresh(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { _openTimer.Stop(); _page.Selected.UnionWith(_page.Matches.Where(c => !_isOpen(c.Id)).Select(c => c.Id)); Render(); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { _openTimer.Stop(); _page.Selected.Clear(); Render(); }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _page.Selected.Count == 0) return;
        _openTimer.Stop(); _busy = true; DialogBody.IsEnabled = false;
        try { await _delete(_page.Selected.ToArray()); Refresh(); }
        catch (Exception ex) { ResultCount.Text = "删除未完成：" + ex.Message; }
        finally { _busy = false; DialogBody.IsEnabled = true; UpdateSelection(); }
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_page is null) return;
        var capacity = Math.Max(1, (int)(RowsViewport.ActualHeight / RowHeight));
        if (capacity == _page.Capacity) return;
        _openTimer.Stop();
        _page.Resize(capacity);
        if (_editor is null) Render(); // Keep an unfinished rename when the window changes size.
    }
    private void Rows_Wheel(object sender, MouseWheelEventArgs e) { if (_editor is not null) return; _page.GoTo(_page.PageNumber + (e.Delta < 0 ? 1 : -1)); Render(); e.Handled = true; }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (_editor is null) { _page.GoTo(_page.PageNumber - 1); Render(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_editor is null) { _page.GoTo(_page.PageNumber + 1); Render(); } }
    private void Go_Click(object sender, RoutedEventArgs e) { if (_editor is not null) return; _openTimer.Stop(); if (int.TryParse(PageBox.Text, out var page)) _page.GoTo(page); Render(); }
    private void Page_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Go_Click(sender, e); e.Handled = true; } }
}
