using System.Text;
using System.Windows;
using System.Windows.Documents;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    // Every visual piece of a wrapped formula belongs to one Markdown object.
    private static Inline? SourceObject(DependencyObject? element, out string source)
    {
        switch (element)
        {
            case Span { Tag: ChatMath.Token token } formula:
                source = "$" + token.Source + "$";
                return formula;
            case InlineUIContainer { Tag: string text } embedded:
                source = text;
                return embedded;
            default:
                source = "";
                return null;
        }
    }

    private static (TextPointer Start, TextPointer End) FormulaSelection(TextPointer start, TextPointer end)
    {
        if (start.CompareTo(end) == 0) return (start, end);
        return (Boundary(start, true), Boundary(end, false));

        static TextPointer Boundary(TextPointer pointer, bool startOfRange)
        {
            for (var inline = pointer.Parent as Inline; inline is not null; inline = inline.Parent as Inline)
            {
                if (inline is not Span { Tag: ChatMath.Token } group) continue;
                if (pointer.CompareTo(group.ContentStart) == 0) return group.ElementStart;
                if (pointer.CompareTo(group.ContentEnd) == 0) return group.ElementEnd;
                return startOfRange ? group.ElementStart : group.ElementEnd;
            }
            return pointer;
        }
    }

    internal List<AiSelectionPart> SelectedReferenceParts()
    {
        var view = SelectionView(); var selection = FormulaSelection(view.Selection.Start, view.Selection.End);
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
                if (SourceObject(p.GetAdjacentElement(LogicalDirection.Forward), out var source) is { } inline)
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
        if (view.Selection.IsEmpty) return default;
        var selection = FormulaSelection(view.Selection.Start, view.Selection.End);
        string? selected = null;
        for (var pointer = selection.Start; pointer is not null && pointer.CompareTo(selection.End) < 0; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart ||
                SourceObject(pointer.GetAdjacentElement(LogicalDirection.Forward), out var tag) is not { } element) continue;
            selected ??= view.CopySource(selection.Start, selection.End).Trim();
            if (selected != tag.Trim()) continue;
            if (view._clipboardMath.TryGetValue(element, out var token)) return ("formula", token.Source, token.Number);
            if (element is InlineUIContainer { Child: MarkdownPicture }) return ("image", tag.StartsWith("[图片] ") ? tag[5..].Trim() : "", null);
        }
        return default;
    }
}
