using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using TeXeed.Core;

namespace TeXeed.App.Controls;

/// <summary>A native LaTeX editor whose documents (and undo stacks) survive tab changes.</summary>
public sealed partial class LatexEditor : TextEditor
{
    private readonly Dictionary<string, FileSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SearchPanel _search;
    private IReadOnlyList<ProjectFile> _files = [];
    private CompletionWindow? _completion;
    private bool _changingDocument;
    private static readonly Regex CommandPrefix = new(@"\\(?<name>[a-zA-Z@]*)$", RegexOptions.Compiled);
    private static readonly Regex ReferencePrefix = new(
        @"\\(?<command>(?:(?:eq|auto|page|name|v|c|C|labelc)?ref|[a-zA-Z]*cite[a-zA-Z]*))\*?(?:\[[^\]]*\]){0,2}\{(?<keys>[^{}]*)$",
        RegexOptions.Compiled);
    private static readonly Lazy<IHighlightingDefinition> LatexHighlighting = new(() => LoadHighlighting(LatexSyntax));
    private static readonly Lazy<IHighlightingDefinition> BibHighlighting = new(() => LoadHighlighting(BibSyntax));

    public event EventHandler<SourceLocation>? CursorLocationChanged;
    public event EventHandler? ContentEdited;
    public string? CurrentFilePath { get; private set; }
    public SourceLocation? CurrentLocation => CurrentFilePath is { } path
        ? new SourceLocation(path, TextArea.Caret.Line, TextArea.Caret.Column) : null;

