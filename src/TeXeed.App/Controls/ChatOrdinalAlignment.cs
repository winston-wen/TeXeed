using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Align the ordinal with first-line ink without changing the reading layout.</summary>
internal sealed class ChatOrdinalAlignment : IDisposable
{
    private readonly MarkdownView _question;
    private readonly TextBlock _number;
    private readonly TranslateTransform _offset = new();
    private readonly Action _align;
    private bool _queued, _disposed;
    internal ChatOrdinalAlignment(MarkdownView question, TextBlock number)
    {
        _question = question; _number = number; number.RenderTransform = _offset; _align = Align;
        question.Loaded += Loaded; question.SizeChanged += Resized; question.TextChanged += Changed;
        number.Loaded += Loaded; number.SizeChanged += Resized; Schedule();
    }
    private void Loaded(object sender, RoutedEventArgs e) => Schedule();
    private void Resized(object sender, SizeChangedEventArgs e) => Schedule();
    private void Changed(object sender, TextChangedEventArgs e) => Schedule();
    internal void Schedule()
    {
        if (_queued || _disposed) return;
        _queued = true;
        _question.Dispatcher.BeginInvoke(DispatcherPriority.Render, _align);
    }
    private void Align()
    {
        _queued = false;
        if (_disposed || !_question.IsLoaded || _number.Parent is not Visual relative) return;
        var text = Rect.Empty;
        var needsLineBounds = true;
        if (StartsWithText()) text = Ink(_question, Rect.Empty, out needsLineBounds);
        if (needsLineBounds)
        {
            var first = _question.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            if (first is null || !first.HasValidLayout) return;
            text = Ink(_question, first.GetCharacterRect(LogicalDirection.Forward), out _);
        }
        var ordinal = Ink(_number, new Rect(_number.RenderSize), out _);
        if (text.IsEmpty || ordinal.IsEmpty) return;
        text = _question.TransformToAncestor(relative).TransformBounds(text);
        ordinal = _number.TransformToAncestor(relative).TransformBounds(ordinal);
        var delta = text.Top + text.Height / 2 - ordinal.Top - ordinal.Height / 2;
        if (Math.Abs(delta) > .001) _offset.Y += delta;
    }
    private bool StartsWithText()
    {
        if (_question.Document.Blocks.FirstBlock is not Paragraph paragraph) return false;
        var first = paragraph.Inlines.FirstInline;
        while (first is Span span) first = span.Inlines.FirstInline;
        return first is Run run && run.Text.Length > 0 && !char.IsWhiteSpace(run.Text[0]);
    }
    private struct InkScan(Rect line)
    {
        internal Rect Line = line, Bounds = Rect.Empty;
        internal double Baseline = double.NaN;
        internal bool NeedsLineBounds;
    }
    private static Rect Ink(Visual root, Rect line, out bool needsLineBounds)
    {
        var scan = new InkScan(line);
        VisitVisual(root, Matrix.Identity, ref scan);
        needsLineBounds = scan.NeedsLineBounds;
        return scan.Bounds;
    }
    private static void VisitVisual(Visual visual, Matrix toRoot, ref InkScan scan)
    {
        if (scan.NeedsLineBounds) return;
        var bottom = scan.Line.IsEmpty ? scan.Baseline + .5 : scan.Line.Bottom;
        if (toRoot.OffsetY > bottom) return;
        // Layout controls paint backgrounds; WPF text lines and TextBlock paint glyphs.
        if (visual is ContainerVisual or TextBlock && VisualTreeHelper.GetDrawing(visual) is { } drawing)
            VisitDrawing(drawing, toRoot, ref scan);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual) && !scan.NeedsLineBounds; i++)
        {
            if (VisualTreeHelper.GetChild(visual, i) is not Visual child) continue;
            var next = VisualTreeHelper.GetTransform(child)?.Value ?? Matrix.Identity;
            var offset = VisualTreeHelper.GetOffset(child); next.Translate(offset.X, offset.Y); next.Append(toRoot);
            VisitVisual(child, next, ref scan);
        }
    }
    private static void VisitDrawing(Drawing drawing, Matrix transform, ref InkScan scan)
    {
        if (drawing is DrawingGroup group)
        {
            var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
            foreach (var child in group.Children)
            {
                VisitDrawing(child, next, ref scan);
                if (scan.NeedsLineBounds) break;
            }
        }
        else if (drawing is GlyphRunDrawing glyph)
        {
            var run = glyph.GlyphRun; var ink = run.ComputeInkBoundingBox();
            if (ink.IsEmpty || ink.Height == 0) return;
            ink.Offset((Vector)run.BaselineOrigin); ink = Rect.Transform(ink, transform);
            if (!scan.Line.IsEmpty)
            {
                if (ink.Bottom < scan.Line.Top || ink.Top > scan.Line.Bottom) return;
            }
            else
            {
                var baseline = transform.Transform(run.BaselineOrigin).Y;
                // Multiple baselines can mean list markers or raised text. Use the
                // document's exact line bounds only for these ambiguous drawings.
                if (!double.IsNaN(scan.Baseline) && Math.Abs(baseline - scan.Baseline) > .5)
                { scan.NeedsLineBounds = true; return; }
                scan.Baseline = baseline;
            }
            scan.Bounds.Union(ink);
        }
    }
    public void Dispose()
    {
        _disposed = true;
        _question.Loaded -= Loaded; _question.SizeChanged -= Resized; _question.TextChanged -= Changed;
        _number.Loaded -= Loaded; _number.SizeChanged -= Resized;
    }
}
