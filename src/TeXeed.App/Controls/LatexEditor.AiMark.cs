using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace TeXeed.App.Controls;

public sealed record SourceAiMark(int Offset, int Length, bool IsPoint);

public sealed partial class LatexEditor
{
    private bool _aiMarking;
    private int? _aiMarkStart;
    private Point _aiMarkPointer;
    private long _aiMarkStartedAt;
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
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            if (!_aiMarking || e.OriginalSource is not DependencyObject source || !IsInsideTextView(source)) return;
            e.Cursor = Cursors.Help;
            e.Handled = true;
        }), true);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_aiMarking || CurrentFilePath is null || Document is null
                || e.OriginalSource is not DependencyObject source || !IsInsideTextView(source)) return;
            _aiMarkPointer = e.GetPosition(TextArea.TextView);
            _aiMarkStart = HitAiMark(_aiMarkPointer);
            _aiMarkStartedAt = Stopwatch.GetTimestamp();
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
            var held = Stopwatch.GetElapsedTime(_aiMarkStartedAt) >= TimeSpan.FromMilliseconds(350);
            var dragged = (point - _aiMarkPointer).Length >= 4;
            _aiMarkStart = null; TextArea.ReleaseMouseCapture();
            var mark = CreateAiMark(start, end, held || dragged);
            if (mark.Length > 0) Select(mark.Offset, mark.Length);
            else { Select(mark.Offset, 0); CaretOffset = mark.Offset; }
            AiMarked?.Invoke(this, mark);
            e.Handled = true;
        };
        TextArea.LostMouseCapture += (_, _) => _aiMarkStart = null;
    }

    private int HitAiMark(Point viewportPoint) => HitMultiCaret(viewportPoint + TextArea.TextView.ScrollOffset).Start;

    internal SourceAiMark CreateAiMark(int start, int end, bool selectSentence)
    {
        start = Math.Clamp(start, 0, Document.TextLength);
        end = Math.Clamp(end, 0, Document.TextLength);
        if (!selectSentence) return new(start, 0, true);
        var text = Document.Text;
        var first = Math.Min(start, end);
        var last = Math.Max(start, end);
        static bool Boundary(char c) => c is '。' or '！' or '？' or '.' or '!' or '?' or '\n' or '\r';
        while (first > 0 && !Boundary(text[first - 1])) first--;
        while (last < text.Length && !Boundary(text[last])) last++;
        if (last < text.Length && text[last] is not ('\n' or '\r')) last++;
        return new(first, last - first, false);
    }
}
