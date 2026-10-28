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
    private bool _queued, _disposed;
    internal ChatOrdinalAlignment(MarkdownView question, TextBlock number)
    {
        _question = question; _number = number; number.RenderTransform = _offset;
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
        _question.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _queued = false;
            if (_disposed || !_question.IsLoaded || _number.Parent is not Visual relative) return;
            var first = _question.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            if (first is null || !first.HasValidLayout) return;
            var line = first.GetCharacterRect(LogicalDirection.Forward);
            var text = Ink(_question, relative, line); var ordinal = Ink(_number, relative, Rect.Empty);
            if (!text.IsEmpty && !ordinal.IsEmpty) _offset.Y += text.Top + text.Height / 2 - ordinal.Top - ordinal.Height / 2;
        }));
    }
    private static Rect Ink(FrameworkElement element, Visual relative, Rect line)
    {
        var result = Rect.Empty;
        void VisualInk(Visual visual)
        {
            var toElement = visual.TransformToAncestor(element);
            void Visit(Drawing drawing, Matrix transform)
            {
                if (drawing.Bounds.IsEmpty) return;
                var bounds = toElement.TransformBounds(new MatrixTransform(transform).TransformBounds(drawing.Bounds));
                if (!line.IsEmpty && (bounds.Bottom < line.Top || bounds.Top > line.Bottom)) return;
                if (drawing is DrawingGroup group)
                {
                    var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
                    foreach (var child in group.Children) Visit(child, next);
                }
                else if (drawing is GlyphRunDrawing glyph)
                {
                    var ink = glyph.GlyphRun.ComputeInkBoundingBox(); if (ink.IsEmpty || ink.Height == 0) return;
                    ink.Offset((Vector)glyph.GlyphRun.BaselineOrigin);
                    result.Union(visual.TransformToAncestor(relative).TransformBounds(new MatrixTransform(transform).TransformBounds(ink)));
                }
            }
            if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Visit(drawing, Matrix.Identity);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                if (VisualTreeHelper.GetChild(visual, i) is Visual child) VisualInk(child);
        }
        VisualInk(element); return result;
    }
    public void Dispose()
    {
        _disposed = true;
        _question.Loaded -= Loaded; _question.SizeChanged -= Resized; _question.TextChanged -= Changed;
        _number.Loaded -= Loaded; _number.SizeChanged -= Resized;
    }
}
