using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>One shared draft in the composer and at its original document position.</summary>
internal sealed class AiAnnotationLayer : Adorner, IDisposable
{
    private readonly FrameworkElement _host;
    private readonly Func<IReadOnlyList<AiEntry>> _entries;
    private readonly Func<AiEntry, Point?> _anchor;
    private readonly Action<AiEntry> _remove;
    private readonly bool _margin;
    private readonly Func<AiEntry, Task>? _send;
    private readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Relative };
    private List<(AiEntry Entry, Rect Bounds)> _positions = [];
    private AiEntry? _editing;
    private AiEntry? _pendingEdit;
    private bool _refreshQueued, _disposed;
    private Rect _lastClip = Rect.Empty;
    private Size _lastSize;
    private int _lastDocumentVersion = -1;
    internal AiAnnotationLayer(FrameworkElement host, Func<IReadOnlyList<AiEntry>> entries, Func<AiEntry, Point?> anchor, Action<AiEntry> remove, bool margin = false, Func<AiEntry, Task>? send = null) : base(host)
    {
        _host = host; _entries = entries; _anchor = anchor; _remove = remove; _margin = margin; _send = send;
        _popup.PlacementTarget = host;
        host.Loaded += HostLoaded;
        host.Unloaded += HostUnloaded;
        host.LayoutUpdated += HostLayout;
        host.IsVisibleChanged += HostVisible;
        if (host.IsLoaded) Attach();
    }
    private void HostLoaded(object sender, RoutedEventArgs e) => Attach();
    private void HostUnloaded(object sender, RoutedEventArgs e) { _pendingEdit = null; _popup.IsOpen = false; AdornerLayer.GetAdornerLayer(_host)?.Remove(this); }
    private void HostVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        Visibility = _host.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!_host.IsVisible) ClearPositions(); else Refresh();
    }
    private void ClearPositions()
    {
        _popup.IsOpen = false;
        if (_positions.Count > 0) { _positions.Clear(); InvalidateVisual(); }
        _lastClip = Rect.Empty;
    }
    private void HostLayout(object? sender, EventArgs e)
    {
        // LayoutUpdated is global in WPF. An idle/empty annotation layer must not
        // walk ancestors or force FlowDocument character layout on unrelated input.
        if (_disposed || _refreshQueued || !_host.IsVisible || _entries().Count == 0) return;
        _refreshQueued = true;
        _host.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        { _refreshQueued = false; if (!_disposed) Refresh(false); }));
    }
    public void Dispose()
    {
        _disposed = true; _popup.IsOpen = false; _host.Loaded -= HostLoaded; _host.Unloaded -= HostUnloaded; _host.LayoutUpdated -= HostLayout; _host.IsVisibleChanged -= HostVisible;
        AdornerLayer.GetAdornerLayer(_host)?.Remove(this);
    }
    private void Attach()
    {
        var layer = AdornerLayer.GetAdornerLayer(_host);
        if (layer is not null && !(layer.GetAdorners(_host)?.Contains(this) ?? false)) layer.Add(this);
        Refresh();
    }
    internal void Refresh() => Refresh(true);
    private void Refresh(bool force)
    {
        if (!_host.IsLoaded || !_host.IsVisible) { ClearPositions(); return; }
        if (_entries().Count == 0) { ClearPositions(); return; }
        var visible = new Rect(_host.RenderSize);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(_host); parent is Visual; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollContentPresenter clip)
                visible.Intersect(clip.TransformToDescendant(_host).TransformBounds(new Rect(clip.RenderSize)));
        var documentVersion = (_host as MarkdownView)?.LayoutVersion ?? -1;
        // Source/PDF positions also depend on their inner viewport, zoom and text
        // edits, even when the outer control's size and clip have not changed.
        if (!force && _host is MarkdownView && documentVersion == _lastDocumentVersion && visible == _lastClip && _host.RenderSize == _lastSize && _pendingEdit is null) return;
        _lastDocumentVersion = documentVersion;
        _lastClip = visible; _lastSize = _host.RenderSize;
        if (visible.IsEmpty) { ClearPositions(); return; }
        var positions = new List<(AiEntry, Rect)>();
        foreach (var entry in _entries().Where(e => e.State != "已完成" && e.State != "提交中"))
        {
            var point = _anchor(entry);
            if (visible.IsEmpty || point is not { } p || p.X < visible.Left || p.X > visible.Right || p.Y < visible.Top - 24 || p.Y >= visible.Bottom) continue;
            var x = _margin ? visible.Right - 30 : p.X + 4;
            var rect = new Rect(Math.Clamp(x, visible.Left, Math.Max(visible.Left, visible.Right - 30)), Math.Max(p.Y, visible.Top), 28, 24);
            while (positions.Any(x => x.Item2.IntersectsWith(rect)))
            {
                if (_margin && rect.Left - 30 >= visible.Left) rect.Offset(-30, 0);
                else if (!_margin && rect.Right + 30 <= visible.Right) rect.Offset(30, 0);
                else { rect.X = Math.Clamp(x, visible.Left, Math.Max(visible.Left, visible.Right - 30)); rect.Offset(0, 26); }
            }
            positions.Add((entry, rect));
        }
        if (!_positions.SequenceEqual(positions)) { _positions = positions; InvalidateVisual(); }
        if (_pendingEdit is { } pending && _positions.Any(x => x.Entry == pending))
        {
            _pendingEdit = null;
            _host.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => OpenEditor(pending)));
        }
        if (_popup.IsOpen && _editing is { } editing)
        {
            var position = _positions.FirstOrDefault(x => x.Entry == editing);
            if (position.Entry is null) _popup.IsOpen = false;
            else { _popup.HorizontalOffset = position.Bounds.X; _popup.VerticalOffset = position.Bounds.Bottom + 4; }
        }
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (!_host.IsVisible || _lastClip.IsEmpty) return;
        drawing.PushClip(new RectangleGeometry(_lastClip));
        var background = (Brush?)TryFindResource("ChatFocus") ?? Brushes.SteelBlue;
        foreach (var (entry, bounds) in _positions)
        {
            drawing.DrawRoundedRectangle(background, null, bounds, 10, 10);
            var text = new FormattedText(entry.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(bounds.X + (bounds.Width - text.Width) / 2, bounds.Y + 3));
        }
        drawing.Pop();
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        _host.IsVisible && _positions.Any(x => x.Bounds.Contains(parameters.HitPoint)) ? new PointHitTestResult(this, parameters.HitPoint) : null;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var hit = _positions.FirstOrDefault(x => x.Bounds.Contains(e.GetPosition(this)));
        if (hit.Entry is null) return;
        e.Handled = true; Edit(hit.Entry);
    }
    internal void Edit(AiEntry entry)
    {
        _pendingEdit = entry;
        _host.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { _host.UpdateLayout(); Refresh(); }));
    }
    private void OpenEditor(AiEntry entry)
    {
        Refresh();
        var position = _positions.FirstOrDefault(x => x.Entry == entry);
        if (position.Entry is null) return;
        _popup.IsOpen = false; _editing = entry;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = entry.DisplayTitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Tag = entry, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 200, AcceptsTab = true, FontFamily = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(AiEntry.Question)) { Source = entry, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        System.Windows.Automation.AutomationProperties.SetName(input, "原位批注内容");
        input.PreviewKeyDown += async (_, e) =>
        {
            if (!AiPanel.IsSendGesture(e.Key, Keyboard.Modifiers)) return;
            e.Handled = true;
            if (!e.IsRepeat && _send is not null) { await _send(entry); if (!_entries().Any(x => x.Id == entry.Id)) _popup.IsOpen = false; }
        };
        var inputGrid = new Grid(); inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) }); inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var numbers = new TextBlock { Text = "1", FontFamily = input.FontFamily, FontSize = input.FontSize, Margin = new Thickness(3, 4, 0, 0) }; numbers.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        input.TextChanged += (_, _) => numbers.Text = string.Join("\n", Enumerable.Range(1, input.Text.Count(c => c == '\n') + 1)); Grid.SetColumn(input, 1); inputGrid.Children.Add(numbers); inputGrid.Children.Add(input); stack.Children.Add(inputGrid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var remove = new Button { Content = "删除", Margin = new Thickness(0, 0, 8, 0) };
        remove.Click += (_, _) => { _popup.IsOpen = false; _remove(entry); };
        var done = new Button { Content = "收起 · 自动保存" };
        done.Click += (_, _) => _popup.IsOpen = false;
        if (_send is not null)
        {
            var send = new Button { Content = "↑", ToolTip = "发送此条 · Ctrl+Enter", Margin = new Thickness(0, 0, 8, 0) };
            send.Click += async (_, _) => { await _send(entry); if (!_entries().Any(x => x.Id == entry.Id)) _popup.IsOpen = false; };
            actions.Children.Add(send);
        }
        actions.Children.Add(remove); actions.Children.Add(done); stack.Children.Add(actions);
        var surface = new Border { Child = stack, Width = Math.Clamp(_host.ActualWidth - 24, 220, 340), Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); surface.SetResourceReference(Border.BorderBrushProperty, "Accent"); surface.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        surface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; } };
        _popup.Child = surface; _popup.HorizontalOffset = position.Bounds.X; _popup.VerticalOffset = position.Bounds.Bottom + 4;
        _popup.IsOpen = true;
        input.Dispatcher.BeginInvoke(new Action(() => { if (_popup.IsOpen) { input.Focus(); input.CaretIndex = input.Text.Length; } }));
    }
}
