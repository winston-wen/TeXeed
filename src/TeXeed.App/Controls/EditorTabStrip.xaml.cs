using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shape = System.Windows.Shapes.Shape;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;

public sealed record EditorTab(string Path, string Title, bool Modified);
public enum TabCloseScope { This, All, Others, Left, Right }
public partial class EditorTabStrip : UserControl
{
    private readonly Dictionary<string, Border> _views = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<EditorTab> _tabs = [];
    private string? _active, _dragPath;
    private Point _dragStart;
    private bool _dragging;
    private double _dragX;
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    public event Action<string>? Selected;
    public event Action<string, TabCloseScope>? CloseRequested;
    public event Action<string, int>? ReorderRequested;
    public double HorizontalOffset => Scroller.HorizontalOffset;
    public double ScrollableWidth => Scroller.ScrollableWidth;

    public EditorTabStrip()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateOverflow();
        Loaded += (_, _) => { ThemeManager.Changed += ThemeChanged; UpdateChrome(); };
        Unloaded += (_, _) => { ThemeManager.Changed -= ThemeChanged; EndDrag(); };
        PreviewMouseMove += Drag_Move;
        PreviewMouseLeftButtonUp += Drag_Up;
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            if (!_dragging) return;
            e.Cursor = DragCursors.Grabbing; e.Handled = true;
        }), true);
        LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) EndDrag(); };
        _dragScroll.Tick += (_, _) =>
        {
            if (!_dragging || Mouse.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
            if (_dragX < 30 || _dragX > ActualWidth - 30) DragTo(_dragX);
        };
    }
    private void ThemeChanged(object? sender, EventArgs e) => UpdateChrome();
    public void SetTabs(IReadOnlyList<EditorTab> tabs, string? active)
    {
        var reveal = !string.Equals(_active, active, StringComparison.OrdinalIgnoreCase);
        _tabs = tabs; _active = active;
        foreach (var path in _views.Keys.Where(path => !tabs.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))).ToArray())
        { Items.Children.Remove(_views[path]); _views.Remove(path); }
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            if (!_views.TryGetValue(tab.Path, out var view)) { view = CreateTab(tab.Path); _views.Add(tab.Path, view); }
            var label = (TextBlock)((Button)((Grid)view.Child).Children[0]).Content;
            var text = tab.Title + (tab.Modified ? "  ●" : "");
            if (label.Text != text && string.Equals(tab.Path, active, StringComparison.OrdinalIgnoreCase)) reveal = true;
            label.Text = text;
            view.ToolTip = tab.Path + (tab.Modified ? "\n尚未保存" : "");
            var previous = Items.Children.IndexOf(view);
            if (previous != i) { if (previous >= 0) Items.Children.Remove(view); Items.Children.Insert(i, view); }
        }
        UpdateChrome();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { UpdateOverflow(); if (reveal && active is not null) Reveal(active); }));
    }
    private Border CreateTab(string path)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var select = new Button { Style = (Style)Resources["TabTitleButton"], Padding = new Thickness(11, 0, 7, 0), Content = new TextBlock { VerticalAlignment = VerticalAlignment.Center }, Tag = path };
        select.Click += (_, _) => Selected?.Invoke(path);
        var close = new Button { Style = (Style)Resources["TabButton"], Width = 20, Height = 20, Margin = new Thickness(0, 0, 7, 0), ToolTip = "关闭标签 (Ctrl+F4)", Focusable = false };
        AutomationProperties.SetName(close, "关闭 " + System.IO.Path.GetFileName(path));
        close.Content = new Path { Width = 12, Height = 12, Data = Geometry.Parse("M1,1 L11,11 M11,1 L1,11"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        ((Path)close.Content).SetResourceReference(Shape.StrokeProperty, "Ink");
        close.Click += (_, e) => { e.Handled = true; CloseRequested?.Invoke(path, TabCloseScope.This); };
        grid.Children.Add(select); Grid.SetColumn(close, 1); grid.Children.Add(close);
        var view = new Border { Child = grid, Background = Brushes.Transparent, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), Margin = new Thickness(3, 4, 3, 4), Tag = path };
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInside(e.OriginalSource as DependencyObject, close)) return;
            _dragPath = path; _dragStart = e.GetPosition(this); _dragging = false;
        };
        view.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; ShowTabMenu(view, path); };
        return view;
    }
    private static bool IsInside(DependencyObject? target, DependencyObject parent)
    {
        while (target is not null) { if (ReferenceEquals(target, parent)) return true; target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target); }
        return false;
    }
    private void UpdateChrome()
    {
        foreach (var (path, view) in _views)
        {
            var selected = string.Equals(path, _active, StringComparison.OrdinalIgnoreCase);
            view.BorderBrush = selected ? new LinearGradientBrush(ThemeManager.Brush("SyntaxCommand").Color, ThemeManager.Brush("Accent").Color, 0) : Brushes.Transparent;
        }
        UpdateOverflow();
    }
    private void ShowTabMenu(Border view, string path)
    {
        var position = _tabs.ToList().FindIndex(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        var menu = new ContextMenu { PlacementTarget = view, Placement = PlacementMode.MousePoint, Padding = new Thickness(4), BorderThickness = new Thickness(1) };
        menu.SetResourceReference(Control.BackgroundProperty, "EditorBackground"); menu.SetResourceReference(Control.ForegroundProperty, "Ink"); menu.SetResourceReference(Control.BorderBrushProperty, "Guide");
        foreach (var (text, scope, enabled) in new[] { ("全部关闭", TabCloseScope.All, true), ("关闭其他标签", TabCloseScope.Others, _tabs.Count > 1), ("关闭左侧标签", TabCloseScope.Left, position > 0), ("关闭右侧标签", TabCloseScope.Right, position < _tabs.Count - 1) })
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled, Tag = scope };
            item.Click += (_, _) => CloseRequested?.Invoke(path, scope); menu.Items.Add(item);
        }
        view.ContextMenu = menu; menu.IsOpen = true;
    }
    public void ScrollBy(double amount) => Scroller.ScrollToHorizontalOffset(Math.Clamp(Scroller.HorizontalOffset + amount, 0, Scroller.ScrollableWidth));
    private void Tabs_Wheel(object sender, MouseWheelEventArgs e) { if (Scroller.ScrollableWidth <= .5) return; e.Handled = true; ScrollBy(-e.Delta); }
    private void Left_Click(object sender, RoutedEventArgs e) => ScrollBy(-Math.Max(80, ActualWidth * .65));
    private void Right_Click(object sender, RoutedEventArgs e) => ScrollBy(Math.Max(80, ActualWidth * .65));
    private void Tabs_Scrolled(object sender, ScrollChangedEventArgs e) => UpdateOverflow();
    private void UpdateOverflow()
    {
        var left = Scroller.HorizontalOffset > .5; var right = Scroller.ScrollableWidth - Scroller.HorizontalOffset > .5;
        LeftButton.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        RightButton.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        var fade = Math.Min(.45, 38 / Math.Max(1, Scroller.ActualWidth));
        Scroller.OpacityMask = !left && !right ? null : new LinearGradientBrush(new GradientStopCollection
        {
            new(left ? Colors.Transparent : Colors.Black, 0), new(Colors.Black, left ? fade : 0),
            new(Colors.Black, right ? 1 - fade : 1), new(right ? Colors.Transparent : Colors.Black, 1)
        }, new Point(0, .5), new Point(1, .5));
    }
    private void Reveal(string path)
    {
        if (!_views.TryGetValue(path, out var view)) return;
        UpdateLayout();
        var left = view.TranslatePoint(new Point(), Scroller).X;
        var right = left + view.ActualWidth;
        if (left < (LeftButton.IsVisible ? 30 : 0)) ScrollBy(left - 32);
        else if (right > Scroller.ViewportWidth - (RightButton.IsVisible ? 30 : 0)) ScrollBy(right - Scroller.ViewportWidth + 32);
    }
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_dragPath is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!BeginDrag()) return;
        }
        e.Handled = true;
        _dragX = point.X;
        DragTo(point.X);
    }
    private bool BeginDrag()
    {
        _dragging = CaptureMouse();
        if (!_dragging) return false;
        Mouse.UpdateCursor();
        _dragScroll.Start();
        return true;
    }
    private void DragTo(double x)
    {
        if (_dragPath is null) return;
        if (x < 30) ScrollBy(-24); else if (x > ActualWidth - 30) ScrollBy(24);
        var slot = _tabs.Count;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var view = _views[_tabs[i].Path];
            if (x < view.TranslatePoint(new Point(view.ActualWidth / 2, 0), this).X) { slot = i; break; }
        }
        var from = _tabs.ToList().FindIndex(t => string.Equals(t.Path, _dragPath, StringComparison.OrdinalIgnoreCase));
        var target = Math.Clamp(slot > from ? slot - 1 : slot, 0, Math.Max(0, _tabs.Count - 1));
        if (target >= 0 && target != from) ReorderRequested?.Invoke(_dragPath, target);
    }
    private void Drag_Up(object sender, MouseButtonEventArgs e) { if (_dragging) e.Handled = true; EndDrag(); }
    private void EndDrag()
    {
        _dragScroll.Stop(); _dragPath = null;
        var wasDragging = _dragging; _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging) Mouse.UpdateCursor();
    }
}
