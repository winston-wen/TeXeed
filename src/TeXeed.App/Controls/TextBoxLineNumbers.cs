using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

// Logical line starts are indexed only when text changes. Paint only the visible
// starts, using TextBox's own layout so wrapping and scrolling stay aligned.
public sealed class TextBoxLineNumbers : Control
{
    private TextBox? _target;
    private ScrollViewer? _scroll;
    private FrameworkElement? _viewport;
    private readonly List<(ScrollViewer Scroll, FrameworkElement Viewport)> _outerViewports = [];
    private int[] _starts = [0];
    private string _indexedText = "";
    private bool _queued;
    public int MinimumLineCount { get; set; } = 1;
    internal bool ReserveOnlyEmptyVisualLines { get; set; }
    internal bool ClipToAncestorViewport { get; set; }
    internal int LogicalLineCount => _starts.Length;
    internal IReadOnlyList<int> LineStarts => _starts;
    internal IReadOnlyList<int> PaintedNumbers { get; private set; } = [];
    public TextBoxLineNumbers() { IsHitTestVisible = false; Focusable = false; ClipToBounds = true; Padding = new Thickness(4, 0, 4, 0); }
    internal void Attach(TextBox target)
    {
        _target = target;
        target.TextChanged += (_, e) => { if (!ClipToAncestorViewport || !UpdateIndex(e)) Index(); };
        target.SizeChanged += (_, _) => QueuePaint();
        target.Loaded += (_, _) => { AttachScroll(); AttachAncestorViewports(); QueuePaint(); };
        target.Unloaded += (_, _) => DetachAncestorViewports();
        Index();
    }
    private void AttachScroll()
    {
        var scroll = _target?.Template?.FindName("PART_ContentHost", _target) as ScrollViewer;
        if (scroll == _scroll) return;
        if (_scroll is not null) _scroll.ScrollChanged -= Scrolled;
        _scroll = scroll;
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += Scrolled;
            _scroll.ApplyTemplate();
            _viewport = _scroll.Template.FindName("PART_ScrollContentPresenter", _scroll) as FrameworkElement;
        }
        else _viewport = null;
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e) => QueuePaint();
    private void AttachAncestorViewports()
    {
        DetachAncestorViewports();
        if (!ClipToAncestorViewport) return;
        for (DependencyObject? current = VisualTreeHelper.GetParent(this); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ScrollViewer scroll || ReferenceEquals(scroll, _scroll)) continue;
            scroll.ApplyTemplate();
            if (scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) is not FrameworkElement viewport) continue;
            _outerViewports.Add((scroll, viewport)); scroll.ScrollChanged += AncestorScrolled; viewport.SizeChanged += AncestorSizeChanged;
        }
    }
    private void DetachAncestorViewports()
    {
        foreach (var (scroll, viewport) in _outerViewports) { scroll.ScrollChanged -= AncestorScrolled; viewport.SizeChanged -= AncestorSizeChanged; }
        _outerViewports.Clear();
    }
    private void AncestorScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)) QueuePaint();
    }
    private void AncestorSizeChanged(object sender, SizeChangedEventArgs e) => QueuePaint();
    private void Index()
    {
        var text = _target!.Text; var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; starts.Add(i + 1); }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        _starts = starts.ToArray(); _indexedText = text; InvalidateMeasure(); QueuePaint();
    }
    private bool UpdateIndex(TextChangedEventArgs e)
    {
        if (e.Changes.Count != 1) return false;
        var change = e.Changes.First(); var before = _indexedText; var after = _target!.Text;
        var offset = change.Offset; var removed = change.RemovedLength; var added = change.AddedLength;
        if (offset < 0 || removed < 0 || added < 0 || offset + removed > before.Length || offset + added > after.Length || before.Length - removed + added != after.Length) return false;
        var left = Math.Max(0, offset - 2);
        if (left > 0 && before[left - 1] == '\r' && before[left] == '\n') left--;
        var oldEnd = Math.Min(before.Length, offset + removed + 2);
        var newEnd = Math.Min(after.Length, offset + added + 2);
        if (oldEnd > 0 && oldEnd < before.Length && before[oldEnd - 1] == '\r' && before[oldEnd] == '\n') oldEnd++;
        if (newEnd > 0 && newEnd < after.Length && after[newEnd - 1] == '\r' && after[newEnd] == '\n') newEnd++;
        var delta = added - removed;
        if (oldEnd + delta != newEnd) return false;
        static int UpperBound(int[] values, int value)
        {
            var lo = 0; var hi = values.Length;
            while (lo < hi) { var middle = lo + (hi - lo) / 2; if (values[middle] <= value) lo = middle + 1; else hi = middle; }
            return lo;
        }
        var prefix = UpperBound(_starts, left); var suffix = UpperBound(_starts, oldEnd);
        var next = new List<int>(_starts.Length + Math.Min(added, 128));
        for (var i = 0; i < prefix; i++) next.Add(_starts[i]);
        for (var i = left; i < newEnd; i++)
        {
            if (after[i] == '\r') { if (i + 1 < newEnd && after[i + 1] == '\n') i++; next.Add(i + 1); }
            else if (after[i] == '\n') next.Add(i + 1);
        }
        for (var i = suffix; i < _starts.Length; i++) next.Add(_starts[i] + delta);
        var oldDigits = _starts.Length.ToString(CultureInfo.InvariantCulture).Length;
        _starts = next.ToArray(); _indexedText = after;
        if (oldDigits != _starts.Length.ToString(CultureInfo.InvariantCulture).Length) InvalidateMeasure();
        QueuePaint(); return true;
    }
    private void QueuePaint()
    {
        if (_queued) return; _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _queued = false; InvalidateVisual(); }));
    }
    private FormattedText Number(int number) => new(number.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface(_target!.FontFamily, _target.FontStyle, _target.FontWeight, _target.FontStretch),
        _target.FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    protected override Size MeasureOverride(Size constraint) => new(_target is null ? 16 : Math.Ceiling(Number(_starts.Length).WidthIncludingTrailingWhitespace) + 8, 0);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Background, null, new Rect(RenderSize));
        dc.DrawRectangle(BorderBrush, null, new Rect(Math.Max(0, ActualWidth - 1), 0, 1, ActualHeight));
        PaintedNumbers = [];
        if (_target is null || !_target.IsVisible || _viewport is null) return;
        if (!_target.IsMeasureValid || !_target.IsArrangeValid || _target.LineCount == 0) { QueuePaint(); return; }
        // GetFirstVisibleLineIndex divides by the font's natural line height,
        // which is incorrect when the editor uses an explicit line height.
        // Hit-test the actual text viewport instead of estimating visual rows.
        var visible = _viewport.TransformToVisual(_target).TransformBounds(new Rect(_viewport.RenderSize));
        if (ClipToAncestorViewport)
        {
            foreach (var (_, viewport) in _outerViewports)
            {
                if (!viewport.IsVisible || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) { visible = Rect.Empty; break; }
                try { visible.Intersect(viewport.TransformToVisual(_target).TransformBounds(new Rect(viewport.RenderSize))); }
                catch (InvalidOperationException) { visible = Rect.Empty; break; }
                if (visible.IsEmpty) break;
            }
        }
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return;
        var firstPoint = new Point(visible.Left + Math.Min(.5, visible.Width / 2), visible.Top + Math.Min(.5, visible.Height / 2));
        var lastPoint = new Point(visible.Right - Math.Min(.5, visible.Width / 2), visible.Bottom - Math.Min(.5, visible.Height / 2));
        var offset = _target.GetCharacterIndexFromPoint(firstPoint, true);
        var end = _target.GetCharacterIndexFromPoint(lastPoint, true);
        if (offset < 0 || end < offset) return;
        var index = Array.BinarySearch(_starts, offset); if (index < 0) index = ~index - 1;
        var painted = new List<int>();
        for (; index < _starts.Length && _starts[index] <= end; index++)
        {
            var rect = _target.GetRectFromCharacterIndex(_starts[index]);
            if (rect.IsEmpty || rect.Bottom <= visible.Top || rect.Top >= visible.Bottom) continue;
            var number = Number(index + 1);
            dc.DrawText(number, new Point(ActualWidth - Padding.Right - number.WidthIncludingTrailingWhitespace, rect.Top + (rect.Height - number.Height) / 2));
            painted.Add(index + 1);
        }
        if (_starts.Length < MinimumLineCount)
        {
            var last = _target.GetRectFromCharacterIndex(_target.Text.Length);
            var lineHeight = TextBlock.GetLineHeight(_target);
            if (!last.IsEmpty && !double.IsNaN(lineHeight))
                for (var number = _starts.Length + 1; number <= (ReserveOnlyEmptyVisualLines ? Math.Min(MinimumLineCount, _starts.Length + Math.Max(0, MinimumLineCount - _target.LineCount)) : MinimumLineCount); number++)
                {
                    var top = last.Top + (number - _starts.Length) * lineHeight;
                    if (top >= visible.Bottom || top + last.Height <= visible.Top) continue;
                    var label = Number(number);
                    dc.DrawText(label, new Point(ActualWidth - Padding.Right - label.WidthIncludingTrailingWhitespace, top + (last.Height - label.Height) / 2));
                    painted.Add(number);
                }
        }
        PaintedNumbers = painted;
    }
}
