using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TeXeed.App.Controls;

public sealed record SourceAiMark(int Offset, int Length, bool IsPoint);

public sealed partial class LatexEditor
{
    private bool _aiMarking;
    private int? _aiMarkStart;
    public event EventHandler<SourceAiMark>? AiMarked;
    public bool IsAiMarking => _aiMarking;

    public void SetAiMarking(bool enabled)
    {
        _aiMarking = enabled;
        _aiMarkStart = null;
        if (TextArea.IsMouseCaptured) TextArea.ReleaseMouseCapture();
        Mouse.UpdateCursor();
    }

    private void InitializeAiMarking()
    {
        IsVisibleChanged += (_, _) => { if (!IsVisible && _aiMarking) SetAiMarking(false); };
        Unloaded += (_, _) => { if (_aiMarking) SetAiMarking(false); };
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            if (!_aiMarking || e.OriginalSource is not DependencyObject source || !IsInsideTextView(source)) return;
            e.Cursor = Cursors.Cross;
            e.Handled = true;
        }), true);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_aiMarking || Keyboard.Modifiers != ModifierKeys.None || CurrentFilePath is null || Document is null
                || e.OriginalSource is not DependencyObject source || !IsInsideTextView(source)) return;
            _aiMarkStart = HitAiMark(e.GetPosition(TextArea.TextView));
            ResetMultiCaret();
            TextArea.Focus(); TextArea.CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_aiMarkStart is not { } start) return;
            var end = HitAiMark(e.GetPosition(TextArea.TextView));
            Select(Math.Min(start, end), Math.Abs(end - start));
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_aiMarkStart is not { } start) return;
            var point = e.GetPosition(TextArea.TextView);
            var end = HitAiMark(point);
            _aiMarkStart = null; TextArea.ReleaseMouseCapture();
            var mark = CreateAiMark(start, end, true);
            if (mark.Length > 0)
            {
                Select(mark.Offset, mark.Length);
                AiMarked?.Invoke(this, mark);
            }
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!_aiMarking || e.Key != Key.Escape) return;
            SetAiMarking(false); e.Handled = true;
        };
        TextArea.LostMouseCapture += (_, _) => _aiMarkStart = null;
    }

    private int HitAiMark(Point viewportPoint) => HitMultiCaret(viewportPoint + TextArea.TextView.ScrollOffset).Start;

    internal bool CaptureAnnotationContext(Point point)
    {
        var segments = TextArea.Selection.Segments.Where(segment => segment.Length > 0).ToArray();
        if (segments.Length == 0 || Document is null) return false;
        var view = TextArea.TextView;
        var withinView = point.X >= 0 && point.Y >= 0 && point.X < view.ActualWidth && point.Y < view.ActualHeight;
        if (withinView)
        {
            view.EnsureVisualLines();
            foreach (var visual in view.VisualLines)
            foreach (var segment in segments)
            {
                var start = Math.Max(segment.StartOffset, visual.FirstDocumentLine.Offset);
                var end = Math.Min(segment.EndOffset, visual.LastDocumentLine.EndOffset);
                if (end <= start) continue;
                if (BackgroundGeometryBuilder.GetRectsForSegment(view, new TextSegment { StartOffset = start, Length = end - start }, false).Any(rect => rect.Contains(point)))
                    return true;
            }
        }
        ResetMultiCaret();
        Select(CaretOffset, 0);
        return false;
    }

    internal SourceAiMark CreateAiMark(int start, int end, bool _)
    {
        start = Math.Clamp(start, 0, Document.TextLength);
        end = Math.Clamp(end, 0, Document.TextLength);
        var first = Math.Min(start, end);
        return new(first, Math.Abs(end - start), false);
    }
}
