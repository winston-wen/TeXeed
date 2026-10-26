using System.Reflection;
using System.Windows;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Run("tab switching preserves documents, undo, selection and cursor", PersistentTabs);
            Run("closing a document view retains drafts, cursor and undo when reopened", ClosedViewSession);
            Run("programmatic loads are silent and external changes refresh content", ExternalRefresh);
            Run("comment and indent commands retain reversible edits", CommentIndent);
            Run("formatting is undoable and navigation clamps positions", FormatNavigation);
            Run("LaTeX and BibTeX highlighters load and tokenize", Highlighting);
            Run("command snippets insert multiline TeX with a useful cursor", Snippets);
            Run("reference completion uses live labels and citation context", References);
            Run("completion excludes comments and escaped commands", CompletionExclusions);
            Run("literal replacements preserve TeX backslashes and dollar signs", LiteralReplacement);
            Run("regex replacement expands captures in full document context", RegexReplacement);
            Run("replacement uses the search panel case and whole-word options", ReplacementOptions);
            Run("replace current selects a match before changing unrelated selections", CurrentReplacement);
            Run("replace all is one undo step and one edit notification", ReplacementUndo);
            Run("replacement respects read-only files and current document sessions", ReplacementSessions);
            Run("empty, unmatched and invalid searches leave content intact", ReplacementErrors);
            Run("zero-length regex replacements terminate and advance", EmptyMatchReplacement);
            Run("viewport anchor follows scrolling without moving the caret", ViewportScroll);
            Run("single navigation centers nearby and repeated targets without a second click", CenteredNavigation);
            Run("viewport anchor refreshes on resize and file changes", ViewportResizeAndFiles);
            Run("viewport anchor uses rendered rows for wrapped and folded text", ViewportWrapAndFold);
            Run("viewport follow uses file start at the top and notifies boundary crossings", ViewportTopBoundary);
            Run("word wrap reflows without editing text, losing selection or undo", WordWrapToggle);
            RunFixedGridTests();
            RunMultiCaretTests();
            Console.WriteLine($"PASS: {_passed} native editor checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Run(string name, Action action) { action(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static ProjectFile File(string path, string content, ProjectFileKind kind = ProjectFileKind.Tex) => new(path, path, kind, content);

    private static void PersistentTabs()
    {
        var editor = new LatexEditor();
        var first = File("chapter.tex", "first\nsecond");
        var other = File("other.tex", "other");
        editor.ShowFile(first, first.Content);
        var document = editor.Document;
        document.Insert(5, "!");
        editor.CaretOffset = 4;
        editor.Select(1, 3);
        var caret = editor.CaretOffset;
        editor.ShowFile(other, other.Content);
        editor.ShowFile(first, "first!\nsecond");
        Require(ReferenceEquals(document, editor.Document), "Switching tabs discarded the text document.");
        Require(editor.CaretOffset == caret && editor.SelectionStart == 1 && editor.SelectionLength == 3, "Switching tabs lost cursor or selection.");
        Require(editor.Document.UndoStack.CanUndo, "Tab switch discarded undo.");
        editor.Undo();
        Require(editor.Text == first.Content, "Undo after switching did not restore original text.");
    }
    private static void ClosedViewSession()
    {
        var editor = new LatexEditor(); var file = File("closed.tex", "original");
        editor.ShowFile(file, file.Content); editor.Document.Insert(8, " draft");
        editor.Select(2, 3); var caret = editor.CaretOffset; var document = editor.Document;
        editor.CloseView();
        Require(editor.CurrentFilePath is null && editor.Text == "", "Closing last tab retained an active editing document");
        editor.ShowFile(file, "original draft");
        Require(ReferenceEquals(document, editor.Document) && editor.CaretOffset == caret && editor.SelectionStart == 2 && editor.SelectionLength == 3,
            "Reopening a closed view lost its document, caret or selection");
        editor.Undo(); Require(editor.Text == file.Content, "Closing a view lost the preceding edit's undo history");
    }

    private static void ExternalRefresh()
    {
        var editor = new LatexEditor();
        var file = File("main.tex", "original");
        var edits = 0;
        editor.ContentEdited += (_, _) => edits++;
        editor.ShowFile(file, file.Content);
        editor.ShowFile(file, "external");
        Require(edits == 0 && editor.Text == "external", "Loading external content emitted a user edit.");
        editor.Document.Insert(editor.Document.TextLength, " edit");
        Require(edits == 1, "User document change did not emit exactly one edit event.");
        editor.ClearSession();
        Require(editor.CurrentFilePath is null && editor.CurrentLocation is null && editor.Text == "", "Project sessions were not cleared.");
        Require(edits == 1, "Clearing project emitted an edit.");
    }

    private static void CommentIndent()
    {
        var editor = new LatexEditor();
        var content = "  alpha\n  beta\ngamma";
        editor.ShowFile(File("main.tex", content), content);
        editor.Select(0, content.IndexOf("gamma", StringComparison.Ordinal));
        editor.ExecuteCommand("comment");
        Require(editor.Text == "  % alpha\n  % beta\ngamma", "Comment command touched a line after the selected range.");
        editor.Undo();
        Require(editor.Text == content, "Comment must undo as a single operation.");
        editor.Select(0, content.IndexOf("gamma", StringComparison.Ordinal));
        editor.ExecuteCommand("indent");
        Require(editor.Text == "    alpha\n    beta\ngamma", "Indentation did not apply to selected lines.");
        editor.Select(0, editor.Text.IndexOf("gamma", StringComparison.Ordinal));
        editor.ExecuteCommand("outdent");
        Require(editor.Text == content, "Outdent did not reverse indent.");
        editor.ShowFile(File("main.tex", "  % alpha\n  %beta"), "  % alpha\n  %beta");
        editor.SelectAll();
        editor.ExecuteCommand("comment");
        Require(editor.Text == "  alpha\n  beta", "Uncomment lost content or indentation.");
    }

    private static void FormatNavigation()
    {
        var editor = new LatexEditor();
        var content = "\\begin{document}\nHello\n\\end{document}";
        var file = File("main.tex", content);
        editor.SetProjectFiles([file]);
        editor.ShowFile(file, content);
        editor.ExecuteCommand("format");
        Require(editor.Text.Contains("\n  Hello\n", StringComparison.Ordinal), "Formatter did not indent environment content.");
        editor.Undo();
        Require(editor.Text == content, "Formatting must be one undo operation.");
        editor.NavigateTo(new("main.tex", 9999, 9999));
        Require(editor.CaretOffset == editor.Document.TextLength, "Navigation did not clamp to valid text.");
        editor.NavigateTo(new("main.tex", -5, -10));
        Require(editor.CaretOffset == 0, "Navigation did not clamp negative positions.");
    }

    private static void Highlighting()
    {
        var editor = new LatexEditor();
        foreach (var file in new[] { File("main.tex", "\\section{Title}\n% note\n$x^2$\n\\[ x \\]"), File("refs.bib", "@article{key,\n title = {Hello},\n year = 2026\n}", ProjectFileKind.Bibliography) })
        {
            editor.ShowFile(file, file.Content);
            using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
            var segments = Enumerable.Range(1, editor.Document.LineCount).Sum(n => highlighter.HighlightLine(n).Sections.Count);
            Require(segments > 3, $"{file.Name} was not syntax highlighted.");
        }
    }

    private static void Snippets()
    {
        var editor = new LatexEditor();
        editor.ShowFile(File("main.tex", "  \\equ"), "  \\equ");
        var dataType = typeof(LatexEditor).GetNestedType("LatexCompletion", BindingFlags.NonPublic)!;
        var data = (ICompletionData)Activator.CreateInstance(dataType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            ["equation", "begin{equation}\n  ¦\n\\end{equation}", "Equation", false], null)!;
        data.Complete(editor.TextArea, new TextSegment { StartOffset = 3, Length = 3 }, EventArgs.Empty);
        Require(editor.Text == "  \\begin{equation}\n    \n  \\end{equation}", "Snippet inserted literal newlines or failed to inherit indentation.");
        Require(editor.TextArea.Caret.Line == 2 && editor.TextArea.Caret.Column == 5, "Snippet cursor is outside its editable field.");
    }

    private static object? CompletionContext(LatexEditor editor)
        => typeof(LatexEditor).GetMethod("BuildCompletionContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);

    private static IReadOnlyList<ICompletionData> Items(object context)
        => ((IEnumerable<ICompletionData>)context.GetType().GetProperty("Items")!.GetValue(context)!).ToArray();

    private static void References()
    {
        var editor = new LatexEditor();
        var main = File("main.tex", "\\label{old}");
        var bib = File("refs.bib", "@article{first, title={First paper}}\n@book{smith2026, title={A useful title}}", ProjectFileKind.Bibliography);
        editor.SetProjectFiles([main, bib]);
        var source = "\\section{Introduction}\n\\label{sec:intro}\n\\eqref{sec:";
        editor.ShowFile(main, source);
        editor.CaretOffset = source.Length;
        var context = CompletionContext(editor) ?? throw new InvalidOperationException("Equation reference did not offer completion.");
        var labels = Items(context);
        Require(labels.Count == 1 && labels[0].Text == "sec:intro", "Reference completion ignored unsaved label changes.");
        Require(labels[0].Description.ToString()!.Contains("Introduction", StringComparison.Ordinal), "Label completion omitted heading context.");
        source = "\\citep[see][p. 2]{first, sm";
        editor.ShowFile(main, source);
        editor.CaretOffset = source.Length;
        context = CompletionContext(editor)!;
        var citations = Items(context);
        Require(citations.Count == 1 && citations[0].Text == "smith2026", "Multi-citation offered an already selected key or ignored options.");
        Require(citations[0].Description.ToString()!.Contains("A useful title", StringComparison.Ordinal), "Citation completion omitted bibliography title.");
        var start = (int)context.GetType().GetProperty("StartOffset")!.GetValue(context)!;
        citations[0].Complete(editor.TextArea, new TextSegment { StartOffset = start, Length = editor.CaretOffset - start }, EventArgs.Empty);
        Require(editor.Text == "\\citep[see][p. 2]{first, smith2026", "Citation insertion damaged the prior key or options.");
    }

    private static void CompletionExclusions()
    {
        var editor = new LatexEditor();
        var labels = File("labels.tex", "\\label{test}");
        editor.SetProjectFiles([labels]);
        foreach (var source in new[] { "% \\ref{", "% \\ref{\n", @"\\ref{" })
        {
            editor.ShowFile(File("main.tex", source), source);
            editor.CaretOffset = source.Length;
            Require(CompletionContext(editor) is null, "Completion appeared in commented or escaped input: " + source);
        }
        var escapedPercent = @"\% \ref{";
        editor.ShowFile(File("main.tex", escapedPercent), escapedPercent);
        editor.CaretOffset = escapedPercent.Length;
        Require(CompletionContext(editor) is not null, "Escaped percent was incorrectly treated as a comment.");
    }

    private static void Search(LatexEditor editor, string pattern, bool regex = false, bool matchCase = false, bool wholeWords = false)
    {
        var search = (SearchPanel)typeof(LatexEditor).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        search.UseRegex = regex;
        search.MatchCase = matchCase;
        search.WholeWords = wholeWords;
        search.SearchPattern = pattern;
    }

    private static LatexEditor ReplacementEditor(string content)
    {
        var editor = new LatexEditor();
        editor.ShowFile(File("main.tex", content), content);
        return editor;
    }

    private static void LiteralReplacement()
    {
        var editor = ReplacementEditor(@"\alpha $1 $$ \alpha");
        Search(editor, @"\alpha");
        var result = editor.ReplaceAll(@"\beta{$1}\\$");
        Require(result.Count == 2 && editor.Text == @"\beta{$1}\\$ $1 $$ \beta{$1}\\$",
            "Literal replacements interpreted TeX backslashes or dollar expressions.");
        Search(editor, "$1");
        result = editor.ReplaceAll("");
        Require(result.Count == 3 && editor.Text == @"\beta{}\\$  $$ \beta{}\\$", "Empty replacement did not delete literal matches.");
    }

    private static void RegexReplacement()
    {
        var editor = ReplacementEditor(@"\label{first} \label{second}");
        Search(editor, @"\\label\{(?<key>[^}]+)\}", regex: true);
        var result = editor.ReplaceAll(@"\ref{${key}} $$ $1");
        Require(result.Count == 2 && editor.Text == @"\ref{first} $ first \ref{second} $ second",
            "Regex replacement did not expand named and numbered captures or escaped dollars.");

        editor = ReplacementEditor(@"\label{first} \label{second}");
        Search(editor, @"(?<=\\label\{)([^}]+)(?=\})", regex: true);
        editor.Select(7, 5);
        result = editor.ReplaceCurrent("$1-new");
        Require(result.Count == 1 && editor.Text == @"\label{first-new} \label{second}",
            "Replace current evaluated a contextual regex against only the selected text.");
    }

    private static void ReplacementOptions()
    {
        var editor = ReplacementEditor("Alpha alphabet alpha ALPHA");
        Search(editor, "alpha", matchCase: true, wholeWords: true);
        Require(editor.ReplaceAll("beta").Count == 1 && editor.Text == "Alpha alphabet beta ALPHA",
            "Case-sensitive whole-word replacement changed the wrong matches.");
        Search(editor, "alpha", wholeWords: true);
        Require(editor.ReplaceAll("gamma").Count == 2 && editor.Text == "gamma alphabet beta gamma",
            "Case-insensitive whole-word replacement did not match the search panel.");
        Search(editor, "alpha");
        Require(editor.ReplaceAll("X").Count == 1 && editor.Text == "gamma Xbet beta gamma",
            "Disabling whole-word search failed to replace a substring.");
    }

    private static void CurrentReplacement()
    {
        var editor = ReplacementEditor("zero one one");
        Search(editor, "one");
        editor.Select(0, 4);
        editor.CaretOffset = 4;
        var result = editor.ReplaceCurrent("three");
        Require(result.Count == 0 && editor.Text == "zero one one" && editor.SelectionStart == 5 && editor.SelectionLength == 3,
            "Replace current changed an unrelated selection or failed to select the next match.");
        result = editor.ReplaceCurrent("three");
        Require(result.Count == 1 && editor.Text == "zero three one" && editor.SelectionStart == 11 && editor.SelectionLength == 3,
            "Replacing the selected match failed to advance using adjusted offsets.");
        Require(editor.ReplaceCurrent("").Count == 1 && editor.Text == "zero three ", "Replace current could not delete the selected match.");

        editor = ReplacementEditor("alphabet alpha");
        Search(editor, "alpha", wholeWords: true);
        editor.Select(0, 5);
        editor.CaretOffset = 5;
        Require(editor.ReplaceCurrent("X").Count == 0 && editor.Text == "alphabet alpha" && editor.SelectionStart == 9,
            "A selection inside a word was replaced despite whole-word search.");
        editor.Select(editor.Document.TextLength, 0);
        editor.CaretOffset = editor.Document.TextLength;
        Require(editor.ReplaceCurrent("X").Count == 0 && editor.SelectionStart == 9, "Finding a replacement match did not wrap.");
    }

    private static void ReplacementUndo()
    {
        const string content = "one one\none";
        var editor = ReplacementEditor(content);
        Search(editor, "one");
        var edits = 0;
        editor.ContentEdited += (_, _) => edits++;
        var result = editor.ReplaceAll("a much longer replacement");
        var replaced = editor.Text;
        Require(result.Count == 3 && edits == 1, "Replace all did not emit one completed edit notification.");
        Require(editor.Undo() && editor.Text == content && !editor.Document.UndoStack.CanUndo,
            "Replace all did not undo as a single operation.");
        Require(editor.Redo() && editor.Text == replaced, "Replace all could not be redone.");
    }

    private static void ReplacementSessions()
    {
        var editor = new LatexEditor { Text = "one" };
        Search(editor, "one");
        Require(editor.ReplaceAll("two").Count == 0 && editor.Text == "one", "Replacement changed an editor with no open file.");
        editor.ShowFile(File("image.png", "one", ProjectFileKind.Asset), "one");
        editor.SelectAll();
        Require(editor.ReplaceCurrent("two").Count == 0 && editor.ReplaceAll("two").Count == 0 && editor.Text == "one",
            "Replacement changed a read-only file.");

        editor = ReplacementEditor("one one");
        var firstDocument = editor.Document;
        Search(editor, "one");
        editor.Select(0, 3);
        var other = File("other.tex", "one other");
        editor.ShowFile(other, other.Content);
        Require(editor.ReplaceAll("two").Count == 1 && editor.Text == "two other" && firstDocument.Text == "one one",
            "Replacement reused matches from the previous file or changed another session.");
        editor.ShowFile(File("main.tex", firstDocument.Text), firstDocument.Text);
        Require(!editor.Document.UndoStack.CanUndo, "Replacing another file changed this session's undo history.");

        // The application's edit handler may synchronously activate another tab.
        editor.ContentEdited += (_, _) => editor.ShowFile(other, "two other");
        editor.Select(0, 3);
        Require(editor.ReplaceCurrent("three").Count == 1 && firstDocument.Text == "three one" && editor.Text == "two other",
            "An edit notification caused replacement to spill into the newly active file.");
    }

    private static void ReplacementErrors()
    {
        var editor = ReplacementEditor("one one");
        foreach (var pattern in new[] { "", "absent", "[" })
        {
            // A bound search field keeps its dependency-property value when AvalonEdit's
            // validation callback rejects it. Simulate that binding behavior without a window.
            try { Search(editor, pattern, regex: true); }
            catch (SearchPatternException) when (pattern == "[") { }
            var all = editor.ReplaceAll("two");
            var current = editor.ReplaceCurrent("two");
            Require(all.Count == 0 && current.Count == 0 && all.Message.Length > 0 && editor.Text == "one one"
                && !editor.Document.UndoStack.CanUndo, "An empty, unmatched or invalid search mutated the document.");
        }
        Search(editor, "(one)", regex: true);
        var invalid = editor.ReplaceAll("${2147483648}");
        Require(invalid.Count == 0 && editor.Text == "one one" && !editor.Document.UndoStack.CanUndo,
            "An invalid replacement expression partially changed the document.");
    }

    private static void EmptyMatchReplacement()
    {
        var editor = ReplacementEditor("a\nb");
        Search(editor, "^|$", regex: true);
        var result = editor.ReplaceAll("|");
        Require(result.Count == 4 && editor.Text == "|a|\n|b|", "Zero-length matches were skipped or processed repeatedly.");
        Require(editor.Undo() && editor.Text == "a\nb", "Zero-length replacements did not share one undo step.");

        Search(editor, "^", regex: true);
        editor.Select(0, 0);
        editor.CaretOffset = 0;
        Require(editor.ReplaceCurrent("> ").Count == 1 && editor.Text == "> a\nb" && editor.SelectionStart == 4,
            "Replacing a zero-length match did not advance to the next line.");
        Require(editor.ReplaceCurrent("> ").Count == 1 && editor.Text == "> a\n> b",
            "The next zero-length match could not be replaced.");

        editor = ReplacementEditor("");
        Search(editor, "$", regex: true);
        Require(editor.ReplaceAll("end").Count == 1 && editor.Text == "end", "A zero-length match in an empty document was missed.");
    }
}
