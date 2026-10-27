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
    private int[] _starts = [0];
    private bool _queued;
    public int MinimumLineCount { get; set; } = 1;
    internal int LogicalLineCount => _starts.Length;
    internal IReadOnlyList<int> PaintedNumbers { get; private set; } = [];
    public TextBoxLineNumbers() { IsHitTestVisible = false; Focusable = false; ClipToBounds = true; Padding = new Thickness(4, 0, 4, 0); }
    internal void Attach(TextBox target)
    {
        _target = target;
        target.TextChanged += (_, _) => Index();
        target.SizeChanged += (_, _) => QueuePaint();
        target.Loaded += (_, _) => { AttachScroll(); QueuePaint(); };
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
    private void Index()
    {
        var text = _target!.Text; var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; starts.Add(i + 1); }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        _starts = starts.ToArray(); InvalidateMeasure(); QueuePaint();
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
        var firstPoint = _viewport.TranslatePoint(new Point(.5, .5), _target);
        var lastPoint = _viewport.TranslatePoint(new Point(Math.Max(.5, _viewport.ActualWidth - .5), Math.Max(.5, _viewport.ActualHeight - .5)), _target);
        var offset = _target.GetCharacterIndexFromPoint(firstPoint, true);
        var end = _target.GetCharacterIndexFromPoint(lastPoint, true);
        if (offset < 0 || end < offset) return;
        var index = Array.BinarySearch(_starts, offset); if (index < 0) index = ~index - 1;
        var painted = new List<int>();
        for (; index < _starts.Length && _starts[index] <= end; index++)
        {
            var rect = _target.GetRectFromCharacterIndex(_starts[index]);
            if (rect.IsEmpty || rect.Bottom <= 0 || rect.Top >= ActualHeight) continue;
            var number = Number(index + 1);
            dc.DrawText(number, new Point(ActualWidth - Padding.Right - number.WidthIncludingTrailingWhitespace, rect.Top + (rect.Height - number.Height) / 2));
            painted.Add(index + 1);
        }
        if (_starts.Length < MinimumLineCount)
        {
            var last = _target.GetRectFromCharacterIndex(_target.Text.Length);
            var lineHeight = TextBlock.GetLineHeight(_target);
            if (!last.IsEmpty && !double.IsNaN(lineHeight))
                for (var number = _starts.Length + 1; number <= MinimumLineCount; number++)
                {
                    var top = last.Top + (number - _starts.Length) * lineHeight;
                    if (top >= ActualHeight || top + last.Height <= 0) continue;
                    var label = Number(number);
                    dc.DrawText(label, new Point(ActualWidth - Padding.Right - label.WidthIncludingTrailingWhitespace, top + (last.Height - label.Height) / 2));
                    painted.Add(number);
                }
        }
        PaintedNumbers = painted;
    }
}
