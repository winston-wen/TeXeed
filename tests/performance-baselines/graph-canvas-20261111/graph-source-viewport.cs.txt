using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App.AI;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;

// Nodes and directed edges are independent of the present parent-only data model.
internal sealed record AiRelationEdge(string From, string To);
internal sealed record AiRelationRoute(AiRelationEdge Edge, int Lane);
internal sealed record AiRelationVertex(string Id, string Title);
internal sealed record AiRelationPlacedNode(AiRelationVertex Vertex, Rect Bounds);
internal sealed record AiRelationLayout(IReadOnlyList<AiRelationPlacedNode> Nodes, IReadOnlyList<AiRelationEdge> Edges, Size Extent);

internal static class AiRelationGraphLayout
{
    internal static IReadOnlyList<AiRelationRoute> Routes(IReadOnlyList<AiRelationEdge> edges)
    {
        var counts = new Dictionary<(string, string), int>();
        var routes = new List<AiRelationRoute>(edges.Count);
        foreach (var edge in edges)
        {
            var key = string.CompareOrdinal(edge.From, edge.To) <= 0 ? (edge.From, edge.To) : (edge.To, edge.From);
            var lane = counts.GetValueOrDefault(key); counts[key] = lane + 1;
            routes.Add(new(edge, lane));
        }
        return routes;
    }
    internal static AiRelationLayout Build(IReadOnlyList<AiRelationVertex> vertices, IReadOnlyList<AiRelationEdge> edges, string current)
    {
        var byId = new Dictionary<string, AiRelationVertex>();
        foreach (var vertex in vertices) byId.TryAdd(vertex.Id, vertex);
        if (!byId.ContainsKey(current)) return new([], [], new(360, 100));
        var adjacency = byId.Keys.ToDictionary(id => id, _ => new List<string>());
        var validEdges = edges.Where(e => byId.ContainsKey(e.From) && byId.ContainsKey(e.To)).ToArray();
        foreach (var edge in validEdges) { adjacency[edge.From].Add(edge.To); adjacency[edge.To].Add(edge.From); }
        var component = new HashSet<string> { current }; var queue = new Queue<string>(); queue.Enqueue(current);
        while (queue.TryDequeue(out var id)) foreach (var neighbor in adjacency[id]) if (component.Add(neighbor)) queue.Enqueue(neighbor);
        var related = validEdges.Where(e => component.Contains(e.From) && component.Contains(e.To)).ToArray();
        var incoming = related.Select(e => e.To).ToHashSet();
        var start = vertices.FirstOrDefault(v => component.Contains(v.Id) && !incoming.Contains(v.Id))?.Id ?? current;
        var depth = new Dictionary<string, int> { [start] = 0 }; queue.Enqueue(start);
        while (queue.TryDequeue(out var id))
        {
            foreach (var neighbor in adjacency[id]) if (component.Contains(neighbor) && depth.TryAdd(neighbor, depth[id] + 1)) queue.Enqueue(neighbor);
        }
        var order = new List<string>(); var ordered = new HashSet<string>(); var stack = new Stack<string>(); stack.Push(start);
        while (stack.TryPop(out var id))
        {
            if (!ordered.Add(id)) continue;
            order.Add(id);
            var neighbors = adjacency[id];
            for (var i = neighbors.Count - 1; i >= 0; i--) if (component.Contains(neighbors[i]) && !ordered.Contains(neighbors[i])) stack.Push(neighbors[i]);
        }
        const double width = 260, height = 54, gap = 24;
        var widestLane = Routes(related).Select(route => route.Lane).DefaultIfEmpty(0).Max();
        var left = 92 + Math.Max(0, widestLane * 8 - 60);
        var nodes = order.Select((id, i) => new AiRelationPlacedNode(byId[id], new Rect(left + Math.Min(depth[id], 6) * 42, 24 + i * (height + gap), width, height))).ToArray();
        return new(nodes, related, new(nodes.Max(n => n.Bounds.Right) + 32, nodes.Max(n => n.Bounds.Bottom) + 24));
    }
}

