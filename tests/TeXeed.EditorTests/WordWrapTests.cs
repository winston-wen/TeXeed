using System.Windows.Controls;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static void WordWrapToggle()
    {
        var editor = new LatexEditor();
        var content = string.Join('\n', Enumerable.Range(1, 80).Select(n => $"行 {n} " + string.Concat(Enumerable.Repeat("中文 abc 😀e\u0301 ", 40))));
        var file = File("wrap.tex", content);
        editor.ShowFile(file, content);
        editor.Document.Insert(editor.Document.TextLength, "!");
        var window = HostEditor(editor, width: 420);
        try
        {
            editor.NavigateTo(new SourceLocation(file.Path, 25, 15), center: true);
            editor.Select(editor.CaretOffset, 8);
            SettleLayout(window);
            var document = editor.Document;
            var caret = editor.CaretOffset;
            var start = editor.SelectionStart;
            var anchor = Anchor(editor);
            var edits = 0;
            editor.ContentEdited += (_, _) => edits++;
            editor.SetWordWrap(true); SettleLayout(window);
            Require(editor.WordWrap && editor.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                && editor.TextArea.TextView.VisualLines.Any(l => l.TextLines.Count > 1), "Enabling wrap did not reflow a long line.");
            Require(editor.HorizontalOffset < 1 && editor.TextArea.TextView.VisualLines.All(l => l.TextLines.All(t => t.WidthIncludingTrailingWhitespace <= editor.TextArea.TextView.ActualWidth + 1)),
                "Wrapped text overflows the viewport or retains horizontal scrolling.");
            Require(Anchor(editor).Line == anchor.Line, "Word-wrap toggle jumped away from the visible source line.");
            var rows = editor.TextArea.TextView.GetVisualLine(25)!.TextLines.Count;
            window.Width = 720; SettleLayout(window);
            Require(editor.TextArea.TextView.GetVisualLine(25)!.TextLines.Count < rows, "Wrapped rows did not reflow after resizing.");
            editor.SetWordWrap(false); SettleLayout(window);
            Require(!editor.WordWrap && editor.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
                && editor.TextArea.TextView.VisualLines.All(l => l.TextLines.Count == 1), "Disabling wrap did not restore long source lines.");
            Require(ReferenceEquals(editor.Document, document) && editor.Text == content + "!"
                && editor.CaretOffset == caret && editor.SelectionStart == start && editor.SelectionLength == 8
                && editor.Document.LineCount == 80 && editor.Document.UndoStack.CanUndo && edits == 0,
                "Reflow changed the document, source line numbers, selection, caret or undo history.");
            editor.Undo();
            Require(editor.Text == content, "Undo after reflow failed to undo the preceding edit.");
            editor.SetWordWrap(true);
            var other = File("other.tex", content);
            editor.ShowFile(other, content); SettleLayout(window);
            Require(editor.WordWrap && editor.TextArea.TextView.VisualLines.Any(l => l.TextLines.Count > 1), "Switching files lost the wrap setting.");
        }
        finally { window.Close(); }
    }
}