    public LatexEditor()
    {
        ShowLineNumbers = true;
        FontFamily = new FontFamily("Cascadia Code, Consolas");
        FontSize = 15;
        Background = Brush("EditorBackground");
        Foreground = Brush("EditorForeground");
        LineNumbersForeground = Brush("Subtle");
        BorderThickness = new Thickness(0);
        // AvalonEdit applies this to the viewport, inside the scrollbars. Right/bottom
        // padding would leave fixed blank strips where scrolling text gets clipped.
        Padding = new Thickness(12, 14, 0, 0);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 2;
        Options.HighlightCurrentLine = true;
        // Keep room below EOF so the final lines can scroll through the view center.
        Options.AllowScrollBelowDocument = true;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        TextArea.SelectionBrush = Brush("Selection");
        TextArea.SelectionForeground = Brush("EditorForeground");
        TextArea.SelectionBorder = null;
        TextArea.TextView.CurrentLineBackground = Brush("CurrentLine");
        TextArea.TextView.CurrentLineBorder = new Pen(Brush("CurrentLine"), 0);
        TextArea.IndentationStrategy = new LatexIndentationStrategy();
        _search = SearchPanel.Install(this);
        // The compact AvalonEdit search template must not inherit the application's
        // padded form controls or the document's adjustable editor typography.
        _search.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        _search.FontSize = 13;
        _search.FontWeight = FontWeights.Normal;
        _search.FontStyle = FontStyles.Normal;
        _search.Resources[typeof(Button)] = new Style(typeof(Button));
        _search.Resources[typeof(TextBox)] = new Style(typeof(TextBox))
        {
            Setters =
            {
                new Setter(Control.PaddingProperty, new Thickness(2, 0, 2, 0)),
                new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SidebarBackground")),
                new Setter(Control.ForegroundProperty, new DynamicResourceExtension("EditorForeground")),
                new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Subtle")),
                new Setter(TextBox.CaretBrushProperty, new DynamicResourceExtension("EditorForeground")),
                new Setter(TextBox.SelectionBrushProperty, new DynamicResourceExtension("Selection")),
                new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        };
        InitializeSearchUi();
        InitializeViewportTracking();
        InitializeFixedGrid();
        InitializeMultiCaret();
        ApplyTheme();
        Loaded += (_, _) => { TeXeed.App.Services.ThemeManager.Changed += ThemeChanged; ApplyTheme(); };
        Unloaded += (_, _) => TeXeed.App.Services.ThemeManager.Changed -= ThemeChanged;
        TextChanged += (_, _) =>
        {
            if (!_changingDocument && CurrentFilePath is not null)
            {
                ResetReplacementUi();
                ContentEdited?.Invoke(this, EventArgs.Empty);
            }
        };
        TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!_changingDocument && CurrentLocation is { } location)
                CursorLocationChanged?.Invoke(this, location);
        };
        TextArea.TextEntering += OnTextEntering;
        TextArea.TextEntered += OnTextEntered;
        PreviewKeyDown += OnEditorKeyDown;
        Unloaded += (_, _) => CloseCompletion();
    }

    public void SetProjectFiles(IEnumerable<ProjectFile> files) => _files = files.ToArray();

    public void ClearSession()
    {
        ResetMultiCaret();
        CloseCompletion();
        _changingDocument = true;
        try
        {
            CurrentFilePath = null;
            Document = new TextDocument();
            _sessions.Clear();
            _files = [];
            _search.Close();
            ResetReplacementUi();
        }
        finally { _changingDocument = false; }
        ResetViewportTracking();
    }

    /// <summary>Close the visible document without discarding its buffer or undo stack.</summary>
    public void CloseView()
    {
        CaptureSession(); ResetMultiCaret(); CloseCompletion();
        _changingDocument = true;
        try
        {
            CurrentFilePath = null; Document = new TextDocument(); SyntaxHighlighting = null;
            _search.Close(); ResetReplacementUi();
        }
        finally { _changingDocument = false; }
        ResetViewportTracking();
    }

    public void ShowFile(ProjectFile file, string content)
    {
        // Saving/live compilation reactivates the current file. Keep the exact
        // selection (including independent carets) when its document has not changed.
        if (string.Equals(CurrentFilePath, file.Path, StringComparison.OrdinalIgnoreCase) && Document.Text == content)
        {
            IsReadOnly = !file.IsEditable;
            return;
        }
        CloseCompletion();
        ResetMultiCaret();
        CaptureSession();
        _changingDocument = true;
        try
        {
            if (!_sessions.TryGetValue(file.Path, out var session))
            {
                session = new FileSession(new TextDocument(content));
                _sessions.Add(file.Path, session);
            }
            else if (session.Document.Text != content)
            {
                // A disk reload is a new baseline; switching between unchanged tabs never touches the document.
                session.Document.Text = content;
                session.Document.UndoStack.ClearAll();
            }
            CurrentFilePath = file.Path;
            Document = session.Document;
            IsReadOnly = !file.IsEditable;
            SyntaxHighlighting = file.Kind == ProjectFileKind.Bibliography ? BibHighlighting.Value : LatexHighlighting.Value;
            var caret = Math.Clamp(session.CaretOffset, 0, Document.TextLength);
            var selectionStart = Math.Clamp(session.SelectionStart, 0, Document.TextLength);
            Select(selectionStart, Math.Clamp(session.SelectionLength, 0, Document.TextLength - selectionStart));
            CaretOffset = caret;
            ScrollToVerticalOffset(session.VerticalOffset);
            ScrollToHorizontalOffset(session.HorizontalOffset);
        }
        finally { _changingDocument = false; }
        ResetReplacementUi();
        QueueViewportLocationChanged();
        if (CurrentLocation is { } location) CursorLocationChanged?.Invoke(this, location);
    }

    public void NavigateTo(SourceLocation location, bool center = false)
    {
        ResetMultiCaret();
        if (!string.Equals(CurrentFilePath, location.Path, StringComparison.OrdinalIgnoreCase))
        {
            var file = _files.FirstOrDefault(f => string.Equals(f.Path, location.Path, StringComparison.OrdinalIgnoreCase));
            if (file is null) return;
            ShowFile(file, _sessions.TryGetValue(file.Path, out var session) ? session.Document.Text : file.Content);
        }
        var line = Document.GetLineByNumber(Math.Clamp(location.Line, 1, Document.LineCount));
        CaretOffset = line.Offset + Math.Clamp(location.Column - 1, 0, line.Length);
        Select(CaretOffset, 0);
        ScrollTo(TextArea.Caret.Line, TextArea.Caret.Column);
        TextArea.Caret.BringCaretToView();
        Focus();
        if (center)
        {
            // Finish pending document/scroll layout before reading the viewport size.
            UpdateLayout();
            // AvalonEdit's default ScrollTo has a minimum-scroll threshold. Disable
            // it for explicit outline navigation, including already-visible targets.
            ScrollTo(TextArea.Caret.Line, TextArea.Caret.Column,
                ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle,
                TextArea.TextView.ActualHeight / 2, 0);
        }
    }

    public void ExecuteCommand(string command)
    {
        ResetMultiCaret();
        if (command is "find" or "replace")
        {
            OpenSearch(command == "replace");
            return;
        }
        if (IsReadOnly || CurrentFilePath is null) return;
        CloseCompletion();
        switch (command)
        {
            case "comment": ToggleComment(); break;
            case "indent": IndentSelection(false); break;
            case "outdent": IndentSelection(true); break;
            case "format": FormatDocument(); break;
        }
        Focus();
    }

    private void CaptureSession()
    {
        if (CurrentFilePath is not { } path || !_sessions.TryGetValue(path, out var session)) return;
        session.CaretOffset = CaretOffset;
        session.SelectionStart = SelectionStart;
        session.SelectionLength = SelectionLength;
        session.VerticalOffset = VerticalOffset;
        session.HorizontalOffset = HorizontalOffset;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (_search.IsKeyboardFocusWithin)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F or Key.H)
            { OpenSearch(e.Key == Key.H); e.Handled = true; }
            return;
        }
        if (HandleMultiCaretKeyDown(e)) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F or Key.H)
            { OpenSearch(e.Key == Key.H); e.Handled = true; }
            else if (e.Key == Key.Space) { ShowCompletion(true); e.Handled = true; }
            else if (e.Key == Key.Oem2) { ExecuteCommand("comment"); e.Handled = true; }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            { ExecuteCommand("format"); e.Handled = true; }
            return;
        }
        if (IsReadOnly) return;
        if (e.Key == Key.Tab && _completion is null)
        {
            if (SelectionLength == 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                var spaces = Options.IndentationSize - GetGridColumn(CaretOffset) % Options.IndentationSize;
                Document.Insert(CaretOffset, new string(' ', spaces));
            }
            else IndentSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && SelectionLength == 0 && CaretOffset > 0 && CaretOffset < Document.TextLength)
        {
            var left = Document.GetCharAt(CaretOffset - 1);
            var right = Document.GetCharAt(CaretOffset);
            if ((left == '{' && right == '}') || (left == '[' && right == ']'))
            {
                var offset = CaretOffset - 1;
                Document.Remove(offset, 2);
                CaretOffset = offset;
                e.Handled = true;
            }
        }
    }

    private void OnTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (HandleMultiCaretTextEntering(e)) return;
        if (IsReadOnly || e.Text.Length != 1) return;
        var ch = e.Text[0];
        if (_completion is not null && !(char.IsLetterOrDigit(ch) || ch is '_' or ':' or '-' or '.' or '@')) CloseCompletion();
        var offset = CaretOffset;
        if (IsCommentAtCaret() || IsEscaped(offset)) return;
        if ((ch == '}' || ch == ']') && SelectionLength == 0 && offset < Document.TextLength && Document.GetCharAt(offset) == ch)
        {
            CaretOffset++;
            e.Handled = true;
        }
        else if (ch is '{' or '[')
        {
            var closing = ch == '{' ? '}' : ']';
            var start = SelectionStart;
            var selected = SelectedText;
            using (Document.RunUpdate()) Document.Replace(start, SelectionLength, ch + selected + closing);
            CaretOffset = start + 1 + selected.Length;
            Select(start + 1, selected.Length);
            e.Handled = true;
            if (selected.Length == 0 && ch == '{') ShowCompletion(false);
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (HasMultipleCarets) return;
        if (_completion is null && e.Text.Length == 1 && (e.Text[0] is '\\' or '{' or ',' || char.IsLetter(e.Text[0])))
            ShowCompletion(false);
    }

    private void ShowCompletion(bool explicitRequest)
    {
        if (HasMultipleCarets) return;
        if (_completion is not null) return;
        var context = BuildCompletionContext(explicitRequest);
        if (context is null) return;
        var (startOffset, items, typed, _) = context;
        var window = new CompletionWindow(TextArea)
        {
            StartOffset = startOffset,
            EndOffset = CaretOffset,
            CloseAutomatically = true,
            Width = 360,
            MaxHeight = 300,
            Background = Brush("EditorBackground"),
            Foreground = Brush("EditorForeground"),
            ResizeMode = ResizeMode.NoResize
        };
        foreach (var item in items)
        {
            item.Inserted += () =>
            {
                var document = Document;
                var version = document.Version;
                var offset = CaretOffset;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Chain command -> label/citation, not command -> the same command again.
                    if (IsLoaded && ReferenceEquals(Document, document) && Document.Version == version && CaretOffset == offset &&
                        BuildCompletionContext(false)?.IsReferenceContext == true)
                        ShowCompletion(false);
                }));
            };
            window.CompletionList.CompletionData.Add(item);
        }
        _completion = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_completion, window)) _completion = null; };
        window.Show();
        window.CompletionList.SelectItem(typed);
    }

    private CompletionContext? BuildCompletionContext(bool explicitRequest)
    {
        if (IsReadOnly || CurrentFilePath is null || IsCommentAtCaret()) return null;
        var lookBehind = Math.Max(0, CaretOffset - 2048);
        var preceding = Document.GetText(lookBehind, CaretOffset - lookBehind);
        var reference = ReferencePrefix.Match(preceding);
        if (reference.Success && (IsEscaped(lookBehind + reference.Index) || IsCommentAtOffset(lookBehind + reference.Index))) return null;
        var items = new List<LatexCompletion>();
        int startOffset;
        if (reference.Success)
        {
            var keys = reference.Groups["keys"];
            var suffixStart = keys.Value.LastIndexOf(',') + 1;
            while (suffixStart < keys.Length && char.IsWhiteSpace(keys.Value[suffixStart])) suffixStart++;
            startOffset = lookBehind + keys.Index + suffixStart;
            var liveFiles = _files.Select(f => string.Equals(f.Path, CurrentFilePath, StringComparison.OrdinalIgnoreCase)
                ? f with { Content = Text } : f);
            var index = LatexIndexer.Build(liveFiles);
            var entries = reference.Groups["command"].Value.Contains("cite", StringComparison.OrdinalIgnoreCase)
                ? index.Citations : index.Labels;
            var alreadyChosen = keys.Value.Split(',').SkipLast(1).Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries.GroupBy(x => x.Title).Select(g => g.First()).Where(x => !alreadyChosen.Contains(x.Title)))
                items.Add(new LatexCompletion(entry.Title, entry.Title, $"{entry.Detail}\n{entry.Location}", isReference: true));
        }
        else
        {
            var command = CommandPrefix.Match(preceding);
            if (!command.Success && !explicitRequest) return null;
            if (command.Success && IsEscaped(lookBehind + command.Index)) return null;
            startOffset = command.Success ? lookBehind + command.Groups["name"].Index : CaretOffset;
            foreach (var item in CommandCompletions)
                items.Add(new LatexCompletion(item.Name, (command.Success ? "" : "\\") + item.Snippet, item.Description));
        }
        if (items.Count == 0) return null;
        var typed = Document.GetText(startOffset, CaretOffset - startOffset);
        if (!explicitRequest && !items.Any(item => item.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))) return null;
        return new(startOffset, items, typed, reference.Success);
    }

    private void CloseCompletion() { _completion?.Close(); _completion = null; }

    private bool IsEscaped(int offset)
    {
        var slashCount = 0;
        for (var i = offset - 1; i >= 0 && Document.GetCharAt(i) == '\\'; i--) slashCount++;
        return slashCount % 2 != 0;
    }

    private bool IsCommentAtCaret() => IsCommentAtOffset(CaretOffset);

    private bool IsCommentAtOffset(int offset)
    {
        var line = Document.GetLineByOffset(offset);
        for (var i = line.Offset; i < offset; i++)
            if (Document.GetCharAt(i) == '%' && !IsEscaped(i)) return true;
        return false;
    }

    private (int First, int Last) SelectedLines()
    {
        var first = Document.GetLineByOffset(SelectionStart).LineNumber;
        var end = SelectionStart + SelectionLength;
        var last = Document.GetLineByOffset(end).LineNumber;
        if (SelectionLength > 0 && end == Document.GetLineByNumber(last).Offset) last--;
        return (first, Math.Max(first, last));
    }

    private void ToggleComment()
    {
        var (first, last) = SelectedLines();
        var lines = Enumerable.Range(first, last - first + 1).Select(Document.GetLineByNumber).ToArray();
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(Document.GetText(l))).ToArray();
        var uncomment = nonEmpty.Length > 0 && nonEmpty.All(l => Document.GetText(l).TrimStart().StartsWith('%'));
        using (Document.RunUpdate())
        {
            foreach (var line in lines.Reverse())
            {
                var text = Document.GetText(line);
                var indent = text.Length - text.TrimStart(' ', '\t').Length;
                if (uncomment && string.IsNullOrWhiteSpace(text)) continue;
                if (uncomment)
                {
                    var count = indent + 1 < text.Length && text[indent + 1] == ' ' ? 2 : 1;
                    Document.Remove(line.Offset + indent, count);
                }
                else Document.Insert(line.Offset + indent, "% ");
            }
        }
    }

    private void IndentSelection(bool outdent)
    {
        var (first, last) = SelectedLines();
        using (Document.RunUpdate())
        {
            for (var n = last; n >= first; n--)
            {
                var line = Document.GetLineByNumber(n);
                if (!outdent) Document.Insert(line.Offset, new string(' ', Options.IndentationSize));
                else
                {
                    var text = Document.GetText(line);
                    var count = text.StartsWith('\t') ? 1 : text.TakeWhile(c => c == ' ').Take(Options.IndentationSize).Count();
                    if (count > 0) Document.Remove(line.Offset, count);
                }
            }
        }
    }

    private void FormatDocument()
    {
        var line = TextArea.Caret.Line;
        var column = TextArea.Caret.Column;
        var formatted = LatexFormatter.Format(Text);
        if (formatted == Text) return;
        using (Document.RunUpdate()) Document.Replace(0, Document.TextLength, formatted);
        var target = Document.GetLineByNumber(Math.Clamp(line, 1, Document.LineCount));
        CaretOffset = target.Offset + Math.Clamp(column - 1, 0, target.Length);
        TextArea.Caret.BringCaretToView();
    }

    private static SolidColorBrush Brush(string key) => TeXeed.App.Services.ThemeManager.Brush(key);

    private static IHighlightingDefinition LoadHighlighting(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private sealed class FileSession(TextDocument document)
    {
        public TextDocument Document { get; } = document;
        public int CaretOffset { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
    }

    private sealed class LatexCompletion(string text, string snippet, string description, bool isReference = false) : ICompletionData
    {
        public event Action? Inserted;
        public ImageSource? Image => null;
        public string Text => text;
        public object Content => new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Code, Consolas"), Margin = new Thickness(3) };
        public object Description => description;
        public double Priority => isReference ? 1 : 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // Completion segments use moving anchors; insertion into an empty argument moves them.
            var startOffset = completionSegment.Offset;
            var cursor = snippet.IndexOf('¦');
            var value = snippet.Replace("¦", "");
            var line = textArea.Document.GetLineByOffset(completionSegment.Offset);
            var leading = new string(textArea.Document.GetText(line).TakeWhile(c => c is ' ' or '\t').ToArray());
            if (value.Contains('\n'))
            {
                // Preserve the surrounding indentation on every line of an environment snippet.
                var newline = textArea.Document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                if (cursor >= 0) cursor += snippet[..cursor].Count(c => c == '\n') * (leading.Length + newline.Length - 1);
                value = value.Replace("\n", newline + leading);
            }
            textArea.Document.Replace(completionSegment, value);
            textArea.Caret.Offset = startOffset + (cursor >= 0 ? cursor : value.Length);
            if (!isReference) Inserted?.Invoke();
        }
    }

    private sealed record CompletionContext(int StartOffset, List<LatexCompletion> Items, string Typed, bool IsReferenceContext);

    private sealed class LatexIndentationStrategy : DefaultIndentationStrategy
    {
        public override void IndentLine(TextDocument document, DocumentLine line)
        {
            if (line.PreviousLine is not { } previous) return;
            var previousText = document.GetText(previous);
            var indent = new string(previousText.TakeWhile(c => c is ' ' or '\t').ToArray());
            var previousCode = Regex.Replace(previousText, @"(?<!\\)%.*$", "").TrimEnd();
            if (Regex.IsMatch(previousCode, @"\\begin\{[^}]+\}(?:\[[^\]]*\])?\s*$") || previousCode.EndsWith('{')) indent += "  ";
            var current = document.GetText(line);
            if ((current.TrimStart().StartsWith(@"\end{") || current.TrimStart().StartsWith('}')) && indent.Length >= 2)
                indent = indent[..^2];
            var existing = current.TakeWhile(c => c is ' ' or '\t').Count();
            document.Replace(line.Offset, existing, indent);
        }
    }

    private static readonly (string Name, string Snippet, string Description)[] CommandCompletions =
    [
        ("section", "section{¦}", "Section heading · 章节"),
        ("subsection", "subsection{¦}", "Subsection heading · 小节"),
        ("subsubsection", "subsubsection{¦}", "Subsubsection heading"),
        ("chapter", "chapter{¦}", "Chapter heading · 章"),
        ("paragraph", "paragraph{¦}", "Paragraph heading"),
        ("textbf", "textbf{¦}", "Bold text · 粗体"),
        ("textit", "textit{¦}", "Italic text · 斜体"),
        ("emph", "emph{¦}", "Emphasized text · 强调"),
        ("texttt", "texttt{¦}", "Monospaced text"),
        ("underline", "underline{¦}", "Underline text"),
        ("label", "label{¦}", "Define a cross-reference label"),
        ("ref", "ref{¦}", "Reference a project label · 引用标签"),
        ("eqref", "eqref{¦}", "Reference an equation"),
        ("autoref", "autoref{¦}", "Reference with automatic type name"),
        ("pageref", "pageref{¦}", "Reference a page number"),
        ("cref", "cref{¦}", "Cleveref reference"),
        ("cite", "cite{¦}", "Cite a bibliography entry · 引用文献"),
        ("citep", "citep{¦}", "Parenthetical citation (natbib)"),
        ("citet", "citet{¦}", "Textual citation (natbib)"),
        ("parencite", "parencite{¦}", "Parenthetical citation (biblatex)"),
        ("textcite", "textcite{¦}", "Textual citation (biblatex)"),
        ("autocite", "autocite{¦}", "Automatic citation (biblatex)"),
        ("nocite", "nocite{¦}", "Include an entry in the bibliography"),
        ("begin", "begin{¦}\n  \n\\end{}", "Environment (enter matching names)"),
        ("end", "end{¦}", "Close an environment"),
        ("itemize", "begin{itemize}\n  \\item ¦\n\\end{itemize}", "Bulleted list · 无序列表"),
        ("enumerate", "begin{enumerate}\n  \\item ¦\n\\end{enumerate}", "Numbered list · 有序列表"),
        ("item", "item ¦", "List item"),
        ("equation", "begin{equation}\n  ¦\n\\end{equation}", "Numbered equation · 公式"),
        ("align", "begin{align}\n  ¦ &= \n\\end{align}", "Aligned equations (amsmath)"),
        ("figure", "begin{figure}[htbp]\n  \\centering\n  \\includegraphics[width=0.8\\linewidth]{¦}\n  \\caption{}\n  \\label{fig:}\n\\end{figure}", "Figure with caption and label"),
        ("table", "begin{table}[htbp]\n  \\centering\n  \\caption{¦}\n  \\begin{tabular}{ll}\n    \\hline\n     & \\\\\n    \\hline\n  \\end{tabular}\n  \\label{tab:}\n\\end{table}", "Table with caption and label"),
        ("frac", "frac{¦}{}", "Fraction · 分数"),
        ("sqrt", "sqrt{¦}", "Square root"),
        ("sum", "sum_{¦}^{}", "Summation"),
        ("int", "int_{¦}^{}", "Integral"),
        ("left", "left(¦\\right)", "Automatically sized delimiters"),
        ("mathrm", "mathrm{¦}", "Upright math text"),
        ("mathbf", "mathbf{¦}", "Bold math text"),
        ("mathbb", "mathbb{¦}", "Blackboard bold"),
        ("text", "text{¦}", "Text in math mode"),
        ("alpha", "alpha", "α · alpha"), ("beta", "beta", "β · beta"),
        ("gamma", "gamma", "γ · gamma"), ("delta", "delta", "δ · delta"),
        ("epsilon", "epsilon", "ϵ · epsilon"), ("theta", "theta", "θ · theta"),
        ("lambda", "lambda", "λ · lambda"), ("mu", "mu", "μ · mu"),
        ("pi", "pi", "π · pi"), ("sigma", "sigma", "σ · sigma"),
        ("phi", "phi", "ϕ · phi"), ("omega", "omega", "ω · omega"),
        ("infty", "infty", "∞ · infinity"), ("times", "times", "× · multiplication"),
        ("cdot", "cdot", "· · dot product"), ("leq", "leq", "≤ · less than or equal"),
        ("geq", "geq", "≥ · greater than or equal"), ("neq", "neq", "≠ · not equal"),
        ("rightarrow", "rightarrow", "→ · right arrow"),
        ("includegraphics", "includegraphics[width=\\linewidth]{¦}", "Include an image (graphicx)"),
        ("caption", "caption{¦}", "Figure or table caption"),
        ("centering", "centering", "Center the current environment"),
        ("input", "input{¦}", "Include a TeX source file"),
        ("include", "include{¦}", "Include a chapter source file"),
        ("usepackage", "usepackage{¦}", "Load a LaTeX package"),
        ("documentclass", "documentclass{¦}", "Document class"),
        ("newcommand", "newcommand{\\¦}{}", "Define a command"),
        ("bibliography", "bibliography{¦}", "BibTeX bibliography source"),
        ("bibliographystyle", "bibliographystyle{¦}", "BibTeX bibliography style"),
        ("addbibresource", "addbibresource{¦}", "Biblatex bibliography source"),
        ("printbibliography", "printbibliography", "Print the bibliography"),
        ("footnote", "footnote{¦}", "Footnote"),
        ("url", "url{¦}", "Web address"),
        ("href", "href{¦}{}", "Hyperlink"),
        ("title", "title{¦}", "Document title"),
        ("author", "author{¦}", "Document author"),
        ("date", "date{¦}", "Document date"),
        ("maketitle", "maketitle", "Print the document title"),
        ("tableofcontents", "tableofcontents", "Generate a table of contents"),
        ("newpage", "newpage", "Start a new page")
    ];

    private const string LatexSyntax = """
        <SyntaxDefinition name="LaTeX" extensions=".tex;.sty;.cls" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Command" foreground="#FD6883" fontWeight="bold" />
          <Color name="Comment" foreground="#938889" fontStyle="italic" />
          <Color name="Math" foreground="#F9CC6C" />
          <Color name="Brace" foreground="#C3B7B8" />
          <Color name="Number" foreground="#A8A9EB" />
          <RuleSet>
            <Span color="Comment"><Begin>(?&lt;!\\)(?:\\\\)*%</Begin></Span>
            <Span color="Comment" multiline="true"><Begin>\\begin\{(?:verbatim|lstlisting|minted)\}</Begin><End>\\end\{(?:verbatim|lstlisting|minted)\}</End></Span>
            <Span color="Math" multiline="true"><Begin>(?&lt;!\\)\$\$</Begin><End>(?&lt;!\\)\$\$</End><RuleSet><Import ruleSet="MathRules" /></RuleSet></Span>
            <Span color="Math" multiline="true"><Begin>(?&lt;!\\)\$</Begin><End>(?&lt;!\\)\$</End><RuleSet><Import ruleSet="MathRules" /></RuleSet></Span>
            <Span color="Math" multiline="true"><Begin>\\\[</Begin><End>\\\]</End><RuleSet><Import ruleSet="MathRules" /></RuleSet></Span>
            <Span color="Math" multiline="true"><Begin>\\\(</Begin><End>\\\)</End><RuleSet><Import ruleSet="MathRules" /></RuleSet></Span>
            <Rule color="Command">\\(?:[a-zA-Z@]+\*?|.)</Rule>
            <Rule color="Brace">[{}\[\]&amp;]</Rule>
            <Rule color="Number">\b[0-9]+(?:\.[0-9]+)?\b</Rule>
          </RuleSet>
          <RuleSet name="MathRules">
            <Rule color="Command">\\(?:[a-zA-Z@]+\*?|.)</Rule>
            <Rule color="Brace">[{}\[\]&amp;_^]</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private const string BibSyntax = """
        <SyntaxDefinition name="BibTeX" extensions=".bib" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Type" foreground="#FD6883" fontWeight="bold" />
          <Color name="Field" foreground="#85DACC" />
          <Color name="String" foreground="#F9CC6C" />
          <Color name="Comment" foreground="#938889" fontStyle="italic" />
          <Color name="Brace" foreground="#C3B7B8" />
          <RuleSet>
            <Span color="Comment"><Begin>%</Begin></Span>
            <Span color="String" multiline="true"><Begin>"</Begin><End>(?&lt;!\\)"</End></Span>
            <Rule color="Type">@[a-zA-Z]+</Rule>
            <Rule color="Field">\b[a-zA-Z_]+(?=\s*=)</Rule>
            <Rule color="Brace">[{}=,]</Rule>
            <Rule color="String">\b[0-9]+\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