internal sealed class AiConversationGraph : FrameworkElement
{
    private readonly AiRelationLayout _layout;
    private readonly Dictionary<string, AiRelationPlacedNode> _byId;
    private readonly IReadOnlyList<AiRelationRoute> _routes;
    private readonly Action<string> _open;
    private readonly string _current;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _scene = new();
    private readonly DrawingVisual _selection = new();
    private ScrollViewer? _scroll;
    private bool _sceneValid;
    private int _selected;

    internal AiConversationGraph(IReadOnlyList<AiConversation> conversations, string current, Action<string> open)
        : this(conversations.Select(c => new AiRelationVertex(c.Id, c.Title)).ToArray(),
            conversations.Where(c => c.ParentConversationId is not null).Select(c => new AiRelationEdge(c.ParentConversationId!, c.Id)).ToArray(), current, open) { }

    internal AiConversationGraph(IReadOnlyList<AiRelationVertex> vertices, IReadOnlyList<AiRelationEdge> edges, string current, Action<string> open)
    {
        _layout = AiRelationGraphLayout.Build(vertices, edges, current); _current = current; _open = open;
        _byId = _layout.Nodes.ToDictionary(n => n.Vertex.Id);
        _routes = AiRelationGraphLayout.Routes(_layout.Edges);
        _selected = Math.Max(0, _layout.Nodes.ToList().FindIndex(n => n.Vertex.Id == current));
        _visuals = new VisualCollection(this) { _scene, _selection };
        Focusable = true; Cursor = Cursors.Arrow;
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += GraphThemeChanged;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll) { _scroll = scroll; _scroll.ScrollChanged += GraphScrollChanged; break; }
            InvalidateScene();
        };
        Unloaded += (_, _) => { ThemeManager.Changed -= GraphThemeChanged; if (_scroll is not null) _scroll.ScrollChanged -= GraphScrollChanged; _scroll = null; };
        AutomationProperties.SetName(this, "会话关系图。使用上下键选择，回车打开会话。");
    }
    internal AiRelationLayout Layout => _layout;
    internal int SceneRevision { get; private set; }
    internal int RenderedNodeCount { get; private set; }
    internal string? FirstRenderedNodeId { get; private set; }
    internal bool ActivateAt(Point point, int clicks)
    {
        for (var i = 0; i < _layout.Nodes.Count; i++) if (_layout.Nodes[i].Bounds.Contains(point))
        {
            _selected = i; Focus(); DrawSelection();
            if (clicks == 2) _open(_layout.Nodes[i].Vertex.Id);
            return true;
        }
        return false;
    }
    protected override Size MeasureOverride(Size availableSize) => _layout.Extent;
    protected override Size ArrangeOverride(Size finalSize) => _layout.Extent;
    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];
    private Brush Theme(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private void GraphThemeChanged(object? sender, EventArgs e) => InvalidateScene();
    private void GraphScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0) InvalidateScene();
    }
    private void InvalidateScene() { _sceneValid = false; InvalidateVisual(); DrawSelection(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); InvalidateScene(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!_sceneValid) DrawScene();
    }
    private void DrawScene()
    {
        using var drawing = _scene.RenderOpen();
        _sceneValid = true; SceneRevision++;
        var line = new Pen(Theme("Muted", Brushes.Gray), 1.1);
        var top = Math.Max(0, (_scroll?.VerticalOffset ?? 0) - 80);
        var viewportHeight = _scroll?.ViewportHeight ?? 0;
        var bottom = top + (viewportHeight > 0 && double.IsFinite(viewportHeight) ? viewportHeight + 160 : 960);
        var vertical = new List<(double X, double Start, double End)>();
        var horizontal = new HashSet<(double Left, double Right, double Y)>();
        void Horizontal(double x1, double x2, double y)
        {
            if (y < top || y > bottom) return;
            var segment = (Math.Min(x1, x2), Math.Max(x1, x2), y);
            if (horizontal.Add(segment)) drawing.DrawLine(line, new Point(x1, y), new Point(x2, y));
        }
        void Arrow(Point point)
        {
            if (point.Y < top || point.Y > bottom) return;
            drawing.DrawLine(line, point, new Point(point.X - 5, point.Y - 3)); drawing.DrawLine(line, point, new Point(point.X - 5, point.Y + 3));
        }
        foreach (var route in _routes)
        {
            var edge = route.Edge;
            var from = _byId[edge.From].Bounds; var to = _byId[edge.To].Bounds;
            var x = Math.Min(from.Left, to.Left) - 18 - route.Lane * 8;
            var offset = Math.Clamp(route.Lane == 0 ? 0 : (route.Lane % 2 == 1 ? 1 : -1) * Math.Ceiling(route.Lane / 2d) * 7, -from.Height / 2 + 8, from.Height / 2 - 8);
            var a = new Point(from.Left, from.Top + from.Height / 2 + offset); var b = new Point(to.Left, to.Top + to.Height / 2 + offset);
            if (edge.From == edge.To)
            {
                a = new Point(from.Left, from.Top + from.Height / 4);
                b = new Point(from.Left, from.Bottom - from.Height / 4);
            }
            if (Math.Max(a.Y, b.Y) < top || Math.Min(a.Y, b.Y) > bottom) continue;
            Horizontal(a.X, x, a.Y); Horizontal(x, b.X, b.Y); Arrow(b);
            vertical.Add((x, Math.Max(top, Math.Min(a.Y, b.Y)), Math.Min(bottom, Math.Max(a.Y, b.Y))));
        }
        vertical.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Start.CompareTo(b.Start));
        for (var i = 0; i < vertical.Count;)
        {
            var segment = vertical[i++];
            while (i < vertical.Count && vertical[i].X == segment.X && vertical[i].Start <= segment.End)
            { segment.End = Math.Max(segment.End, vertical[i].End); i++; }
            drawing.DrawLine(line, new Point(segment.X, segment.Start), new Point(segment.X, segment.End));
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        RenderedNodeCount = 0; FirstRenderedNodeId = null;
        for (var i = 0; i < _layout.Nodes.Count; i++)
        {
            var item = _layout.Nodes[i]; var selected = item.Vertex.Id == _current;
            if (item.Bounds.Bottom < top || item.Bounds.Top > bottom) continue;
            RenderedNodeCount++; FirstRenderedNodeId ??= item.Vertex.Id;
            var border = new Pen(Theme(selected ? "ChatFocus" : "Guide", selected ? Brushes.Goldenrod : Brushes.Gray), selected ? 1.5 : 1);
            drawing.DrawRoundedRectangle(Theme("PanelBackground", Brushes.Black), border, item.Bounds, 5, 5);
            var title = new FormattedText(item.Vertex.Title + (selected ? "（当前）" : ""), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), 15, Theme("Ink", Brushes.White), dpi)
                { MaxTextWidth = item.Bounds.Width - 24, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            drawing.DrawText(title, new Point(item.Bounds.Left + 12, item.Bounds.Top + (item.Bounds.Height - title.Height) / 2));
        }
    }
    private void DrawSelection()
    {
        using var drawing = _selection.RenderOpen();
        if (IsKeyboardFocused && _selected < _layout.Nodes.Count)
            drawing.DrawRoundedRectangle(null, new Pen(Theme("ChatFocus", Brushes.Goldenrod), 2), _layout.Nodes[_selected].Bounds, 5, 5);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); DrawSelection(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); DrawSelection(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var item = _layout.Nodes.FirstOrDefault(n => n.Bounds.Contains(e.GetPosition(this)));
        ToolTip = item?.Vertex.Title; Cursor = item is null ? Cursors.Arrow : Cursors.Hand;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActivateAt(e.GetPosition(this), e.ClickCount)) e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_layout.Nodes.Count == 0) return;
        if (e.Key == Key.Enter) { _open(_layout.Nodes[_selected].Vertex.Id); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up)
        {
            _selected = Math.Clamp(_selected + (e.Key == Key.Down ? 1 : -1), 0, _layout.Nodes.Count - 1);
            BringIntoView(_layout.Nodes[_selected].Bounds); DrawSelection(); e.Handled = true;
        }
    }
}
