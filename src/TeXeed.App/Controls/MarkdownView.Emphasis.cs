using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    internal static readonly object StrongEmphasisMarker = new();
    private readonly List<Inline> _strongRuns = [];
    private DrawingGroup? _waves;
    private bool _waveLayoutQueued;
    private readonly record struct WaveFontKey(string Family, FontStyle Style, FontWeight Weight, FontStretch Stretch, double Size, double Dpi);
    private static readonly Dictionary<WaveFontKey, (double Height, double InkBottom)> WaveMetrics = [];
    private static readonly Queue<WaveFontKey> WaveMetricOrder = [];
    private const int WaveCacheLimit = 256;
    private static readonly double[] WaveSamples = [0, Math.Sqrt(.5), 1, Math.Sqrt(.5), 0, -Math.Sqrt(.5), -1, -Math.Sqrt(.5)];
    private sealed class WaveStroke(Brush brush, double size)
    {
        internal readonly StreamGeometry Geometry = new();
        internal readonly Pen Pen = new(brush, Math.Max(.7, size / 18));
        internal StreamGeometryContext Path = null!;
    }

    private static void ApplyWavyEmphasis(Span span) => span.Tag = StrongEmphasisMarker;

    private void RegisterStrongRuns(Span span)
    {
        void Add(InlineCollection inlines)
        {
            foreach (var inline in inlines)
                if (inline is Run run && run.Text.Length > 0 && !ReferenceEquals(run.Tag, CodePaddingMarker)) _strongRuns.Add(run);
                else if (inline is InlineUIContainer ui && ui.Tag is string) _strongRuns.Add(ui);
                else if (inline is Span child && !ReferenceEquals(child.Tag, StrongEmphasisMarker))
                {
                    if (child is Hyperlink link) link.TextDecorations = null;
                    Add(child.Inlines);
                }
        }
        Add(span.Inlines);
    }

    private static bool ContainsStrongEmphasis(InlineCollection inlines) => inlines.OfType<Span>().Any(span =>
        ReferenceEquals(span.Tag, StrongEmphasisMarker) || ContainsStrongEmphasis(span.Inlines));

    private void QueueWaveLayout()
    {
        if (_waveLayoutQueued || _strongRuns.Count == 0 || !IsVisible) return;
        _waveLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _waveLayoutQueued = false;
            if (!IsVisible || _strongRuns.Count == 0) return;
            LayoutWaves();
        }));
    }

    private void LayoutWaves()
    {
        var drawing = new DrawingGroup();
        var strokes = new Dictionary<(Brush Brush, double Size), WaveStroke>();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var run in _strongRuns)
        {
            if (!InCurrentDocument(run) || !run.ContentStart.HasValidLayout) continue;
            var start = run is InlineUIContainer ? run.ElementStart : run.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var end = run is InlineUIContainer ? run.ElementEnd : run.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            if (start is null || end is null) continue;
            var size = run.FontSize;
            var metrics = GetWaveMetrics(run, size, dpi);
            var strokeKey = (run.Foreground, size);
            if (!strokes.TryGetValue(strokeKey, out var stroke))
            {
                stroke = new WaveStroke(run.Foreground, size); stroke.Path = stroke.Geometry.Open();
                strokes.Add(strokeKey, stroke);
            }
            var firstRect = start.GetCharacterRect(LogicalDirection.Forward);
            var lastRect = end.GetCharacterRect(LogicalDirection.Backward);
            if (!firstRect.IsEmpty && !lastRect.IsEmpty && Math.Abs(firstRect.Top - lastRect.Top) < .5)
            {
                AddLine(firstRect, lastRect);
                continue;
            }
            while (start.CompareTo(end) < 0)
            {
                var next = start.GetLineStartPosition(1);
                var stop = next is not null && next.CompareTo(start) > 0 && next.CompareTo(end) < 0 ? next : end;
                var first = start.GetCharacterRect(LogicalDirection.Forward);
                var last = stop.GetCharacterRect(LogicalDirection.Backward);
                AddLine(first, last);
                if (stop.CompareTo(end) >= 0) break;
                start = stop;
            }
            void AddLine(Rect first, Rect last)
            {
                if (!first.IsEmpty && !last.IsEmpty && last.Right > first.Left)
                {
                    var amplitude = size / 16;
                    var inkBottom = run is InlineUIContainer ? first.Bottom :
                        first.Top + Math.Max(0, (first.Height - metrics.Height) / 2) + metrics.InkBottom;
                    var center = inkBottom + size / 8 + amplitude;
                    AppendWave(stroke.Path, first.Left, last.Right, center, size);
                }
            }
        }
        using (var context = drawing.Open())
            foreach (var stroke in strokes.Values)
            {
                stroke.Path.Close(); stroke.Geometry.Freeze();
                context.DrawGeometry(null, stroke.Pen, stroke.Geometry);
            }
        _waves = drawing;
        InvalidateVisual();
    }

    private static (double Height, double InkBottom) GetWaveMetrics(Inline run, double size, double dpi)
    {
        var key = new WaveFontKey(run.FontFamily.Source, run.FontStyle, run.FontWeight, run.FontStretch, size, dpi);
        if (WaveMetrics.TryGetValue(key, out var metrics)) return metrics;
        var formatted = new FormattedText("国Agjpqy", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch), size, Brushes.Black, dpi);
        metrics = (formatted.Height, formatted.Height + formatted.OverhangAfter);
        if (WaveMetrics.Count >= WaveCacheLimit) WaveMetrics.Remove(WaveMetricOrder.Dequeue());
        WaveMetrics.Add(key, metrics); WaveMetricOrder.Enqueue(key);
        return metrics;
    }

    private static void AppendWave(StreamGeometryContext path, double left, double right, double center, double size)
    {
        var amplitude = size / 16; var period = size / 2; var step = period / 8;
        path.BeginFigure(new Point(left, center), false, false);
        var i = 1;
        for (var x = left + step; x < right; x += step, i++)
            path.LineTo(new Point(x, center + amplitude * WaveSamples[i & 7]), true, false);
        path.LineTo(new Point(right, center + amplitude * Math.Sin((right - left) * 2 * Math.PI / period)), true, false);
    }

    private void DrawWaves(DrawingContext drawingContext)
    { if (_waves is not null) drawingContext.DrawDrawing(_waves); }
}
