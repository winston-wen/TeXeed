using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
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
        for (var p = start; p is not null && p.CompareTo(end) < 0;)
        {
            if (IsDecoration(p, out var paddingEnd)) { p = paddingEnd; continue; }
            if (EmbeddedTable(p) is { } table)
            { Text(table.View.CopyForClipboard(table.View.Document.ContentStart, table.View.Document.ContentEnd)); p = table.Block.ElementEnd; continue; }
            var context = p.GetPointerContext(LogicalDirection.Forward);
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
}
