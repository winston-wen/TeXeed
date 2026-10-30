using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly double CodePadding = Math.Max(0, (25 - 14 * CodeFont.LineSpacing) / 2);
    private const double InlineCodeRadius = 4, BlockCodeRadius = 8;
    private static readonly object CodePaddingMarker = new();
    private static readonly object CodeSpanMarker = new();
    private static readonly DependencyProperty CodeSurfaceBrushProperty = DependencyProperty.Register(
        "CodeSurfaceBrush", typeof(Brush), typeof(MarkdownView), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    private sealed record InlineCodeSurface(Span Span, Run Content);
    private readonly List<InlineCodeSurface> _inlineCodeSurfaces = [];
    private readonly List<(Paragraph Paragraph, RectangleGeometry Shape)> _blockCodeSurfaces = [];
    private readonly List<Rect> _inlineCodeBounds = [];
    private bool _codeLayoutDirty;

    private void InitializeCodeSurfaces()
    {
        SetResourceReference(CodeSurfaceBrushProperty, "PanelBackground");
        TextChanged += (_, _) => _codeLayoutDirty = true;
        SizeChanged += (_, _) => _codeLayoutDirty = true;
        LayoutUpdated += (_, _) =>
        {
            if (!_codeLayoutDirty || !IsVisible) return;
            if (_inlineCodeSurfaces.Count == 0 && _blockCodeSurfaces.Count == 0) { _codeLayoutDirty = false; return; }
            if (!Document.ContentStart.HasValidLayout) return;
            _codeLayoutDirty = false; UpdateCodeSurfaces(); InvalidateVisual();
        };
    }
    private void ClearCodeSurfaces()
    {
        _inlineCodeSurfaces.Clear(); _blockCodeSurfaces.Clear(); _inlineCodeBounds.Clear(); _codeLayoutDirty = true; InvalidateVisual();
    }
    private Paragraph CodeParagraph(string source)
    {
        var paragraph = new Paragraph(new Run(source)) { FontFamily = CodeFont, FontSize = 14, LineHeight = 23,
            Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 14) };
        var shape = new RectangleGeometry(new Rect(0, 0, 400, 100), BlockCodeRadius, BlockCodeRadius);
        var drawing = new GeometryDrawing { Geometry = shape };
        BindingOperations.SetBinding(drawing, GeometryDrawing.BrushProperty, new Binding { Source = this, Path = new PropertyPath(CodeSurfaceBrushProperty) });
        // A native Paragraph background retains text selection and follows its real
        // column/list/table bounds. Only the rounded brush geometry needs sizing.
        paragraph.Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        _blockCodeSurfaces.Add((paragraph, shape));
        return paragraph;
    }
    private Span CodeSpan(string source)
    {
        var text = new Run(source);
        var span = new Span { FontFamily = CodeFont, FontSize = 14, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal, Tag = CodeSpanMarker };
        // An en-space is half an em. Equal padding fills the existing 25-DIP line
        // without turning code into an embedded control or changing copied text.
        Run PaddingRun() => new("\u2002") { FontFamily = new FontFamily("Arial"), FontSize = CodePadding * 2, Tag = CodePaddingMarker };
        if (SourceLink is not null && System.Text.RegularExpressions.Regex.IsMatch(source, @"^[\w./\\ -]+\.(?:tex|md)(?::\d+(?:[–—-]\d+)?)?$"))
        {
            // Keep the code glyph inside the link text context for font-height caret metrics.
            var link = new Hyperlink(); link.TextDecorations = null; text.TextDecorations = TextDecorations.Underline;
            link.Inlines.Add(PaddingRun()); link.Inlines.Add(text); link.Inlines.Add(PaddingRun());
            link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            link.Click += (_, e) => { SourceLink?.Invoke(source); e.Handled = true; }; span.Inlines.Add(link);
        }
        else { span.Inlines.Add(PaddingRun()); span.Inlines.Add(text); span.Inlines.Add(PaddingRun()); }
        _inlineCodeSurfaces.Add(new(span, text));
        return span;
    }
    private static bool IsCodePadding(TextPointer pointer, out TextPointer end)
    {
        var run = pointer.Parent as Run ?? (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
            ? pointer.GetAdjacentElement(LogicalDirection.Forward) as Run : null);
        if (run is not null && ReferenceEquals(run.Tag, CodePaddingMarker)) { end = run.ElementEnd; return true; }
        end = pointer; return false;
    }
    private TextPointer? LegacyAnnotationPointer(int offset)
    {
        if (offset < 0) return null;
        foreach (var (pointer, length) in LegacySymbolRanges())
        {
            if (offset < length) return pointer.GetPositionAtOffset(offset);
            offset -= length;
        }
        return offset == 0 ? Document.ContentEnd : null;
    }
    private IEnumerable<(TextPointer Pointer, int Length)> LegacySymbolRanges()
    {
        // Project decorative spans and nested table viewports out of the old symbol stream.
        for (var pointer = Document.ContentStart; pointer is not null;)
        {
            if (IsDecoration(pointer, out var paddingEnd)) { pointer = paddingEnd; continue; }
            if (EmbeddedTable(pointer) is { } table)
            {
                foreach (var range in table.View.LegacySymbolRanges()) yield return range;
                pointer = table.Block.ElementEnd; continue;
            }
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context is TextPointerContext.ElementStart or TextPointerContext.ElementEnd
                && pointer.GetAdjacentElement(LogicalDirection.Forward) is Span span && ReferenceEquals(span.Tag, CodeSpanMarker))
            { pointer = context == TextPointerContext.ElementStart ? span.ContentStart : span.ElementEnd; continue; }
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null) yield break;
            yield return (pointer, pointer.GetOffsetToPosition(next));
            pointer = next;
        }
    }
    private bool InCurrentDocument(TextElement element)
    {
        for (DependencyObject? parent = element; parent is not null; parent = (parent as FrameworkContentElement)?.Parent)
            if (ReferenceEquals(parent, Document)) return true;
        return false;
    }
    private void UpdateCodeSurfaces()
    {
        _inlineCodeBounds.Clear();
        foreach (var (span, content) in _inlineCodeSurfaces)
        {
            if (!InCurrentDocument(span) || !content.ContentStart.HasValidLayout) continue;
            var cursor = content.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            var end = content.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            while (cursor is not null && cursor.CompareTo(end) < 0)
            {
                var next = cursor.GetLineStartPosition(1);
                var finish = next is not null && next.CompareTo(end) < 0 ? next : end;
                var first = cursor.GetCharacterRect(LogicalDirection.Forward);
                var last = finish.GetCharacterRect(LogicalDirection.Backward);
                if (!first.IsEmpty && !last.IsEmpty)
                {
                    var left = Math.Min(first.X, last.X); var right = Math.Max(first.Right, last.Right);
                    _inlineCodeBounds.Add(new Rect(left - CodePadding, first.Y + (first.Height - 25) / 2, right - left + 2 * CodePadding, 25));
                }
                if (next is null || next.CompareTo(cursor) <= 0 || next.CompareTo(end) >= 0) break;
                cursor = next;
            }
        }
        foreach (var (paragraph, shape) in _blockCodeSurfaces)
        {
            if (!InCurrentDocument(paragraph) || !paragraph.ContentStart.HasValidLayout) continue;
            var first = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var last = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (first.IsEmpty || last.IsEmpty) continue;
            shape.Rect = new Rect(0, 0, Math.Max(1, ActualWidth - Padding.Right - first.X + paragraph.Padding.Left),
                Math.Max(1, last.Bottom - first.Top + paragraph.Padding.Top + paragraph.Padding.Bottom));
        }
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var brush = (Brush)GetValue(CodeSurfaceBrushProperty);
        foreach (var bounds in _inlineCodeBounds) drawingContext.DrawRoundedRectangle(brush, null, bounds, InlineCodeRadius, InlineCodeRadius);
    }
}
