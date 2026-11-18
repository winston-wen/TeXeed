using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckSourceAnnotationModesAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "source-annotation-modes"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\nFirst sentence.\n    Second sentence.\n\\end{document}\n");
        await CallAsync(window, "OpenAsync", main);
        window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var other = (LatexEditor)window.FindName("RightEditor");
        var pdf = (PdfPreview)window.FindName("Preview");
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(ai, "_book")!;
        var composer = (TextBox)ai.FindName("Composer"); composer.Text = "保留主草稿";
        var action = editor.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "批注到当前会话"));
        var offset = editor.Text.IndexOf("First", StringComparison.Ordinal);
        var second = editor.Text.IndexOf("Second", StringComparison.Ordinal);
        if (offset < 0 || second < 0) throw new Exception("Source fixture is missing its selection");
        Point CharacterPoint(int index)
        {
            var view = editor.TextArea.TextView;
            var start = view.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(index)), VisualYPosition.TextTop) - view.ScrollOffset;
            var end = view.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(index + 1)), VisualYPosition.TextTop) - view.ScrollOffset;
            return new Point((start.X + end.X) / 2, start.Y + view.DefaultLineHeight / 2);
        }
        editor.Select(offset, 5);
        if (!editor.CaptureAnnotationContext(CharacterPoint(offset + 2)) || editor.SelectedText != "First")
            throw new Exception("Right-clicking inside selected source failed to preserve the selection");
        if (editor.CaptureAnnotationContext(CharacterPoint(second)) || editor.TextArea.Selection.Segments.Any(segment => segment.Length > 0))
            throw new Exception("Right-clicking outside the selection retained the old annotation target");
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (!editor.IsAiMarking || book.Entries.Count != 0) throw new Exception("Right-clicking B annotated the old selection A");
        editor.SetAiMarking(false);
        editor.Select(offset, second + 6 - offset);
        if (!editor.CaptureAnnotationContext(CharacterPoint(second + 2))) throw new Exception("A selected character in a later line was not hit");
        if (editor.CaptureAnnotationContext(new Point(-2, CharacterPoint(offset).Y))) throw new Exception("Line-number margin was treated as a selected character");
        editor.Select(offset, 5);
        if (editor.CaptureAnnotationContext(new Point(editor.TextArea.TextView.ActualWidth - 1, CharacterPoint(offset).Y)))
            throw new Exception("Blank space after a line was treated as selected text");

        editor.Select(offset, 1);
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var entry = book.Entries.Last();
        if (entry.Sources.Count != 1 || entry.Sources[0].Text != "F" || entry.IsPoint || entry.Question != "" || !entry.ReferenceSelected)
            throw new Exception("A one-character source selection did not become an empty selectable reference");
        var layers = (Dictionary<LatexEditor, AiAnnotationLayer>)Field(window, "_sourceAnnotations")!;
        var popup = (Popup)Field(layers[editor], "_popup")!;
        if (!popup.IsOpen || composer.Text != "保留主草稿") throw new Exception("Source annotation did not open locally or disturbed the composer draft");
        var confirm = AnnotationVisuals<Button>(popup.Child).Single(button => button.Name == "AnnotationConfirm");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (popup.IsOpen || !entry.ReferenceSelected || entry.Question != "") throw new Exception("Completing an empty annotation did not keep its reference");

        editor.Select(offset, 0);
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (!editor.IsAiMarking || other.IsAiMarking || pdf.IsAiSelecting) throw new Exception("Source selection mode leaked into a different region");
        var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.QueryCursorEvent };
        editor.TextArea.TextView.RaiseEvent(cursor);
        if (!cursor.Handled || cursor.Cursor != Cursors.Cross) throw new Exception("Source marking mode has no dedicated pointer");
        var count = book.Entries.Count;
        var point = editor.CreateAiMark(offset, offset, false);
        Invoke(window, "CollectSourceMark", editor, point);
        if (book.Entries.Count != count) throw new Exception("A source point was accepted");
        var exact = editor.CreateAiMark(offset + 3, offset, true);
        if (exact.IsPoint || exact.Offset != offset || exact.Length != 3) throw new Exception("Reverse source selection was extended or changed");
        ((EventHandler<SourceAiMark>?)Field(editor, "AiMarked"))?.Invoke(editor, exact);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var marked = book.Entries.Last();
        if (book.Entries.Count != count + 1 || marked.Sources.Single().Text != "Fir" || marked.Question != "" || !popup.IsOpen)
            throw new Exception("Selecting in source marking mode did not open the exact selected reference");
        popup.IsOpen = false;
        pdf.SetAiMarking(true);
        if (editor.IsAiMarking || !pdf.IsAiSelecting) throw new Exception("PDF mode did not end source marking without crossing regions");
        pdf.SetAiMarking(false);
        editor.Select(offset, 0); action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var escape = CreateKey(window, Key.Escape, Keyboard.PreviewKeyDownEvent); escape.Source = editor.TextArea.TextView;
        editor.TextArea.TextView.RaiseEvent(escape);
        if (editor.IsAiMarking || !escape.Handled) throw new Exception("Escape did not end source annotation selection mode");
        var current = new ProjectFile(main, "main.tex", ProjectFileKind.Tex, editor.Text);
        editor.SetAiMarking(true);
        editor.ShowFile(current, current.Content);
        if (!editor.IsAiMarking) throw new Exception("Refreshing the unchanged source unnecessarily exited selection mode");
        var updated = current with { Content = current.Content + "% same file refresh\n" };
        editor.ShowFile(updated, updated.Content);
        if (!editor.IsAiMarking) throw new Exception("Refreshing the same source exited selection mode");
        var otherFile = new ProjectFile(Path.Combine(folder, "other.tex"), "other.tex", ProjectFileKind.Tex, "other source");
        editor.ShowFile(otherFile, otherFile.Content);
        if (editor.IsAiMarking) throw new Exception("Source selection mode leaked into another file");
        editor.SetAiMarking(true); editor.Visibility = Visibility.Collapsed;
        if (editor.IsAiMarking) throw new Exception("Hiding the source editor did not exit selection mode");
        editor.Visibility = Visibility.Visible;
        if (editor.IsAiMarking) throw new Exception("Showing the source editor restored an old selection mode");
        editor.SetAiMarking(true); editor.CloseView();
        if (editor.IsAiMarking) throw new Exception("Closing the source view did not exit selection mode");
        editor.ShowFile(current, current.Content); editor.SetAiMarking(true); editor.ClearSession();
        if (editor.IsAiMarking) throw new Exception("Clearing the source session did not exit selection mode");
        if (composer.Text != "保留主草稿") throw new Exception("Source annotation mode changed the composer draft");
        Console.WriteLine("PASS: source context annotation, exact nonempty selections, empty reference, local popup, region/file isolation, same-file refresh and Escape");
    }
}
