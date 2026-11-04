using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private const double BookmarkBallSize = 36;
    private Canvas _bookmarkOverlay = null!;
    private Button _bookmarkBall = null!;
    private Border? _bookmarkSurface;
    private TextBox? _bookmarkSearch;
    private ListBox? _defaultBookmarks, _userBookmarks;
    private TextBlock? _bookmarkEmpty;
    private ComboBox? _bookmarkOrder;
    private IReadOnlyList<AiBookmarkTarget> _bookmarkItems = [];
    private Point _bookmarkPress, _bookmarkStart;
    private bool _bookmarkDragging, _bookmarkMoved, _bookmarkPositioned;

    private sealed record BookmarkRow(AiBookmarkTarget Target)
    {
        public string Display => string.IsNullOrWhiteSpace(Target.Description) ? Target.Label : $"{Target.Label}  {Target.Description}";
    }

    private void InitializeBookmarkUi()
    {
        _bookmarkOverlay = new Canvas { ClipToBounds = true };
        Grid.SetRowSpan(_bookmarkOverlay, 4); Panel.SetZIndex(_bookmarkOverlay, 10);
        ChatRoot.Children.Add(_bookmarkOverlay);
        _bookmarkBall = new Button { Width = BookmarkBallSize, Height = BookmarkBallSize, Padding = new Thickness(9),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "书签与跳转 · Ctrl+G" };
        _bookmarkBall.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
        _bookmarkBall.SetResourceReference(Control.BorderBrushProperty, "Guide");
        _bookmarkBall.SetResourceReference(Control.ForegroundProperty, "Muted");
        AutomationProperties.SetName(_bookmarkBall, "书签与跳转");
        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(BookmarkBallSize / 2));
        chrome.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        chrome.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter); template.VisualTree = chrome; _bookmarkBall.Template = template;
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M3,2 H13 V15 L8,12 3,15 Z"), Width = 16, Height = 16,
            Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = _bookmarkBall });
        _bookmarkBall.Content = icon;
        _bookmarkBall.PreviewMouseLeftButtonDown += BookmarkBall_Down;
        _bookmarkBall.PreviewMouseMove += BookmarkBall_Move;
        _bookmarkBall.PreviewMouseLeftButtonUp += BookmarkBall_Up;
        _bookmarkBall.LostMouseCapture += (_, _) => _bookmarkDragging = false;
        _bookmarkBall.Click += (_, _) => ToggleBookmarkUi();
        _bookmarkOverlay.Children.Add(_bookmarkBall);
        _bookmarkOverlay.SizeChanged += (_, _) => PositionBookmarkBall();
        ChatRoot.PreviewMouseDown += (_, e) =>
        {
            if (_bookmarkSurface?.Visibility == Visibility.Visible && !WithinBookmarkElement(e.OriginalSource as DependencyObject, _bookmarkSurface) &&
                !WithinBookmarkElement(e.OriginalSource as DependencyObject, _bookmarkBall)) CloseBookmarkUi();
        };
        Loaded += (_, _) => PositionBookmarkBall();
    }

    private void RestoreBookmarkBallPosition()
    {
        _bookmarkPositioned = false;
        CloseBookmarkUi();
        PositionBookmarkBall();
    }

    private void PositionBookmarkBall()
    {
        if (_bookmarkOverlay.ActualWidth <= 0 || _bookmarkOverlay.ActualHeight <= 0) return;
        var width = Math.Max(0, _bookmarkOverlay.ActualWidth - BookmarkBallSize);
        var height = Math.Max(0, _bookmarkOverlay.ActualHeight - BookmarkBallSize);
        if (!_bookmarkPositioned)
        {
            Canvas.SetLeft(_bookmarkBall, width * Math.Clamp(_book.Active.BookmarkBallX ?? .98, 0, 1));
            Canvas.SetTop(_bookmarkBall, height * Math.Clamp(_book.Active.BookmarkBallY ?? .45, 0, 1));
            _bookmarkPositioned = true;
        }
        else
        {
            Canvas.SetLeft(_bookmarkBall, Math.Clamp(Canvas.GetLeft(_bookmarkBall), 0, width));
            Canvas.SetTop(_bookmarkBall, Math.Clamp(Canvas.GetTop(_bookmarkBall), 0, height));
        }
        PlaceBookmarkSurface();
    }

    private void BookmarkBall_Down(object sender, MouseButtonEventArgs e)
    {
        _bookmarkPress = e.GetPosition(_bookmarkOverlay);
        _bookmarkStart = new Point(Canvas.GetLeft(_bookmarkBall), Canvas.GetTop(_bookmarkBall));
        _bookmarkDragging = _bookmarkBall.CaptureMouse(); _bookmarkMoved = false; e.Handled = true;
    }
    private void BookmarkBall_Move(object sender, MouseEventArgs e)
    {
        if (!_bookmarkDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_bookmarkOverlay); var dx = point.X - _bookmarkPress.X; var dy = point.Y - _bookmarkPress.Y;
        if (!_bookmarkMoved && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
        _bookmarkMoved = true; CloseBookmarkUi();
        Canvas.SetLeft(_bookmarkBall, Math.Clamp(_bookmarkStart.X + dx, 0, Math.Max(0, _bookmarkOverlay.ActualWidth - BookmarkBallSize)));
        Canvas.SetTop(_bookmarkBall, Math.Clamp(_bookmarkStart.Y + dy, 0, Math.Max(0, _bookmarkOverlay.ActualHeight - BookmarkBallSize)));
        e.Handled = true;
    }
    private void BookmarkBall_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_bookmarkDragging) return;
        var moved = _bookmarkMoved; _bookmarkDragging = false; _bookmarkBall.ReleaseMouseCapture();
        if (moved)
        {
            var width = Math.Max(0, _bookmarkOverlay.ActualWidth - BookmarkBallSize);
            var height = Math.Max(0, _bookmarkOverlay.ActualHeight - BookmarkBallSize);
            _book.Active.BookmarkBallX = width > 0 ? Canvas.GetLeft(_bookmarkBall) / width : 0;
            _book.Active.BookmarkBallY = height > 0 ? Canvas.GetTop(_bookmarkBall) / height : 0;
            Dirty();
        }
        else ToggleBookmarkUi();
        e.Handled = true;
    }
    private void ToggleBookmarkUi()
    {
        if (_bookmarkSurface?.Visibility == Visibility.Visible) CloseBookmarkUi();
        else OpenBookmarkUi(focusSearch: false);
    }
    private void OpenBookmarkUi(bool focusSearch)
    {
        EnsureBookmarkSurface();
        _bookmarkItems = GetBookmarks();
        _bookmarkOrder!.SelectedIndex = _book.Active.BookmarkSort == "alphabetical" ? 1 : 0;
        _bookmarkSearch!.Text = ""; RefreshBookmarkLists();
        _bookmarkSurface!.Visibility = Visibility.Visible; PlaceBookmarkSurface();
        if (focusSearch) { _bookmarkSearch.Focus(); _bookmarkSearch.SelectAll(); }
    }
    private void CloseBookmarkUi()
    {
        if (_bookmarkSurface is not null) _bookmarkSurface.Visibility = Visibility.Collapsed;
    }
    private void EnsureBookmarkSurface()
    {
        if (_bookmarkSurface is not null) return;
        var panel = new StackPanel();
        var heading = new TextBlock { Text = "书签", FontSize = 15, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); panel.Children.Add(heading);
        _bookmarkSearch = new TextBox { IsReadOnly = false, ToolTip = "输入标题号、公式号或标签；↑↓ 选择，Tab 补全，Enter 跳转" };
        AutomationProperties.SetName(_bookmarkSearch, "跳转到书签");
        _bookmarkSearch.TextChanged += (_, _) => RefreshBookmarkLists();
        _bookmarkSearch.PreviewKeyDown += BookmarkSearch_KeyDown;
        var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 8) }; searchBox.Children.Add(_bookmarkSearch);
        var hint = new TextBlock { Text = "跳转到书签 · Ctrl+G", FontSize = 12, Margin = new Thickness(9, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var hintStyle = new Style(typeof(TextBlock)); hintStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var empty = new DataTrigger { Binding = new Binding("Text") { Source = _bookmarkSearch }, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible)); hintStyle.Triggers.Add(empty); hint.Style = hintStyle;
        searchBox.Children.Add(hint); panel.Children.Add(searchBox);
        var defaults = new TextBlock { Text = "默认书签", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        defaults.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(defaults);
        _defaultBookmarks = CreateBookmarkList(false); _defaultBookmarks.MaxHeight = 158; panel.Children.Add(_defaultBookmarks);
        var userHeader = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        var userLabel = new TextBlock { Text = "用户标签", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        userLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _bookmarkOrder = new ComboBox { Width = 120, FontSize = 12, Padding = new Thickness(4, 1, 4, 1), ItemsSource = new[] { "按行文顺序", "按字典序" }, SelectedIndex = 0 };
        AutomationProperties.SetName(_bookmarkOrder, "用户标签排序");
        _bookmarkOrder.SelectionChanged += (_, _) =>
        {
            var order = _bookmarkOrder.SelectedIndex == 1 ? "alphabetical" : "document";
            if (_book.Active.BookmarkSort != order) { _book.Active.BookmarkSort = order; Dirty(); }
            RefreshBookmarkLists();
        };
        DockPanel.SetDock(_bookmarkOrder, Dock.Right); userHeader.Children.Add(_bookmarkOrder); userHeader.Children.Add(userLabel); panel.Children.Add(userHeader);
        _userBookmarks = CreateBookmarkList(true); _userBookmarks.MaxHeight = 118; panel.Children.Add(_userBookmarks);
        _bookmarkEmpty = new TextBlock { Text = "没有匹配的书签", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _bookmarkEmpty.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(_bookmarkEmpty);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _bookmarkSurface = new Border { Child = scroll, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        _bookmarkSurface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); _bookmarkSurface.SetResourceReference(Border.BorderBrushProperty, "Guide");
        _bookmarkSurface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseBookmarkUi(); _bookmarkBall.Focus(); e.Handled = true; } };
        _bookmarkOverlay.Children.Add(_bookmarkSurface);
    }
    private ListBox CreateBookmarkList(bool user)
    {
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, user ? "用户标签" : "默认书签");
        var template = new DataTemplate(typeof(BookmarkRow)); var row = new FrameworkElementFactory(typeof(DockPanel));
        if (user)
        {
            var remove = new FrameworkElementFactory(typeof(Button)); remove.SetValue(ContentControl.ContentProperty, "×");
            remove.SetValue(FrameworkElement.ToolTipProperty, "删除标签"); remove.SetValue(Control.PaddingProperty, new Thickness(5, 0, 5, 0));
            remove.SetValue(Control.BorderThicknessProperty, new Thickness(0)); remove.SetValue(DockPanel.DockProperty, Dock.Right);
            remove.SetBinding(FrameworkElement.TagProperty, new Binding("Target")); remove.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RemoveBookmark_Click)); row.AppendChild(remove);
        }
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Display"));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3)); row.AppendChild(text); template.VisualTree = row; list.ItemTemplate = template;
        list.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (FindBookmarkButton(e.OriginalSource as DependencyObject)) return;
            var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is BookmarkRow selected) { e.Handled = true; await GoBookmarkAsync(selected.Target); }
        };
        list.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is BookmarkRow selected) { e.Handled = true; await GoBookmarkAsync(selected.Target); } };
        return list;
    }
    private static bool FindBookmarkButton(DependencyObject? element)
    {
        for (var p = element; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is Button) return true;
        return false;
    }
    private static bool WithinBookmarkElement(DependencyObject? element, DependencyObject ancestor)
    {
        for (var p = element; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }
    private void RemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AiBookmarkTarget target } && RemoveBookmark(target))
        { _bookmarkItems = GetBookmarks(); RefreshBookmarkLists(); }
    }
    private void RefreshBookmarkLists()
    {
        if (_defaultBookmarks is null || _userBookmarks is null || _bookmarkSearch is null) return;
        var query = _bookmarkSearch.Text.Trim();
        var numbered = query.Length > 0 && char.IsDigit(query[0]);
        var matches = _bookmarkItems.Where(b => query.Length == 0 || (numbered ? b.Label.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) : b.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)) || b.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var defaults = matches.Where(b => b.Kind != AiBookmarkKind.User).Select(b => new BookmarkRow(b)).ToArray();
        var users = matches.Where(b => b.Kind == AiBookmarkKind.User);
        if (_bookmarkOrder?.SelectedIndex == 1) users = users.OrderBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.TurnIndex).ThenBy(b => b.SegmentIndex).ThenBy(b => b.Position);
        var userRows = users.Select(b => new BookmarkRow(b)).ToArray();
        _defaultBookmarks.ItemsSource = defaults; _userBookmarks.ItemsSource = userRows;
        _defaultBookmarks.Visibility = defaults.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _userBookmarks.Visibility = userRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_bookmarkEmpty is not null) _bookmarkEmpty.Visibility = defaults.Length + userRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceBookmarkSurface();
    }
    private void PlaceBookmarkSurface()
    {
        if (_bookmarkSurface is null || _bookmarkSurface.Visibility != Visibility.Visible || _bookmarkOverlay.ActualWidth <= 0) return;
        _bookmarkSurface.Width = Math.Max(0, Math.Min(360, _bookmarkOverlay.ActualWidth - 8));
        _bookmarkSurface.MaxHeight = Math.Max(0, _bookmarkOverlay.ActualHeight - 8);
        _bookmarkSurface.Measure(new Size(_bookmarkSurface.Width, _bookmarkSurface.MaxHeight));
        var x = Canvas.GetLeft(_bookmarkBall); var y = Canvas.GetTop(_bookmarkBall);
        var left = x - _bookmarkSurface.Width - 8;
        if (left < 4) left = Math.Min(x + BookmarkBallSize + 8, Math.Max(4, _bookmarkOverlay.ActualWidth - _bookmarkSurface.Width - 4));
        Canvas.SetLeft(_bookmarkSurface, Math.Max(0, left));
        Canvas.SetTop(_bookmarkSurface, Math.Clamp(y, 0, Math.Max(0, _bookmarkOverlay.ActualHeight - _bookmarkSurface.DesiredSize.Height)));
    }
    private async void BookmarkSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            var lists = new[] { _defaultBookmarks!, _userBookmarks! }.Where(l => l.Items.Count > 0).ToArray();
            var count = lists.Sum(l => l.Items.Count); if (count == 0) return;
            var index = -1; var offset = 0;
            foreach (var list in lists) { if (list.SelectedIndex >= 0) index = offset + list.SelectedIndex; offset += list.Items.Count; }
            index = index < 0 ? (e.Key == Key.Down ? 0 : count - 1) : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            var chosen = lists[0];
            foreach (var list in lists) { list.SelectedIndex = -1; if (index >= list.Items.Count) { index -= list.Items.Count; continue; } chosen = list; break; }
            chosen.SelectedIndex = index;
            chosen.ScrollIntoView(chosen.SelectedItem); e.Handled = true;
        }
        else if (e.Key == Key.Tab || e.Key == Key.Enter)
        {
            var row = _defaultBookmarks!.SelectedItem as BookmarkRow ?? _userBookmarks!.SelectedItem as BookmarkRow ??
                _defaultBookmarks.Items.Cast<BookmarkRow>().FirstOrDefault() ?? _userBookmarks.Items.Cast<BookmarkRow>().FirstOrDefault();
            if (row is null) return;
            e.Handled = true;
            if (e.Key == Key.Tab) { _bookmarkSearch!.Text = row.Target.Label; _bookmarkSearch.CaretIndex = _bookmarkSearch.Text.Length; }
            else await GoBookmarkAsync(row.Target);
        }
    }
    private async Task GoBookmarkAsync(AiBookmarkTarget target)
    {
        CloseBookmarkUi();
        if (!await NavigateBookmarkAsync(target)) StatusText.Text = "未能定位书签，原内容可能已变化。";
    }

    private void RequestBookmarkLabel(AiBookmarkSelection selection)
    {
        var selected = selection.Anchor.Quote.Trim();
        var dialog = new Window { Title = "添加标签", Owner = Window.GetWindow(this), Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "标签名称", Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { IsReadOnly = false, Text = string.Concat(selected.EnumerateRunes().Take(40)), MaxLength = 120 };
        AutomationProperties.SetName(input, "标签名称"); content.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var add = new Button { Content = "添加", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
        add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(add); content.Children.Add(actions); dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() == true && !AddBookmarkFromSelection(selection, input.Text.Trim())) StatusText.Text = "无法添加标签，请重新选择文字。";
    }
}
