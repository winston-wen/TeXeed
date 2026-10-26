using System.Reflection;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void RunMultiCaretTests()
    {
        Run("independent carets type and paste with one undo and no duplicates", MultiTyping);
        Run("multi-caret deletion preserves graphemes and CRLF", MultiUnicode);
        Run("brackets, Enter, Tab and cursor movement apply to every caret", MultiKeys);
        Run("multi-caret edits prevalidate read-only sections", MultiReadOnly);
        Run("multi-caret state survives save and resets across sessions", MultiSessions);
        Run("Alt click and middle/Alt drag use displayed cells and short rows", MultiPointer);
        Run("rectangle selection follows wrapped display rows", MultiWrappedRows);
        Run("virtual carets and selections use cell padding and remain visible", MultiVirtualAndScrolling);
        Run("multi-caret cut is atomic and respects read-only sections", MultiCut);
        Run("WPF middle-button preview routing owns and releases its drag", MultiMouseRouting);
    }

    private static object? MultiCall(LatexEditor editor, string name, params object[] args)
    {
        var method = typeof(LatexEditor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing multi-caret integration method: " + name);
        try { return method.Invoke(editor, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static LatexEditor MultiEditor(string content, int caret, params int[] additional)
    {
        var editor = new LatexEditor();
        editor.ShowFile(File("multi.tex", content), content);
        editor.CaretOffset = caret;
        foreach (var offset in additional) MultiCall(editor, "AddMultiCaret", offset, 0);
        return editor;
    }

    private static void MultiType(LatexEditor editor, string text)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, editor.TextArea, text))
        { RoutedEvent = TextCompositionManager.TextInputEvent };
        MultiCall(editor, "OnTextEntering", editor.TextArea, args);
        Require(args.Handled, "Multi-caret input fell through to the single caret handler.");
    }

    private static void MultiPaste(LatexEditor editor, string text)
    {
        var data = new DataObject(DataFormats.UnicodeText, text);
        var args = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
        editor.TextArea.RaiseEvent(args);
        Require(args.CommandCancelled, "Multi-caret paste was allowed to run a second time in AvalonEdit.");
    }

    private static bool MultiKey(LatexEditor editor, Key key, ModifierKeys modifiers = ModifierKeys.None)
        => (bool)MultiCall(editor, "ProcessMultiCaretKey", key, modifiers)!;

    private static void MultiTyping()
    {
        var editor = MultiEditor("abcd\n012345\nxyz", 1, 9, 14);
        MultiCall(editor, "AddMultiCaret", 9, 0);
        Require(editor.MultiCaretCount == 3, "Clicking an existing position duplicated the caret.");
        var edits = 0;
        editor.ContentEdited += (_, _) => edits++;
        MultiType(editor, "甲😀"); // An IME commit can contain more than one UTF-16 code unit.
        Require(editor.Text == "a甲😀bcd\n0123甲😀45\nxy甲😀z", "Independent columns did not all receive the complete text commit.");
        Require(edits == 1 && editor.MultiCaretCount == 3, "One multi-caret edit emitted multiple notifications or lost a caret.");
        editor.Undo();
        Require(editor.Text == "abcd\n012345\nxyz" && !editor.Document.UndoStack.CanUndo, "Typing at three carets took more than one undo step.");
        MultiCall(editor, "AddMultiCaret", 0, 0);
        MultiCall(editor, "AddMultiCaret", editor.Document.TextLength, 0);
        var count = editor.MultiCaretCount;
        MultiPaste(editor, "\n\\alpha");
        Require(editor.Text.Split("\\alpha", StringSplitOptions.None).Length - 1 == count,
            "Pasted multiline TeX was not inserted intact at every caret.");
        editor.Undo();
        Require(editor.Text == "abcd\n012345\nxyz", "Multi-caret paste was not one undo operation.");
    }

    private static void MultiUnicode()
    {
        const string original = "😀a\r\n e\u0301b\r\n👩‍💻c";
        var editor = MultiEditor(original, 2, original.IndexOf('b'), original.IndexOf('c'));
        MultiKey(editor, Key.Back);
        Require(editor.Text == "a\r\n b\r\nc", "Backspace split a surrogate pair, combining sequence, or emoji sequence.");
        editor.Undo();
        Require(editor.Text == original, "Unicode deletion did not undo as a single operation.");
        editor.CaretOffset = 0;
        MultiCall(editor, "AddMultiCaret", original.IndexOf('e'), 0);
        MultiCall(editor, "AddMultiCaret", original.IndexOf("👩", StringComparison.Ordinal), 0);
        MultiKey(editor, Key.Delete);
        Require(editor.Text == "a\r\n b\r\nc", "Delete split an extended grapheme cluster.");

        editor = MultiEditor("ab\r\ncd\r\nef", 4, 8);
        MultiKey(editor, Key.Back);
        Require(editor.Text == "abcdef", "Deleting line boundaries split CRLF or deleted adjacent letters.");
        editor.Undo();
        Require(editor.Text == "ab\r\ncd\r\nef", "Line-join undo did not restore delimiters.");
    }

    private static void MultiKeys()
    {
        var editor = MultiEditor("a\n% b\nc", 1, 5, 7);
        MultiType(editor, "{");
        Require(editor.Text == "a{}\n% b{\nc{}", "Bracket insertion ignored comments or only paired at the primary caret.");
        MultiType(editor, "}");
        Require(editor.Text == "a{}\n% b{}\nc{}", "Closing delimiters were not skipped independently at each caret.");

        editor = MultiEditor("{}\n[]", 1, 4);
        MultiKey(editor, Key.Back);
        Require(editor.Text == "\n", "Paired backspace did not run at every caret.");
        editor.Undo();
        Require(editor.Text == "{}\n[]", "Paired deletion was not one undo step.");

        editor = MultiEditor("  a\n    b", 3, 9);
        MultiKey(editor, Key.Enter);
        Require(editor.Text == "  a\n  \n    b\n    ", "Enter failed to retain each line's own indentation.");
        editor.Undo();
        editor.CaretOffset = 3;
        MultiCall(editor, "AddMultiCaret", 9, 0);
        MultiKey(editor, Key.Tab);
        Require(editor.Text == "  a \n    b ", "Tab did not use each caret's grid column.");
        Require(MultiKey(editor, Key.Left) && editor.MultiCaretCount == 2, "Arrow navigation unexpectedly exited multi-caret mode.");
        MultiType(editor, "!");
        Require(editor.Text == "  a! \n    b! ", "Left arrow moved only the primary caret.");
        Require(MultiKey(editor, Key.Escape) && editor.MultiCaretCount == 1, "Escape did not leave multi-caret mode.");
        Require(editor.TextArea.Selection.IsEmpty, "Escape selected the span between independent carets.");
    }

    private static void MultiReadOnly()
    {
        var editor = MultiEditor("abc\ndef", 1, 5);
        editor.IsReadOnly = true;
        MultiType(editor, "x");
        MultiKey(editor, Key.Delete);
        Require(editor.Text == "abc\ndef" && !editor.Document.UndoStack.CanUndo, "A read-only document was changed through a secondary caret.");
        editor.IsReadOnly = false;
        editor.TextArea.ReadOnlySectionProvider = new MultiReadOnlyProvider(5);
        MultiType(editor, "x");
        Require(editor.Text == "abc\ndef", "A denied secondary insertion left a partial edit at another caret.");
        MultiKey(editor, Key.Delete);
        Require(editor.Text == "abc\ndef", "A denied secondary deletion left a partial edit.");
    }

    private sealed class MultiReadOnlyProvider(int forbidden) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => offset != forbidden;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
        {
            if (segment.Offset > forbidden || segment.EndOffset <= forbidden) yield return segment;
        }
    }

    private static void MultiSessions()
    {
        var editor = MultiEditor("abc\ndef", 1, 6);
        editor.ShowFile(File("multi.tex", editor.Text), editor.Text);
        Require(editor.MultiCaretCount == 2, "Saving/reactivating unchanged content discarded additional carets.");
        MultiType(editor, "x");
        Require(editor.Text == "axbc\ndexf", "Carets moved during same-file activation.");
        editor.ShowFile(File("other.tex", "other"), "other");
        Require(editor.MultiCaretCount == 1 && editor.TextArea.Selection is not null,
            "Changing files leaked multi-caret state into another document.");
        editor.ShowFile(File("multi.tex", "axbc\ndexf"), "axbc\ndexf");
        Require(editor.MultiCaretCount == 1 && editor.SelectionLength == 0, "Returning to a tab restored a spurious bounding selection.");
        MultiCall(editor, "AddMultiCaret", 0, 0);
        editor.ContentEdited += (_, _) => editor.ShowFile(File("event.tex", "event"), "event");
        MultiType(editor, "Z");
        Require(editor.CurrentFilePath == "event.tex" && editor.Text == "event" && editor.MultiCaretCount == 1,
            "An edit notification that switched documents received stale caret offsets.");
    }

    private static Point MultiPoint(LatexEditor editor, int offset, int padding = 0)
    {
        var view = editor.TextArea.TextView;
        var point = view.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(offset)), VisualYPosition.LineMiddle);
        point.X += padding * editor.GridCellWidth;
        return point - view.ScrollOffset;
    }

    private static void MultiPointer()
    {
        var editor = MultiEditor("ab中文z\nx\n012345", 1);
        var window = HostEditor(editor);
        try
        {
            var secondOffset = editor.Document.GetLineByNumber(3).Offset + 4;
            var target = MultiPoint(editor, secondOffset);
            Require((bool)MultiCall(editor, "BeginMultiPointer", MouseButton.Left, ModifierKeys.Alt, target)!, "Alt click was not accepted.");
            MultiCall(editor, "EndMultiPointer", target);
            Require(editor.MultiCaretCount == 2, "Alt click replaced the primary caret instead of adding an arbitrary column.");
            MultiSnapshot(editor, "independent-carets.png");
            MultiType(editor, "!");
            Require(editor.Text == "a!b中文z\nx\n0123!45", "Alt click did not use the rendered character position.");
            editor.Undo();
            SettleLayout(window);

            // Start at display column four (after 中), end at column six on line 3.
            // The short middle row has virtual columns three through five.
            var a = MultiPoint(editor, 3);
            var b = MultiPoint(editor, editor.Document.TextLength);
            MultiCall(editor, "BeginMultiPointer", MouseButton.Middle, ModifierKeys.None, a);
            MultiCall(editor, "MoveMultiPointer", b);
            MultiCall(editor, "EndMultiPointer", b);
            Require(editor.MultiCaretCount == 3, "Middle-button rectangle did not cover all displayed rows.");
            Require(editor.TextArea.Selection.GetText().Replace("\r", "") == "文\n\n45", "Rectangle boundaries did not respect CJK two-cell width.");
            MultiSnapshot(editor, "rectangle-cjk-short-row.png");
            MultiType(editor, "Q");
            Require(editor.Text == "ab中Qz\nx   Q\n0123Q", "Short rows were not padded to the rectangle's left display column.");
            editor.Undo();
            Require(editor.Text == "ab中文z\nx\n012345", "Column edit and virtual padding were not one undo step.");
            SettleLayout(window);

            // Alt drag must create a rectangle, not add just one caret at mouse-up.
            a = MultiPoint(editor, 1);
            b = MultiPoint(editor, editor.Document.GetLineByNumber(3).Offset + 2);
            MultiCall(editor, "BeginMultiPointer", MouseButton.Left, ModifierKeys.Alt, a);
            MultiCall(editor, "MoveMultiPointer", b);
            MultiCall(editor, "EndMultiPointer", b);
            Require(editor.MultiCaretCount == 3 && editor.SelectionLength > 0, "Alt drag was interpreted as an additive click.");
            MultiCall(editor, "BeginMultiPointer", MouseButton.Left, ModifierKeys.None, a);
            Require(editor.MultiCaretCount == 1, "Ordinary left click did not exit multi-caret mode.");
        }
        finally { window.Close(); }
    }

    private static void MultiSnapshot(LatexEditor editor, string name)
    {
        editor.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(editor);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine("artifacts", "multi-caret-probe"));
        Directory.CreateDirectory(directory);
        using var output = System.IO.File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void MultiWrappedRows()
    {
        var content = string.Concat(Enumerable.Repeat("abcdef ", 80));
        var editor = MultiEditor(content, 0);
        editor.WordWrap = true;
        var window = HostEditor(editor, width: 250, height: 300);
        try
        {
            var view = editor.TextArea.TextView;
            var visual = view.VisualLines[0];
            Require(visual.TextLines.Count > 3, "Wrapped test did not produce enough rendered rows.");
            var firstY = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.LineMiddle);
            var thirdY = visual.GetTextLineVisualYPosition(visual.TextLines[2], VisualYPosition.LineMiddle);
            var start = new Point(editor.GridCellWidth, firstY) - view.ScrollOffset;
            var end = new Point(editor.GridCellWidth * 2, thirdY) - view.ScrollOffset;
            MultiCall(editor, "BeginMultiPointer", MouseButton.Middle, ModifierKeys.None, start);
            MultiCall(editor, "MoveMultiPointer", end);
            MultiCall(editor, "EndMultiPointer", end);
            Require(editor.MultiCaretCount == 3, "Rectangle selection counted logical lines instead of three displayed rows.");
            var segments = editor.TextArea.Selection.Segments.ToArray();
            Require(segments.Length == 3 && segments.All(s => s.Length == 1), "Wrapped rectangle selected text beyond its narrow visible column.");
            MultiType(editor, "Q");
            Require(editor.Text.Count(c => c == 'Q') == 3 && editor.Text.Length == content.Length,
                "Wrapped rectangle replacement affected an entire paragraph.");
            editor.Undo();
            Require(editor.Text == content, "Wrapped rectangle replacement did not undo atomically.");
        }
        finally { window.Close(); }
    }

    private static void MultiVirtualAndScrolling()
    {
        var editor = MultiEditor("a", 1);
        MultiCall(editor, "AddMultiCaret", 1, 2);
        MultiCall(editor, "AddMultiCaret", 1, 5);
        MultiType(editor, "X");
        Require(editor.Text == "aX  X   X" && editor.MultiCaretCount == 3,
            "Distinct virtual carets at one line end duplicated padding or overwrote one another.");
        editor.Undo();
        Require(editor.Text == "a" && !editor.Document.UndoStack.CanUndo, "Virtual padding was left behind by undo.");

        editor = MultiEditor("a\nb", 1);
        var window = HostEditor(editor, height: 170);
        try
        {
            void VirtualRectangle()
            {
                SettleLayout(window);
                var a = MultiPoint(editor, 1, 2);
                var b = MultiPoint(editor, 3, 5);
                MultiCall(editor, "BeginMultiPointer", MouseButton.Middle, ModifierKeys.None, a);
                MultiCall(editor, "MoveMultiPointer", b);
                MultiCall(editor, "EndMultiPointer", b);
            }
            VirtualRectangle();
            MultiKey(editor, Key.Back);
            Require(editor.Text == "a\nb" && !editor.Document.UndoStack.CanUndo, "Deleting a wholly virtual selection changed document text.");
            MultiType(editor, "X");
            Require(editor.Text == "a  X\nb  X", "Backspace on a virtual selection did not collapse to its left edge.");
            editor.Undo();
            VirtualRectangle();
            MultiKey(editor, Key.Right);
            MultiType(editor, "X");
            Require(editor.Text == "a     X\nb     X", "Right arrow on a virtual selection did not collapse to its right edge.");

            var content = string.Join('\n', Enumerable.Repeat("line", 80));
            editor.ShowFile(File("scroll-multi.tex", content), content);
            editor.CaretOffset = 1;
            MultiCall(editor, "AddMultiCaret", 6, 0);
            SettleLayout(window);
            for (var i = 0; i < 30; i++)
            {
                MultiKey(editor, Key.Down);
                SettleLayout(window);
            }
            var view = editor.TextArea.TextView;
            var caret = view.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.TextBottom) - view.ScrollOffset;
            Require(editor.TextArea.Caret.Line == 32 && editor.VerticalOffset > 0 && caret.Y <= view.ActualHeight + 1,
                "Multi-caret keyboard navigation left the primary caret below the viewport.");
        }
        finally { window.Close(); }
    }

    private static void MultiCut()
    {
        var editor = MultiEditor("abc\ndef\nghi", 1, 2, 9);
        string? copied = null;
        Func<string, bool> copy = text => { copied = text; return true; };
        MultiCall(editor, "CutMultiCarets", copy);
        Require(copied == "abc\nghi" && editor.Text == "def\n",
            "Cut with empty carets did not deduplicate their complete lines.");
        editor.Undo();
        Require(editor.Text == "abc\ndef\nghi" && !editor.Document.UndoStack.CanUndo, "Cutting several lines required multiple undo steps.");
        editor.CaretOffset = 1;
        MultiCall(editor, "AddMultiCaret", 5, 0);
        copied = null;
        editor.IsReadOnly = true;
        MultiCall(editor, "CutMultiCarets", copy);
        Require(copied is null && editor.Text == "abc\ndef\nghi", "Read-only multi-caret cut touched text or the clipboard.");
        editor.IsReadOnly = false;
        editor.TextArea.ReadOnlySectionProvider = new MultiReadOnlyProvider(5);
        MultiCall(editor, "CutMultiCarets", copy);
        Require(copied is null && editor.Text == "abc\ndef\nghi", "Cut partially deleted editable lines before finding a protected line.");
    }

    private static void MultiMouseRouting()
    {
        var content = string.Join('\n', Enumerable.Repeat("abcdef", 40));
        var editor = MultiEditor(content, 0);
        var window = HostEditor(editor, width: 400, height: 220);
        try
        {
            // Route a real WPF MouseButtonEventArgs through TextView, preserving
            // OriginalSource and device coordinates. The transparent test host is
            // moved under the pointer; no synthetic OS input or clipboard is used.
            var mouse = Mouse.GetPosition(window);
            window.Left += mouse.X - 120;
            window.Top += mouse.Y - 90;
            window.Activate();
            editor.Focus();
            SettleLayout(window);
            var view = editor.TextArea.TextView;
            var point = Mouse.GetPosition(view);
            Require(point.X >= 0 && point.X < view.ActualWidth && point.Y >= 0 && point.Y < view.ActualHeight,
                "The routed mouse test did not place the pointer over TextView.");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            view.RaiseEvent(down);
            Require(down.Handled && editor.TextArea.IsMouseCaptured,
                $"The middle-button preview route failed to start and capture the drag: handled={down.Handled}, active={window.IsActive}, original={down.OriginalSource?.GetType().Name}, captured={Mouse.Captured?.GetType().Name}.");
            editor.ScrollToVerticalOffset(view.DefaultLineHeight);
            SettleLayout(window);
            var move = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent };
            view.RaiseEvent(move);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseUpEvent };
            view.RaiseEvent(up);
            Require(move.Handled && up.Handled && editor.MultiCaretCount >= 2 && !editor.TextArea.IsMouseCaptured,
                "WPF middle drag was swallowed by AvalonEdit or left the mouse captured.");
            var ordinary = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            view.RaiseEvent(ordinary);
            Require(!ordinary.Handled && editor.MultiCaretCount == 1, "Ordinary click did not release multi-caret mode for native selection.");
        }
        finally
        {
            if (editor.TextArea.IsMouseCaptured) editor.TextArea.ReleaseMouseCapture();
            window.Close();
        }
    }
}
