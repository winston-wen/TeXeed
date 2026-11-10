using System.Windows.Documents;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
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
