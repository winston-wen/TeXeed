using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>A constrained menu that presents one hierarchy level at a time.</summary>
public sealed class CompactMenuPanel : Border
{
    private readonly MenuItem[] _roots;
    private readonly Style _rowStyle;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _back;
    private readonly List<(MenuItem Item, double Offset)> _path = [];
    private readonly List<Button> _buttons = [];
    public event EventHandler? CloseRequested;
    public int Depth => _path.Count;
    public MenuItem? CurrentMenu => _path.LastOrDefault().Item;

    public CompactMenuPanel(MenuItem[] roots, Style rowStyle)
    {
        _roots = roots; _rowStyle = rowStyle;
        SetResourceReference(BackgroundProperty, "SidebarBackground");
        SetResourceReference(BorderBrushProperty, "Line");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3);
        ScrollActivity.SetIsScope(this, true);
        // A constrained menu must visibly advertise its overflow.
        _scroll.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar), (Style)Application.Current.FindResource(typeof(ScrollBar)))
        { Setters = { new Setter(ScrollActivity.AutoHideProperty, false) } };
        _scroll.Content = _rows;
        _scroll.PreviewMouseWheel += (_, e) => { _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta / 120d * 102); e.Handled = true; };
        _back = new Button { Style = rowStyle, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_back, "返回上一级菜单");
        _back.Click += (_, _) => GoBack();
        var layout = new DockPanel();
        DockPanel.SetDock(_back, Dock.Top); layout.Children.Add(_back); layout.Children.Add(_scroll); Child = layout;
        PreviewKeyDown += OnMenuKey;
    }

    public void Open() { _path.Clear(); Render(); }
    public void NavigateTo(MenuItem item) { Open(); Enter(item); }
    private void Enter(MenuItem item)
    {
        _path.Add((item, _scroll.VerticalOffset));
        // Lazily populated menus (e.g. themes) keep their normal opened handler.
        item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
        Render();
    }
    public bool GoBack()
    {
        if (_path.Count == 0) return false;
        var previous = _path[^1]; _path.RemoveAt(_path.Count - 1);
        Render(previous.Offset, previous.Item); return true;
    }
    public void FocusItem(MenuItem item)
    {
        var button = _buttons.FirstOrDefault(b => ReferenceEquals(b.Tag, item));
        button?.Focus(); button?.BringIntoView();
    }
    private static string Label(MenuItem? item) => (item?.Header?.ToString() ?? "").Replace("_", "");
    private void Render(double offset = 0, MenuItem? selected = null)
    {
        _rows.Children.Clear(); _buttons.Clear();
        _back.Visibility = Depth == 0 ? Visibility.Collapsed : Visibility.Visible;
        _back.Content = new TextBlock { Text = "‹  " + Label(CurrentMenu!), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var items = Depth == 0 ? _roots.Cast<object>() : CurrentMenu!.Items.Cast<object>();
        foreach (var entry in items)
        {
            if (entry is Separator)
            {
                var separator = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                separator.SetResourceReference(BackgroundProperty, "Line"); _rows.Children.Add(separator); continue;
            }
            if (entry is not MenuItem item || item.Visibility != Visibility.Visible) continue;
            var button = new Button { Style = _rowStyle, Tag = item, ToolTip = Label(item) };
            button.SetBinding(IsEnabledProperty, new Binding(nameof(MenuItem.IsEnabled)) { Source = item });
            AutomationProperties.SetName(button, Label(item));
            var row = new Grid();
            foreach (var width in new[] { new GridLength(22), new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(20) })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var check = new TextBlock { Text = "✓", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            check.SetBinding(VisibilityProperty, new Binding(nameof(MenuItem.IsChecked)) { Source = item, Converter = new BooleanToVisibilityConverter() });
            row.Children.Add(check);
            var label = new TextBlock { Text = Label(item), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var gesture = new TextBlock { Text = item.InputGestureText, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            gesture.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(gesture, 2); row.Children.Add(gesture);
            var arrow = new TextBlock { Text = item.HasItems ? "›" : "", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 3); row.Children.Add(arrow); button.Content = row;
            button.Click += (_, _) => Activate(item);
            _rows.Children.Add(button); _buttons.Add(button);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scroll.ScrollToVerticalOffset(offset);
            if (selected is not null) FocusItem(selected);
            else _buttons.FirstOrDefault(b => b.IsEnabled)?.Focus();
        }));
    }
    private void Activate(MenuItem item)
    {
        if (!item.IsEnabled) return;
        if (item.HasItems) { Enter(item); return; }
        CloseRequested?.Invoke(this, EventArgs.Empty);
        if (item.IsCheckable) item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        if (item.Command is RoutedCommand command && command.CanExecute(item.CommandParameter, item.CommandTarget))
            command.Execute(item.CommandParameter, item.CommandTarget);
        else if (item.Command is { } ordinary && ordinary.CanExecute(item.CommandParameter)) ordinary.Execute(item.CommandParameter);
    }
    private void OnMenuKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape) { if (!GoBack()) CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (e.Key == Key.Left && GoBack()) { e.Handled = true; return; }
        var index = _buttons.FindIndex(b => b.IsKeyboardFocusWithin);
        if (e.Key is Key.Enter or Key.Right && index >= 0)
        {
            var item = (MenuItem)_buttons[index].Tag;
            if (e.Key == Key.Enter || item.HasItems) Activate(item);
            e.Handled = true; return;
        }
        if (e.Key is not (Key.Down or Key.Up or Key.Home or Key.End)) return;
        var enabled = _buttons.Where(b => b.IsEnabled).ToArray(); if (enabled.Length == 0) return;
        index = Array.FindIndex(enabled, b => b.IsKeyboardFocusWithin);
        index = e.Key switch { Key.Home => 0, Key.End => enabled.Length - 1, Key.Up => index < 0 ? enabled.Length - 1 : (index - 1 + enabled.Length) % enabled.Length, _ => (index + 1) % enabled.Length };
        enabled[index].Focus(); enabled[index].BringIntoView(); e.Handled = true;
    }
}
