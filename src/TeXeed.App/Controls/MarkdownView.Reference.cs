using System.Text;
using System.Windows.Documents;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    internal List<AiSelectionPart> SelectedReferenceParts()
    {
        var view = SelectionView(); var selection = view.Selection;
        var parts = new List<AiSelectionPart>();
        var textBuffer = new StringBuilder();
        void Text(string value) => textBuffer.Append(value);
        void Flush() { if (textBuffer.Length > 0) { parts.Add(new AiSelectionPart("text", textBuffer.ToString())); textBuffer.Clear(); } }
        for (var p = selection.Start; p is not null && p.CompareTo(selection.End) < 0;)
        {
            if (IsDecoration(p, out var paddingEnd)) { p = paddingEnd; continue; }
            if (view.EmbeddedTable(p) is { } table) { Text(table.Text); p = table.Block.ElementEnd; continue; }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart)
            {
                if (p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak line)
                { Text("\n"); p = line.ElementEnd; continue; }
                if (p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Tag: string source } inline)
                {
                    if (view._clipboardMath.TryGetValue(inline, out var formula) && formula.Reference is null)
                    { Flush(); parts.Add(new AiSelectionPart("formula", formula.Source) { Display = formula.Display,
                        CopyTex = formula.CopyTex == formula.Source ? null : formula.CopyTex }); }
                    else Text(source);
                    p = inline.ElementEnd; continue;
                }
            }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = p.GetTextInRun(LogicalDirection.Forward);
                var length = Math.Min(text.Length, p.GetOffsetToPosition(selection.End));
                Text(text[..length]); p = p.GetPositionAtOffset(length); continue;
            }
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph) Text("\n");
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        Flush();
        return parts;
    }

    internal string? SelectedReferenceTableMarkdown()
    {
        var view = SelectionView();
        if (ReferenceEquals(view, this) || view.Selection.IsEmpty ||
            view.Selection.Start.CompareTo(view.Document.ContentStart) != 0 ||
            view.Selection.End.CompareTo(view.Document.ContentEnd) != 0 ||
            view.Document.Blocks.FirstBlock is not Table) return null;
        var selected = _tableViews.FirstOrDefault(table => ReferenceEquals(table.View, view));
        return selected is null ? null : _math.OriginalSlice(selected.SourceStart, selected.SourceEnd);
    }

    internal string? SelectedReferenceImageUri()
    {
        var view = SelectionView();
        var selection = view.Selection;
        for (var pointer = selection.Start; pointer is not null && pointer.CompareTo(selection.End) < 0; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                pointer.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: MarkdownPicture picture } element &&
                element.Tag is string tag && view.CopySource(selection.Start, selection.End).Trim() == tag.Trim())
                return picture.SourceUri.AbsoluteUri;
        return null;
    }
    internal (string? Kind, string? Source, string? Number) SelectedReferenceObject()
    {
        var view = SelectionView();
        var selection = view.Selection;
        if (selection.IsEmpty) return default;
        for (var pointer = selection.Start; pointer is not null && pointer.CompareTo(selection.End) < 0; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart ||
                pointer.GetAdjacentElement(LogicalDirection.Forward) is not InlineUIContainer element ||
                element.Tag is not string tag || view.CopySource(selection.Start, selection.End).Trim() != tag.Trim()) continue;
            if (view._clipboardMath.TryGetValue(element, out var token)) return ("formula", token.Source, token.Number);
            if (element.Child is MarkdownPicture) return ("image", tag.StartsWith("[图片] ") ? tag[5..].Trim() : "", null);
        }
        return default;
    }
}
