using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Paint the marker in the existing reading gutter without affecting text flow or input.
internal sealed class AiBookmarkMarkerLayer : Adorner, IDisposable
{
    private static readonly Geometry Icon = Geometry.Parse("M 1,1 L 9,1 9,13 5,10 1,13 Z");
    private static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        "MarkerBrush", typeof(Brush), typeof(AiBookmarkMarkerLayer),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((AiBookmarkMarkerLayer)owner)._drawing = null));
    private readonly MarkdownView _view;
    private readonly bool _question;
    private IReadOnlyList<AiUserBookmark> _bookmarks = [];
    private List<Point> _positions = [];
    private Size _size;
    private int _version = -1;
    private bool _queued, _disposed, _dirty = true;
    private DrawingGroup? _drawing;

    internal AiBookmarkMarkerLayer(MarkdownView view, bool question) : base(view)
    {
        _view = view; _question = question; IsHitTestVisible = false;
        SetResourceReference(MarkerBrushProperty, "ChatHeading");
        view.LayoutUpdated += LayoutChanged;
        view.IsVisibleChanged += VisibilityChanged;
        AdornerLayer.GetAdornerLayer(view)?.Add(this);
    }

    internal IReadOnlyList<Point> Positions => _positions;
    internal void SetBookmarks(IReadOnlyList<AiUserBookmark> bookmarks)
    {
        _bookmarks = bookmarks; _dirty = true; Schedule();
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { _dirty = true; Schedule(); }
    private void LayoutChanged(object? sender, EventArgs e)
    {
        if (_dirty || _version != _view.LayoutVersion || _size != _view.RenderSize) Schedule();
    }
    private void Schedule()
    {
        if (_disposed || _queued || !_view.IsVisible) return;
        _queued = true;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _queued = false; if (!_disposed) Refresh(); }));
    }
    private void Refresh()
    {
        if (!_view.IsLoaded || !_view.IsVisible) return;
        if (!_dirty && _version == _view.LayoutVersion && _size == _view.RenderSize) return;
        _dirty = false; _version = _view.LayoutVersion; _size = _view.RenderSize;
        var positions = new List<Point>();
        foreach (var bookmark in _bookmarks)
        {
            if (_view.BookmarkMarkerPoint(bookmark) is not { } p) continue;
            // The first question line shares its gutter with the turn ordinal.
            // Keep a full-size icon below that ordinal when both are present.
            var firstQuestionLine = _question && p.Y < 20;
            var position = new Point(firstQuestionLine ? -32 : -16, p.Y + (firstQuestionLine ? 16 : 2));
            positions.Add(position);
        }
        positions.Sort((a, b) => a.Y.CompareTo(b.Y));
        for (var i = positions.Count - 1; i > 0; i--)
            if (positions[i].Y - positions[i - 1].Y < 8) positions.RemoveAt(i);
        if (!_positions.SequenceEqual(positions)) { _positions = positions; _drawing = null; InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_positions.Count == 0 || !_view.IsVisible) return;
        if (_drawing is null)
        {
            _drawing = new DrawingGroup();
            using var cache = _drawing.Open();
            var pen = new Pen((Brush)GetValue(MarkerBrushProperty), 1.25) { LineJoin = PenLineJoin.Round };
            foreach (var position in _positions)
            {
                cache.PushTransform(new TranslateTransform(position.X, position.Y));
                cache.DrawGeometry(null, pen, Icon);
                cache.Pop();
            }
        }
        drawing.DrawDrawing(_drawing);
    }
    public void Dispose()
    {
        _disposed = true; _view.LayoutUpdated -= LayoutChanged; _view.IsVisibleChanged -= VisibilityChanged;
        AdornerLayer.GetAdornerLayer(_view)?.Remove(this);
    }
}
