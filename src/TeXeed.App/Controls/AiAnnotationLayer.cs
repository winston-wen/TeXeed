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
    private readonly Action<AiEntry>? _confirm;
    private readonly Action? _commit;
    private readonly Popup _popup = new() { StaysOpen = true, AllowsTransparency = true, Placement = PlacementMode.Relative };
    private Window? _contextWindow;
    private Rect _editingHostBounds, _editingClip, _editingAnchor;
    private int _editingDocumentVersion;
    private double _editingZoom;
    private List<(AiEntry Entry, Rect Bounds)> _positions = [];
    private AiEntry? _editing;
    private AiEntry? _pendingEdit;
    private bool _refreshQueued, _disposed;
    private Rect _lastClip = Rect.Empty;
    private Size _lastSize;
    private int _lastDocumentVersion = -1;
    internal AiAnnotationLayer(FrameworkElement host, Func<IReadOnlyList<AiEntry>> entries, Func<AiEntry, Point?> anchor, Action<AiEntry> remove, bool margin = false, Action<AiEntry>? confirm = null, Action? commit = null) : base(host)
    {
        _host = host; _entries = entries; _anchor = anchor; _remove = remove; _margin = margin; _confirm = confirm; _commit = commit;
        _popup.PlacementTarget = host;
        _popup.Closed += (_, _) =>
        {
            var hadEditor = _editing is not null;
            _editing = null;
            ObserveContext(false);
            if (hadEditor) _commit?.Invoke();
        };
        host.Loaded += HostLoaded;
        host.Unloaded += HostUnloaded;
        host.LayoutUpdated += HostLayout;
        host.IsVisibleChanged += HostVisible;
        if (host.IsLoaded) Attach();
    }
    internal void CloseEditor() { _pendingEdit = null; _popup.IsOpen = false; }
    private void ConfirmEditor()
    {
        if (_editing is not { } entry) return;
        _confirm?.Invoke(entry);
        CloseEditor();
    }
    internal void HandleEditorKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (!AiPanel.IsSendGesture(e.Key, modifiers)) return;
        e.Handled = true;
        if (!e.IsRepeat) ConfirmEditor();
    }
    private Rect HostBounds() => new(_host.PointToScreen(new Point()), _host.RenderSize);
    private void ObserveContext(bool observe)
    {
        if (_contextWindow is { } previous)
        {
            previous.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ContextScrolled));
            previous.LocationChanged -= ContextChanged;
            previous.SizeChanged -= ContextSizeChanged;
        }
        _host.SizeChanged -= ContextSizeChanged;
        if (_host is LatexEditor oldEditor) oldEditor.TextArea.TextView.ScrollOffsetChanged -= ContextChanged;
        if (_host is PdfPreview oldPreview) oldPreview.ZoomChanged -= ContextZoomChanged;
        _contextWindow = observe ? Window.GetWindow(_host) : null;
        if (!observe) return;
        if (_contextWindow is { } window)
        {
            window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ContextScrolled), true);
            window.LocationChanged += ContextChanged;
            window.SizeChanged += ContextSizeChanged;
        }
        _host.SizeChanged += ContextSizeChanged;
        if (_host is LatexEditor editor) editor.TextArea.TextView.ScrollOffsetChanged += ContextChanged;
        if (_host is PdfPreview preview) preview.ZoomChanged += ContextZoomChanged;
    }
    private void ContextScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not Visual scroll || !(_host.IsAncestorOf(scroll) || scroll.IsAncestorOf(_host))) return;
        if (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) CloseEditor();
    }
    private void ContextChanged(object? sender, EventArgs e) => CloseEditor();
    private void ContextSizeChanged(object sender, SizeChangedEventArgs e) => CloseEditor();
    private void ContextZoomChanged(object? sender, EventArgs e)
    {
        if (_host is PdfPreview preview && preview.Zoom != _editingZoom) CloseEditor();
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
        if (!force && _host is MarkdownView && documentVersion == _lastDocumentVersion && visible == _lastClip && _host.RenderSize == _lastSize && _pendingEdit is null && !_popup.IsOpen) return;
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
            if (position.Entry is null || position.Bounds != _editingAnchor || visible != _editingClip
                || HostBounds() != _editingHostBounds || documentVersion != _editingDocumentVersion) CloseEditor();
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
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = entry.DisplayTitle, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        stack.Children.Add(header);
        var input = new TextBox
        {
            Tag = entry, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 200,
            AcceptsTab = true, FontFamily = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei"), FontSize = 15,
            Padding = new Thickness(8), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        TextBlock.SetLineHeight(input, 20);
        TextBlock.SetLineStackingStrategy(input, LineStackingStrategy.BlockLineHeight);
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(AiEntry.Question)) { Source = entry, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        System.Windows.Automation.AutomationProperties.SetName(input, "原位批注内容");
        input.PreviewKeyDown += (_, e) => HandleEditorKey(e, Keyboard.Modifiers);
        var inputGrid = new Grid(); inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var numbers = new TextBoxLineNumbers();
        numbers.SetResourceReference(Control.ForegroundProperty, "Muted");
        numbers.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
        numbers.SetResourceReference(Control.BorderBrushProperty, "Guide");
        numbers.Attach(input);
        Grid.SetColumn(input, 1); inputGrid.Children.Add(numbers); inputGrid.Children.Add(input);
        var editor = new Border { Child = inputGrid, BorderThickness = new Thickness(1.5) };
        editor.SetResourceReference(Border.BackgroundProperty, "ChatQuestionBackground");
        var editorStyle = new Style(typeof(Border));
        editorStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Guide")));
        var focused = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("ChatFocus")));
        editorStyle.Triggers.Add(focused); editor.Style = editorStyle;
        stack.Children.Add(editor);
        Button Icon(string name, string tooltip, string geometry, int column)
        {
            var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
            var button = new Button { Name = name, Content = path, ToolTip = tooltip, Width = 28, Height = 28, Padding = new Thickness(5), Margin = new Thickness(2, 0, 0, 0), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            Grid.SetColumn(button, column); header.Children.Add(button); return button;
        }
        var remove = Icon("AnnotationDelete", "删除此条批注", "M3,5 H17 M7,5 V2 H13 V5 M5,5 L6,18 H14 L15,5 M8,8 V15 M12,8 V15", 1);
        remove.Click += (_, _) => { _remove(entry); CloseEditor(); };
        var confirm = Icon("AnnotationConfirm", "加入 AI 会话待发区 · Ctrl+Enter", "M8,15 V1 M2,7 L8,1 L14,7", 2);
        confirm.Click += (_, _) => ConfirmEditor();
        var surface = new Border { Child = stack, Width = Math.Clamp(_host.ActualWidth - 24, 220, 340), Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        surface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); surface.SetResourceReference(Border.BorderBrushProperty, "Accent"); surface.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        // The editor stays open through focus changes, mouse release and Escape.
        // Confirmation/view changes keep the shared editable annotation; only the
        // conversation composer's send action freezes a snapshot and requests AI.
        _popup.Child = surface; _popup.HorizontalOffset = position.Bounds.X; _popup.VerticalOffset = position.Bounds.Bottom + 4;
        _editingHostBounds = HostBounds(); _editingClip = _lastClip; _editingAnchor = position.Bounds;
        _editingDocumentVersion = _lastDocumentVersion; _editingZoom = (_host as PdfPreview)?.Zoom ?? 0;
        _popup.IsOpen = true;
        ObserveContext(true);
        input.Dispatcher.BeginInvoke(new Action(() => { if (_popup.IsOpen) { input.Focus(); input.CaretIndex = input.Text.Length; } }));
    }
}
