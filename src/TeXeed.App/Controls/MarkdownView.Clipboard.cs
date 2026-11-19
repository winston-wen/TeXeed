using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private (MarkdownView View, Paragraph Paragraph)? _contextParagraph;

    internal void PrepareContextSelection(Point point)
    {
        var selected = SelectionView();
        if (selected.Selection.IsEmpty) return;
        var target = this;
        foreach (var table in _tableViews)
        {
            var origin = table.Scroll.TranslatePoint(new Point(), this);
            if (new Rect(origin, table.Scroll.RenderSize).Contains(point)) { target = table.View; break; }
        }
        var local = ReferenceEquals(target, this) ? point : TranslatePoint(point, target);
        var hit = target.GetPositionFromPoint(local, true);
        if (ReferenceEquals(selected, target) && hit is not null &&
            selected.Selection.Start.CompareTo(hit) <= 0 && selected.Selection.End.CompareTo(hit) >= 0) return;
        selected.Selection.Select(selected.Selection.Start, selected.Selection.Start);
    }

    internal void CaptureContextParagraph(Point point)
    {
        var target = this;
        foreach (var table in _tableViews)
        {
            var origin = table.Scroll.TranslatePoint(new Point(), this);
            if (new Rect(origin, table.Scroll.RenderSize).Contains(point)) { target = table.View; break; }
        }
        var local = ReferenceEquals(target, this) ? point : TranslatePoint(point, target);
        var paragraph = target.GetPositionFromPoint(local, true)?.Paragraph;
        _contextParagraph = paragraph is null ? null : (target, paragraph);
    }

    internal bool HasContextParagraph => _contextParagraph is { } context &&
        context.Paragraph.ContentStart.IsInSameDocument(context.View.Document.ContentStart);

    internal void CopyContextParagraph()
    {
        if (HasContextParagraph && _contextParagraph is { } context)
            context.View.CopyToClipboard(context.Paragraph.ContentStart, context.Paragraph.ContentEnd);
    }

    internal DataObject ClipboardData(TextPointer start, TextPointer end) =>
        new(DataFormats.UnicodeText, CopyForClipboard(start, end));

    internal void CopyToClipboard(TextPointer start, TextPointer end)
    {
        var copying = new DataObjectCopyingEventArgs(ClipboardData(start, end), false);
        RaiseEvent(copying);
        if (!copying.CommandCancelled) Clipboard.SetDataObject(copying.DataObject, true);
    }

    // Clipboard whitespace is independent of the canonical text offsets stored in existing annotations.
    internal string CopyForClipboard(TextPointer start, TextPointer end)
    {
        var output = new StringBuilder();
        var afterInline = false;
        void Text(string text)
        {
            if (afterInline) text = text.TrimStart(' ', '\t');
            if (text.Length == 0) return;
            output.Append(text.ReplaceLineEndings("\n")); afterInline = false;
        }
        void TrimSpaces()
        {
            while (output.Length > 0 && output[^1] is ' ' or '\t') output.Length--;
        }
        var cursor = start;
        // A drag often starts at the first Run's content rather than the
        // surrounding Hyperlink boundary. Preserve any selection after it.
        var expanded = false;
        for (var parent = start.Parent as TextElement; parent is not null; parent = parent.Parent as TextElement)
            if (parent is Hyperlink { Tag: SnapshotCopy snapshot } selected && CoversSnapshot(selected, start, end))
            { Text(snapshot.Text); cursor = selected.ElementEnd.CompareTo(end) <= 0 ? selected.ElementEnd : end; expanded = true; break; }
        if (!expanded)
            for (var parent = start.Parent as TextElement; parent is not null; parent = parent.Parent as TextElement)
                if (parent is Run { Tag: SnapshotFormulaCopy formula } selected && CoversElement(selected, start, end))
                { Text(formula.Text); cursor = selected.ElementEnd.CompareTo(end) <= 0 ? selected.ElementEnd : end; break; }
        for (var p = cursor; p is not null && p.CompareTo(end) < 0;)
        {
            if (IsDecoration(p, out var paddingEnd)) { p = paddingEnd; continue; }
            if (EmbeddedTable(p) is { } table)
            { Text(table.View.CopyForClipboard(table.View.Document.ContentStart, table.View.Document.ContentEnd)); p = table.Block.ElementEnd; continue; }
            var context = p.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is Hyperlink { Tag: SnapshotCopy snapshot } link && CoversSnapshot(link, start, end))
            { Text(snapshot.Text); p = link.ElementEnd.CompareTo(end) <= 0 ? link.ElementEnd : end; continue; }
            if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is Run { Tag: SnapshotFormulaCopy formula } run && CoversElement(run, start, end))
            { Text(formula.Text); p = run.ElementEnd.CompareTo(end) <= 0 ? run.ElementEnd : end; continue; }
            var inline = context == TextPointerContext.ElementStart
                ? p.GetAdjacentElement(LogicalDirection.Forward) as InlineUIContainer : p.Parent as InlineUIContainer;
            if (inline is not null && _clipboardMath.TryGetValue(inline, out var token))
            {
                var tex = token.CopyTex.ReplaceLineEndings("\n").Trim();
                TrimSpaces();
                if (token.Display)
                {
                    if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
                    output.Append("$$\n").Append(tex).Append("$$\n");
                    afterInline = false;
                }
                else
                {
                    output.Append(" $").Append(tex).Append("$ ");
                    afterInline = true;
                }
                p = inline.ElementEnd; continue;
            }
            if (inline is { Tag: string source }) { Text(source); p = inline.ElementEnd; continue; }
            if (context == TextPointerContext.Text)
            {
                var text = p.GetTextInRun(LogicalDirection.Forward);
                var length = Math.Min(text.Length, p.GetOffsetToPosition(end));
                Text(text[..length]); p = p.GetPositionAtOffset(length); continue;
            }
            if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) Text("\n");
            if (context == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
            {
                if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
                afterInline = false;
            }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return output.ToString();
    }

    private bool CoversSnapshot(Hyperlink link, TextPointer start, TextPointer end) => CoversElement(link, start, end);

    private bool CoversElement(TextElement link, TextPointer start, TextPointer end)
    {
        if (start.CompareTo(link.ContentEnd) > 0 || end.CompareTo(link.ContentStart) < 0) return false;
        if (start.CompareTo(link.ContentStart) > 0 && CopySource(link.ContentStart, start).Length != 0) return false;
        return end.CompareTo(link.ContentEnd) >= 0 || CopySource(end, link.ContentEnd).Length == 0;
    }
}
