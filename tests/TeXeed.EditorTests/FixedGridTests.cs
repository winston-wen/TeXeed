using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void RunFixedGridTests()
    {
        Run("arbitrary fonts preserve their face and align to fixed half-width cells", FixedGridTypography);
        Run("grid navigation and deletion preserve Unicode graphemes and tab stops", FixedGridUnicodeAndNavigation);
        Run("wrapped text and selection geometry use the same grid", FixedGridWrappingAndSelection);
        Run("reused glyphs preserve live text colors and do not freeze caller brushes", FixedGridLiveColors);
    }

    private static void FixedGridLiveColors()
    {
        const string source = "Wi中文Wi中文";
        var foreground = new SolidColorBrush(Colors.Red);
        var editor = new LatexEditor { FontFamily = new FontFamily("Arial"), FontSize = 22 };
        editor.ShowFile(File("grid-colors.tex", source), source);
        editor.SyntaxHighlighting = null;
        editor.Foreground = foreground;
        var window = HostEditor(editor);
        try
        {
            (int Red, int Blue) CountInk()
            {
                var view = editor.TextArea.TextView;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var red = 0; var blue = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 2] > pixels[i] + 80 && pixels[i + 2] > pixels[i + 1] + 80) red++;
                    if (pixels[i] > pixels[i + 2] + 80 && pixels[i] > pixels[i + 1] + 80) blue++;
                }
                return (red, blue);
            }
            Require(CountInk().Red > 50, "Initial glyph drawings are empty or lost their text color.");
            Require(!foreground.IsFrozen, "Drawing glyphs froze the caller's mutable text brush.");
            foreground.Color = Colors.Blue;
            editor.TextArea.TextView.Redraw(); SettleLayout(window);
            var changed = CountInk();
            Require(changed.Red == 0 && changed.Blue > 50, "Reused glyph drawings retained a stale foreground color.");
            foreground.Color = Colors.Red;
            editor.TextArea.TextView.Redraw(); SettleLayout(window);
            var restored = CountInk();
            Require(restored.Blue == 0 && restored.Red > 50, "Restoring a previous text color reused an incorrect glyph drawing.");
        }
        finally { window.Close(); }
    }

    private static void FixedGridTypography()
    {
        const string source = "iWm.中文ＡéαΩ🙂e\u0301👩‍💻🇨🇳1\uFE0F\u20E3";
        int[] expectedWidths = [1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 2, 1, 2, 2, 2];
        foreach (var face in new[] { "Arial", "Times New Roman", "Microsoft YaHei UI" })
        {
            var editor = new LatexEditor { FontFamily = new FontFamily(face), FontSize = 20 };
            editor.ShowFile(File("grid.tex", source), source);
            var window = HostEditor(editor);
            try
            {
                VerifyGridPositions(editor, expectedWidths);
                var visual = editor.TextArea.TextView.VisualLines[0];
                Require(visual.Elements.All(element => element.TextRunProperties.Typeface.FontFamily.Source == face),
                    "Grid typography replaced the selected proportional font with another face.");
                editor.FontSize = 30;
                SettleLayout(window);
                VerifyGridPositions(editor, expectedWidths);
                editor.FontFamily = new FontFamily("Times New Roman");
                SettleLayout(window);
                VerifyGridPositions(editor, expectedWidths);
                Require(editor.Text == source && !editor.Document.UndoStack.CanUndo,
                    "Changing fixed-grid typography modified or normalized the source text.");
            }
            finally { window.Close(); }
        }
    }

    private static void VerifyGridPositions(LatexEditor editor, int[] widths)
    {
        var view = editor.TextArea.TextView;
        var source = editor.Document.GetText(editor.Document.GetLineByNumber(1));
        var starts = StringInfo.ParseCombiningCharacters(source);
        Require(starts.Length == widths.Length, "The Unicode fixture did not retain the expected grapheme clusters.");
        var column = 0;
        for (var i = 0; i < starts.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : source.Length;
            var position = view.GetVisualPosition(new TextViewPosition(1, starts[i] + 1), VisualYPosition.LineMiddle);
            var next = view.GetVisualPosition(new TextViewPosition(1, end + 1), VisualYPosition.LineMiddle);
            Require(Math.Abs(position.X - column * editor.GridCellWidth) < .05
                    && Math.Abs(next.X - position.X - widths[i] * editor.GridCellWidth) < .05,
                $"Glyph {source[starts[i]..end]} did not occupy exactly {widths[i]} cells in {editor.FontFamily.Source}.");
            var leftHit = view.GetPosition(new Point(position.X + widths[i] * editor.GridCellWidth * .2, position.Y));
            var rightHit = view.GetPosition(new Point(position.X + widths[i] * editor.GridCellWidth * .8, position.Y));
            Require(leftHit?.Column == starts[i] + 1 && rightHit?.Column == end + 1,
                "Mouse hit testing diverged from grid boundaries or split a grapheme cluster.");
            Require(editor.GetGridColumn(starts[i]) == column && editor.GetOffsetAtGridColumn(1, column) == starts[i],
                "Grid/source column conversion did not preserve grapheme boundaries.");
            column += widths[i];
        }
        Require(editor.GetGridColumn(source.Length) == column, "End-of-line grid width is incorrect.");
    }

    private static void FixedGridUnicodeAndNavigation()
    {
        const string source = "a\u0301中🙂👩‍💻🇨🇳Z\nhello world!\ni\t中\tW\néαΩ·ｶＡ";
        var editor = new LatexEditor { FontFamily = new FontFamily("Arial"), FontSize = 20 };
        editor.Options.IndentationSize = 4;
        editor.ShowFile(File("grid-unicode.tex", source), source);
        var window = HostEditor(editor);
        try
        {
            var view = editor.TextArea.TextView;
            var first = view.GetVisualLine(1);
            var starts = StringInfo.ParseCombiningCharacters(editor.Document.GetText(editor.Document.GetLineByNumber(1)));
            var current = 0;
            foreach (var end in starts.Skip(1).Append(editor.Document.GetLineByNumber(1).Length))
            {
                current = first.GetNextCaretPosition(current, LogicalDirection.Forward, CaretPositioningMode.Normal, false);
                Require(first.GetRelativeOffset(current) == end, "Arrow navigation stopped inside a combining sequence or emoji.");
            }
            var words = view.GetVisualLine(2);
            Require(words.GetNextCaretPosition(0, LogicalDirection.Forward, CaretPositioningMode.WordStart, false) == 6,
                "Word navigation degraded to individual fixed-grid objects.");
            Require(words.GetNextCaretPosition(8, LogicalDirection.Backward, CaretPositioningMode.WordStart, false) == 6,
                "Backward word navigation no longer finds the original word boundary.");
            var tabLine = editor.Document.GetLineByNumber(3);
            int[] offsets = [0, 1, 2, 3, 4, 5];
            int[] columns = [0, 1, 4, 6, 8, 9];
            for (var i = 0; i < offsets.Length; i++)
            {
                var point = view.GetVisualPosition(new TextViewPosition(3, offsets[i] + 1), VisualYPosition.LineMiddle);
                Require(Math.Abs(point.X - columns[i] * editor.GridCellWidth) < .05
                    && editor.GetGridColumn(tabLine.Offset + offsets[i]) == columns[i],
                    "Tab stops did not align to the fixed-width indentation grid.");
            }
            var ambiguous = editor.Document.GetLineByNumber(4);
            Require(editor.GetGridColumn(ambiguous.EndOffset) == 7,
                "Accents, Greek, ambiguous-width or half-width kana were incorrectly treated as wide characters.");
            foreach (var cluster in new[] { "e\u0301", "🙂", "👩‍💻", "🇨🇳" })
            {
                editor.ShowFile(File("grid-delete.tex", cluster + "X"), cluster + "X");
                SettleLayout(window);
                editor.CaretOffset = cluster.Length;
                EditingCommands.Backspace.Execute(null, editor.TextArea);
                Require(editor.Text == "X", "Backspace split or could not remove a complete Unicode grapheme.");
                Require(editor.Undo() && editor.Text == cluster + "X", "Deleting a grapheme failed to undo cleanly.");
                editor.Select(0, 0);
                editor.CaretOffset = 0;
                EditingCommands.Delete.Execute(null, editor.TextArea);
                Require(editor.Text == "X", "Delete split a complete Unicode grapheme.");
            }
        }
        finally { window.Close(); }
    }

    private static void FixedGridWrappingAndSelection()
    {
        var source = string.Concat(Enumerable.Repeat("Wi中🙂e\u0301", 40));
        var editor = new LatexEditor { FontFamily = new FontFamily("Times New Roman"), FontSize = 20, WordWrap = true };
        editor.ShowFile(File("grid-wrap.tex", source), source);
        var window = HostEditor(editor, width: 260, height: 400);
        try
        {
            var view = editor.TextArea.TextView;
            var visual = view.VisualLines[0];
            Require(visual.TextLines.Count > 2, "The fixed-grid paragraph did not wrap.");
            var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToHashSet();
            var column = 0;
            foreach (var line in visual.TextLines)
            {
                var start = visual.GetRelativeOffset(column);
                column += line.Length;
                var end = visual.GetRelativeOffset(Math.Min(column, visual.VisualLength));
                Require(boundaries.Contains(start) && boundaries.Contains(end), "Wrapping split a Unicode grapheme.");
                Require(Math.Abs(line.WidthIncludingTrailingWhitespace / editor.GridCellWidth
                    - Math.Round(line.WidthIncludingTrailingWhitespace / editor.GridCellWidth)) < .01,
                    "A wrapped line drifted away from whole grid cells.");
            }
            editor.WordWrap = false;
            SettleLayout(window);
            // Select W + i + a two-cell Chinese character; geometry must use four cells.
            editor.Select(0, 3);
            SettleLayout(window);
            var rectangles = BackgroundGeometryBuilder.GetRectsForSegment(view, editor.TextArea.Selection.SurroundingSegment).ToArray();
            Require(rectangles.Length == 1 && Math.Abs(rectangles[0].Width - 4 * editor.GridCellWidth) < .05,
                "Selection geometry does not match the text grid.");
            Require(editor.SelectedText == "Wi中" && editor.Text == source, "Grid selection changed source content.");
        }
        finally { window.Close(); }
    }
}
