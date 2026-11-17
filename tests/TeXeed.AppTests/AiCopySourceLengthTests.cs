using System.Windows.Documents;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void CheckCopySourceLengths(MarkdownView view)
    {
        var start = view.Document.ContentStart; var end = view.Document.ContentEnd;
        var points = new List<TextPointer> { start };
        for (var pointer = start; pointer is not null && pointer.CompareTo(end) < 0;)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var count = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (count > 1 && pointer.GetPositionAtOffset(count / 2) is { } middle) points.Add(middle);
            }
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(pointer) <= 0) break;
            points.Add(next); pointer = next;
        }
        if (points[^1].CompareTo(end) != 0) points.Add(end);
        void Check(TextPointer from, TextPointer to)
        {
            var expected = view.CopySource(from, to).Length;
            var actual = view.CopySourceLength(from, to);
            if (actual != expected) throw new Exception($"Source length differs from copied source: {actual} != {expected}");
        }
        foreach (var point in points) { Check(start, point); Check(point, end); }
        for (var i = 0; i + 1 < points.Count; i++) Check(points[i], points[i + 1]);
    }
}
