using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    private readonly List<CaretRange> _multiCarets = [];
    private TextDocument? _multiDocument;
    private bool _multiEditing, _multiSelecting;
    private Point? _multiPointerStart;
    private MouseButton _multiPointerButton;
    private bool _multiPointerDragged;
    private CaretRange _multiPointerHit;

    // A rectangular selection may have only one row. Keep it in this mode so virtual
    // space and paired delimiters use the same editing rules as a larger rectangle.
    private bool HasMultipleCarets => _multiCarets.Count != 0;
    public int MultiCaretCount => Math.Max(1, _multiCarets.Count);

    private void InitializeMultiCaret()
    {
        DocumentChanged += (_, _) =>
        {
            ResetMultiCaret();
            if (_multiDocument is not null) _multiDocument.Changing -= MultiDocumentChanging;
            _multiDocument = Document;
            if (_multiDocument is not null) _multiDocument.Changing += MultiDocumentChanging;
        };
        _multiDocument = Document;
        if (_multiDocument is not null) _multiDocument.Changing += MultiDocumentChanging;
        TextArea.SelectionChanged += (_, _) =>
        {
            if (!_multiEditing && !_multiSelecting && HasMultipleCarets
                && TextArea.Selection is not MultiSelection)
                ClearMultiCaretState();
        };
        TextArea.TextView.BackgroundRenderers.Add(new MultiCaretRenderer(this));
        TextArea.PreviewMouseDown += MultiMouseDown;
        TextArea.PreviewMouseMove += MultiMouseMove;
        TextArea.PreviewMouseUp += MultiMouseUp;
        TextArea.LostMouseCapture += (_, _) => _multiPointerStart = null;
        DataObject.AddPastingHandler(TextArea, (_, e) =>
        {
            if (!HasMultipleCarets) return;
            var text = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : e.DataObject.GetData(DataFormats.Text) as string;
            e.CancelCommand();
            if (text is not null) ApplyMultiEdit(MultiEditKind.Paste, text);
        });
        TextArea.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (!HasMultipleCarets || _search.IsKeyboardFocusWithin || e.Command != ApplicationCommands.Cut) return;
            e.Handled = true;
            CutMultiCarets(text =>
            {
                var data = new DataObject(DataFormats.UnicodeText, text);
                var copying = new DataObjectCopyingEventArgs(data, false);
                TextArea.RaiseEvent(copying);
                if (copying.CommandCancelled) return false;
                try { Clipboard.SetDataObject(data, true); return true; }
                catch (ExternalException) { return false; }
            });
        }));
        Unloaded += (_, _) => ResetMultiCaret();
    }

    private void MultiDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        // Undo, replacement, external reloads and other commands cannot leave stale
        // offsets behind. Our own edits install new positions before EndUpdate fires.
        if (!_multiEditing) ResetMultiCaret();
    }

    private void ClearMultiCaretState()
    {
        _multiCarets.Clear();
        _multiPointerStart = null;
        if (TextArea.IsMouseCaptured) TextArea.ReleaseMouseCapture();
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void ResetMultiCaret()
    {
        if (!HasMultipleCarets) return;
        var primary = _multiCarets[^1];
        ClearMultiCaretState();
        _multiSelecting = true;
        try
        {
            if (Document is null) return;
            var start = Math.Clamp(primary.Start, 0, Document.TextLength);
            var end = Math.Clamp(primary.End, start, Document.TextLength);
            TextArea.Caret.Offset = end;
            TextArea.Selection = Selection.Create(TextArea, start, end);
            if (IsKeyboardFocusWithin) TextArea.Caret.Show();
        }
        finally { _multiSelecting = false; }
    }

    private void SetMultiCarets(IEnumerable<CaretRange> ranges)
    {
        var normalized = new List<CaretRange>();
        foreach (var candidate in ranges)
        {
            var range = candidate with
            {
                Start = GraphemeBoundary(candidate.Start, false),
                End = GraphemeBoundary(candidate.End, true),
                Padding = Math.Max(0, candidate.Padding),
                EndPadding = Math.Max(candidate.Padding, candidate.EndPadding)
            };
            // A second click on an existing point selects it as the primary caret.
            normalized.RemoveAll(r => r == range);
            for (var i = normalized.Count - 1; i >= 0; i--)
            {
                var prior = normalized[i];
                if ((range.Start < prior.End && prior.Start < range.End)
                    || (range.Start == prior.Start && range.Padding == prior.Padding)
                    || (prior.Start == prior.End && prior.Start > range.Start && prior.Start < range.End))
                {
                    range = new(Math.Min(range.Start, prior.Start), Math.Max(range.End, prior.End));
                    normalized.RemoveAt(i);
                }
            }
            normalized.Add(range);
        }
        _multiCarets.Clear();
        _multiCarets.AddRange(normalized);
        if (!HasMultipleCarets) return;
        CloseCompletion();
        _multiSelecting = true;
        try
        {
            var primary = _multiCarets[^1];
            TextArea.Caret.Offset = primary.End;
            TextArea.Selection = new MultiSelection(this, _multiCarets.ToArray());
            if (primary.EndPadding > 0) TextArea.Caret.Hide();
            else if (TextArea.IsKeyboardFocused) TextArea.Caret.Show();
        }
        finally { _multiSelecting = false; }
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void AddMultiCaret(int offset, int virtualPadding = 0)
    {
        var existing = HasMultipleCarets ? _multiCarets.ToArray()
            : new[] { new CaretRange(SelectionStart, SelectionStart + SelectionLength) };
        offset = GraphemeBoundary(Math.Clamp(offset, 0, Document.TextLength), false);
        SetMultiCarets(existing.Append(new(offset, offset, virtualPadding, virtualPadding)));
    }

    private bool HandleMultiCaretTextEntering(TextCompositionEventArgs e)
    {
        if (!HasMultipleCarets || _search.IsKeyboardFocusWithin) return false;
        ApplyMultiEdit(MultiEditKind.Type, e.Text);
        e.Handled = true;
        return true;
    }

    private bool HandleMultiCaretKeyDown(KeyEventArgs e)
    {
        if (!HasMultipleCarets || _search.IsKeyboardFocusWithin) return false;
        var handled = ProcessMultiCaretKey(e.Key, Keyboard.Modifiers);
        if (handled) e.Handled = true;
        return handled;
    }

    private bool ProcessMultiCaretKey(Key key, ModifierKeys modifiers)
    {
        if (!HasMultipleCarets) return false;
        if (key == Key.Escape) { ResetMultiCaret(); return true; }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (key == Key.Space) return true; // Completion windows describe one caret only.
            if (key is Key.C or Key.V or Key.X or Key.S) return false;
            ResetMultiCaret();
            return false;
        }
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        switch (key)
        {
            case Key.Back: ApplyMultiEdit(MultiEditKind.Backspace); return true;
            case Key.Delete: ApplyMultiEdit(MultiEditKind.Delete); return true;
            case Key.Return: ApplyMultiEdit(MultiEditKind.Enter); return true;
            case Key.Tab:
                ApplyMultiEdit((modifiers & ModifierKeys.Shift) != 0 ? MultiEditKind.Unindent : MultiEditKind.Tab);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End:
                if ((modifiers & ModifierKeys.Shift) == 0) { MoveMultiCarets(key); return true; }
                ResetMultiCaret();
                return false;
            case Key.PageUp or Key.PageDown:
                ResetMultiCaret();
                return false;
            default: return false;
        }
    }

    private void MoveMultiCarets(Key key)
    {
        var view = TextArea.TextView;
        SetMultiCarets(_multiCarets.Select(range =>
        {
            var selected = range.End > range.Start || range.EndPadding > range.Padding;
            var offset = key == Key.Left && selected ? range.Start : range.End;
            var padding = key == Key.Left ? range.Padding : range.EndPadding;
            if (key == Key.Left)
            {
                if (!selected && padding > 0) padding--;
                else if (!selected) offset = PreviousGrapheme(offset);
            }
            else if (key == Key.Right)
            {
                if (!selected && padding > 0) padding++;
                else if (!selected) offset = NextGrapheme(offset);
            }
            else if (key is Key.Home or Key.End)
            {
                var line = Document.GetLineByOffset(offset);
                offset = key == Key.Home ? line.Offset : line.EndOffset;
                padding = 0;
            }
            else if (view.VisualLinesValid && view.ActualHeight > 0)
            {
                var point = view.GetVisualPosition(new TextViewPosition(Document.GetLocation(offset)), VisualYPosition.LineMiddle);
                point.X += padding * GridCellWidth;
                point.Y += key == Key.Up ? -view.DefaultLineHeight : view.DefaultLineHeight;
                return HitMultiCaret(point);
            }
            else
            {
                var line = Document.GetLineByOffset(offset);
                var targetLine = Math.Clamp(line.LineNumber + (key == Key.Up ? -1 : 1), 1, Document.LineCount);
                var column = GetGridColumn(offset) + padding;
                offset = GetOffsetAtGridColumn(targetLine, column);
                padding = Math.Max(0, column - GetGridColumn(offset));
            }
            return new CaretRange(offset, offset, padding, padding);
        }).ToArray());
        BringPrimaryMultiCaretToView();
    }

    private void BringPrimaryMultiCaretToView()
    {
        var view = TextArea.TextView;
        if (!HasMultipleCarets || view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        var primary = _multiCarets[^1];
        var position = new TextViewPosition(Document.GetLocation(primary.End));
        var top = view.GetVisualPosition(position, VisualYPosition.TextTop);
        var bottom = view.GetVisualPosition(position, VisualYPosition.TextBottom);
        top.X += primary.EndPadding * GridCellWidth;
        view.MakeVisible(new Rect(top, new Size(2, Math.Max(1, bottom.Y - top.Y))));
    }

    private void MultiMouseDown(object sender, MouseButtonEventArgs e)
    {
        var view = TextArea.TextView;
        var point = e.GetPosition(view);
        // Search controls and margins share TextArea's routed-event path.
        if (point.X < 0 || point.Y < 0 || point.X > view.ActualWidth || point.Y > view.ActualHeight
            || e.OriginalSource is not DependencyObject source || !IsInsideTextView(source)) return;
        if (BeginMultiPointer(e.ChangedButton, Keyboard.Modifiers, point))
        {
            TextArea.Focus();
            TextArea.CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left) ResetMultiCaret();
    }

    private bool IsInsideTextView(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, TextArea.TextView)) return true;
        return false;
    }

    private bool BeginMultiPointer(MouseButton button, ModifierKeys modifiers, Point point)
    {
        if (button == MouseButton.Left && modifiers != ModifierKeys.Alt)
        {
            ResetMultiCaret();
            return false;
        }
        if (_changingDocument || Document is null || CurrentFilePath is null
            || !(button == MouseButton.Middle || button == MouseButton.Left && modifiers == ModifierKeys.Alt)) return false;
        var view = TextArea.TextView;
        view.EnsureVisualLines();
        _multiPointerStart = point + view.ScrollOffset;
        _multiPointerButton = button;
        _multiPointerDragged = false;
        _multiPointerHit = HitMultiCaret(_multiPointerStart.Value);
        CloseCompletion();
        return true;
    }

    private void MultiMouseMove(object sender, MouseEventArgs e)
    {
        if (_multiPointerStart is null) return;
        MoveMultiPointer(e.GetPosition(TextArea.TextView));
        e.Handled = true;
    }

    private void MoveMultiPointer(Point point)
    {
        if (_multiPointerStart is not { } start) return;
        var view = TextArea.TextView;
        var end = point + view.ScrollOffset;
        if (!_multiPointerDragged && Math.Abs(start.X - end.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(start.Y - end.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _multiPointerDragged = true;
        SetMultiRectangle(start, end);
    }

    private void MultiMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_multiPointerStart is null || e.ChangedButton != _multiPointerButton) return;
        EndMultiPointer(e.GetPosition(TextArea.TextView));
        if (TextArea.IsMouseCaptured) TextArea.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EndMultiPointer(Point point)
    {
        if (_multiPointerStart is null) return;
        MoveMultiPointer(point);
        if (!_multiPointerDragged)
        {
            if (_multiPointerButton == MouseButton.Left)
                AddMultiCaret(_multiPointerHit.Start, _multiPointerHit.Padding);
            else SetMultiCarets([_multiPointerHit]);
        }
        _multiPointerStart = null;
    }

    private CaretRange HitMultiCaret(Point documentPoint)
    {
        var view = TextArea.TextView;
        var y = Math.Clamp(documentPoint.Y, 0, Math.Max(0, view.DocumentHeight - 0.001));
        var line = view.GetDocumentLineByVisualTop(y);
        var visual = view.GetOrConstructVisualLine(line);
        var column = visual.GetVisualColumn(new Point(Math.Max(0, documentPoint.X), y), true);
        var offset = Math.Clamp(visual.FirstDocumentLine.Offset + visual.GetRelativeOffset(column), 0, Document.TextLength);
        offset = GraphemeBoundary(offset, false);
        var end = visual.LastDocumentLine.EndOffset;
        var endPosition = view.GetVisualPosition(new TextViewPosition(Document.GetLocation(end)), VisualYPosition.TextTop);
        var pad = offset == end && documentPoint.X > endPosition.X
            ? (int)Math.Round((documentPoint.X - endPosition.X) / GridCellWidth, MidpointRounding.AwayFromZero) : 0;
        return new(offset, offset, pad, pad);
    }

    private void SetMultiRectangle(Point start, Point end)
    {
        var view = TextArea.TextView;
        var first = HitMultiCaret(start);
        var last = HitMultiCaret(end);
        var firstLine = Document.GetLineByOffset(Math.Min(first.Start, last.Start));
        var lastLine = Document.GetLineByOffset(Math.Max(first.Start, last.Start));
        var left = Math.Max(0, Math.Min(start.X, end.X));
        var right = Math.Max(0, Math.Max(start.X, end.X));
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);
        var ranges = new List<CaretRange>();
        for (var line = firstLine; line is not null && line.LineNumber <= lastLine.LineNumber;)
        {
            var visual = view.GetOrConstructVisualLine(line);
            // A wrapped paragraph has several selectable display rows. Iterate the
            // formatter's actual rows so dragging three rows never selects a whole
            // logical paragraph or assumes a fixed line height.
            foreach (var row in visual.TextLines)
            {
                var rowTop = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineTop);
                var rowBottom = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineBottom);
                if (rowBottom <= top || rowTop > bottom) continue;
                var y = (rowTop + rowBottom) / 2;
                var a = HitMultiCaret(new(left, y));
                var b = HitMultiCaret(new(right, y));
                ranges.Add(new(Math.Min(a.Start, b.Start), Math.Max(a.Start, b.Start), a.Padding, b.Padding));
            }
            line = visual.LastDocumentLine.NextLine;
        }
        if (end.Y < start.Y) ranges.Reverse();
        SetMultiCarets(ranges);
    }

    private enum MultiEditKind { Type, Paste, Backspace, Delete, Enter, Tab, Unindent }
    private readonly record struct CaretRange(int Start, int End, int Padding = 0, int EndPadding = 0);
    private sealed record PlannedEdit(int Index, int Start, int End, int Padding, string? Text,
        int SelectionOffset = 0, int SelectionLength = 0, int ResultPadding = 0);

    private void CutMultiCarets(Func<string, bool> copy)
    {
        if (!HasMultipleCarets || IsReadOnly || _changingDocument) return;
        var document = Document;
        var version = document.Version;
        var ranges = _multiCarets.ToArray();
        var wholeLines = ranges.All(r => r.Start == r.End && r.Padding == r.EndPadding);
        if (wholeLines)
        {
            if (!Options.CutCopyWholeLine) return;
            ranges = ranges.Select(r => Document.GetLineByOffset(r.Start)).Distinct()
                .Select(line => new CaretRange(line.Offset, line.Offset + line.TotalLength)).ToArray();
        }
        if (ranges.Select((r, i) => new PlannedEdit(i, r.Start, r.End, 0, "")).Any(p => !CanApplyMultiEdit(p))) return;
        var text = string.Join(wholeLines ? "" : Environment.NewLine,
            ranges.OrderBy(r => r.Start).Select(r => document.GetText(r.Start, r.End - r.Start)));
        if (text.Length == 0 || !copy(text)) return;
        // Clipboard event handlers may synchronously reload or switch documents.
        if (!ReferenceEquals(document, Document) || version.CompareAge(Document.Version) != 0) return;
        SetMultiCarets(ranges);
        ApplyMultiEdit(MultiEditKind.Paste);
    }

    private void ApplyMultiEdit(MultiEditKind kind, string text = "")
    {
        if (!HasMultipleCarets || _changingDocument || IsReadOnly || CurrentFilePath is null) return;
        var document = Document;
        var plans = _multiCarets.Select((range, index) => PlanMultiEdit(range, index, kind, text)).ToList();
        // Overlapping deletions can arise from adjacent carets (e.g. paired braces).
        // Merge them before changing the document rather than deleting any byte twice.
        foreach (var plan in plans.OrderBy(p => p.Start).ToArray())
        {
            if (!plans.Contains(plan) || plan.Text is not "" || plan.End == plan.Start) continue;
            var overlaps = plans.Where(p => p != plan && p.Text == "" && p.End > plan.Start && p.Start < plan.End).ToArray();
            if (overlaps.Length == 0) continue;
            plans.Remove(plan);
            foreach (var other in overlaps) plans.Remove(other);
            plans.Add(plan with { Start = Math.Min(plan.Start, overlaps.Min(p => p.Start)),
                End = Math.Max(plan.End, overlaps.Max(p => p.End)), Index = Math.Max(plan.Index, overlaps.Max(p => p.Index)) });
        }
        if (plans.Any(p => !CanApplyMultiEdit(p))) return;
        var boundaries = plans.Select(p => (Plan: p, Start: MultiAnchor(document, p.Start), End: MultiAnchor(document, p.End))).ToArray();
        var results = new List<(int Index, TextAnchor Start, TextAnchor End, int Padding)>();
        _multiEditing = true;
        try
        {
            using (document.RunUpdate())
            {
                // Materialize a short line's virtual space once, even when several
                // independent carets on that line need different virtual columns.
                foreach (var padding in plans.Where(p => p.Text is { Length: > 0 } && p.Padding > 0)
                    .GroupBy(p => p.Start).OrderByDescending(g => g.Key))
                    document.Insert(padding.Key, new string(' ', padding.Max(p => p.Padding)));
                foreach (var item in boundaries.OrderByDescending(p => p.Start.Offset + (p.Plan.Text is { Length: > 0 } ? p.Plan.Padding : 0)))
                {
                    var plan = item.Plan;
                    var pad = plan.Text is { Length: > 0 } ? plan.Padding : 0;
                    var startOffset = item.Start.Offset + pad;
                    var endOffset = item.End.Offset + pad;
                    if (plan.Text is not null && (plan.Text.Length > 0 || endOffset > startOffset))
                        document.Replace(startOffset, endOffset - startOffset, plan.Text);
                    var selectionStart = startOffset + plan.SelectionOffset;
                    results.Add((plan.Index, MultiAnchor(document, selectionStart, true),
                        MultiAnchor(document, selectionStart + plan.SelectionLength, true), plan.ResultPadding));
                }
                SetMultiCarets(results.OrderBy(r => r.Index).Select(r => new CaretRange(r.Start.Offset, r.End.Offset, r.Padding, r.Padding)));
                BringPrimaryMultiCaretToView();
            }
            // EndUpdate notifies consumers, which may synchronously switch files.
            // Do not restore positions or access the old document after that event.
        }
        finally { _multiEditing = false; }
    }

    private bool CanApplyMultiEdit(PlannedEdit plan)
    {
        var provider = TextArea.ReadOnlySectionProvider;
        if (plan.Text is { Length: > 0 } && !provider.CanInsert(plan.Start)) return false;
        if (plan.Text is null || plan.End == plan.Start) return true;
        var allowed = provider.GetDeletableSegments(new TextSegment { StartOffset = plan.Start, Length = plan.End - plan.Start }).ToArray();
        return allowed.Length == 1 && allowed[0].Offset == plan.Start && allowed[0].EndOffset == plan.End;
    }

    private PlannedEdit PlanMultiEdit(CaretRange range, int index, MultiEditKind kind, string text)
    {
        var start = range.Start;
        var end = range.End;
        var hasSelection = start != end || range.EndPadding > range.Padding;
        PlannedEdit Keep(int offset = 0, int? padding = null) => new(index, start, end, range.Padding, null,
            offset, 0, padding ?? range.Padding);
        if (kind is MultiEditKind.Backspace or MultiEditKind.Delete or MultiEditKind.Unindent)
        {
            if (hasSelection) return new(index, start, end, range.Padding, "", ResultPadding: range.Padding);
            if (range.Padding > 0) return Keep(padding: kind == MultiEditKind.Delete ? range.Padding : Math.Max(0, range.Padding - 1));
            if (kind == MultiEditKind.Backspace)
            {
                if (start == 0) return Keep();
                if (start < Document.TextLength && (Document.GetCharAt(start - 1), Document.GetCharAt(start)) is ('{', '}') or ('[', ']'))
                    return new(index, start - 1, start + 1, 0, "");
                return new(index, PreviousGrapheme(start), start, 0, "");
            }
            if (kind == MultiEditKind.Delete)
                return start == Document.TextLength ? Keep() : new(index, start, NextGrapheme(start), 0, "");
            var lineStart = Document.GetLineByOffset(start).Offset;
            var remove = 0;
            while (start - remove > lineStart && remove < Math.Max(1, Options.IndentationSize)
                && Document.GetCharAt(start - remove - 1) == ' ') remove++;
            if (remove == 0 && start > lineStart && Document.GetCharAt(start - 1) == '\t') remove = 1;
            return new(index, start - remove, start, 0, "");
        }
        if (kind == MultiEditKind.Enter)
        {
            var line = Document.GetLineByOffset(start);
            var prefix = Document.GetText(line.Offset, start - line.Offset);
            var indent = new string(prefix.TakeWhile(c => c is ' ' or '\t').ToArray());
            var delimiter = line.DelimiterLength > 0 ? Document.GetText(line.EndOffset, line.DelimiterLength)
                : Document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            text = delimiter + indent;
        }
        else if (kind == MultiEditKind.Tab)
        {
            var size = Math.Max(1, Options.IndentationSize);
            text = Options.ConvertTabsToSpaces ? new string(' ', size - (GetGridColumn(start) + range.Padding) % size) : "\t";
        }
        else if (kind == MultiEditKind.Type && text.Length == 1 && !IsEscaped(start) && !IsCommentAtOffset(start))
        {
            if (range.Padding == 0 && !hasSelection && text[0] is '}' or ']'
                && start < Document.TextLength && Document.GetCharAt(start) == text[0]) return Keep(1, 0);
            if (text[0] is '{' or '[')
            {
                var selected = Document.GetText(start, end - start);
                var closing = text[0] == '{' ? '}' : ']';
                return new(index, start, end, range.Padding, text + selected + closing, 1, selected.Length);
            }
        }
        return new(index, start, end, range.Padding, text, text.Length);
    }

    private static TextAnchor MultiAnchor(TextDocument document, int offset, bool afterInsertion = false)
    {
        var anchor = document.CreateAnchor(offset);
        anchor.MovementType = afterInsertion ? AnchorMovementType.AfterInsertion : AnchorMovementType.BeforeInsertion;
        anchor.SurviveDeletion = true;
        return anchor;
    }

    private int GraphemeBoundary(int offset, bool ceiling)
    {
        offset = Math.Clamp(offset, 0, Document.TextLength);
        if (offset == 0 || offset == Document.TextLength) return offset;
        var line = Document.GetLineByOffset(offset);
        // Treat CRLF as one unit; no caret or delete operation may split it.
        if (offset > line.EndOffset) return ceiling ? line.Offset + line.TotalLength : line.EndOffset;
        if (offset == line.EndOffset) return offset;
        var boundaries = StringInfo.ParseCombiningCharacters(Document.GetText(line.Offset, line.Length));
        var index = Array.BinarySearch(boundaries, offset - line.Offset);
        if (index >= 0) return offset;
        index = ~index;
        return ceiling ? index < boundaries.Length ? line.Offset + boundaries[index] : line.EndOffset
            : index == 0 ? line.Offset : line.Offset + boundaries[index - 1];
    }

    private int PreviousGrapheme(int offset) => GraphemeBoundary(Math.Max(0, offset - 1), false);
    private int NextGrapheme(int offset) => GraphemeBoundary(Math.Min(Document.TextLength, offset + 1), true);

    private sealed class MultiSelection(LatexEditor editor, CaretRange[] ranges) : Selection(editor.TextArea)
    {
        public override TextViewPosition StartPosition => new(editor.Document.GetLocation(ranges[^1].Start));
        public override TextViewPosition EndPosition => new(editor.Document.GetLocation(ranges[^1].End));
        public override IEnumerable<SelectionSegment> Segments => ranges.OrderBy(r => r.Start).Select(r => new SelectionSegment(r.Start, r.End));
        public override ISegment SurroundingSegment => new TextSegment { StartOffset = ranges.Min(r => r.Start), Length = ranges.Max(r => r.End) - ranges.Min(r => r.Start) };
        public override int Length => ranges.Sum(r => r.End - r.Start);
        public override bool EnableVirtualSpace => true;
        public override string GetText() => string.Join(Environment.NewLine, ranges.OrderBy(r => r.Start).Select(r => editor.Document.GetText(r.Start, r.End - r.Start)));
        public override void ReplaceSelectionWithText(string newText) => editor.ApplyMultiEdit(MultiEditKind.Paste, newText);
        public override Selection UpdateOnDocumentChange(DocumentChangeEventArgs e) => new MultiSelection(editor,
            ranges.Select(r => r with { Start = e.GetNewOffset(r.Start, AnchorMovementType.BeforeInsertion),
                End = e.GetNewOffset(r.End, AnchorMovementType.AfterInsertion) }).ToArray());
        public override Selection SetEndpoint(TextViewPosition endPosition) => Selection.Create(editor.TextArea,
            ranges[^1].Start, editor.Document.GetOffset(endPosition.Location));
        public override Selection StartSelectionOrSetEndpoint(TextViewPosition startPosition, TextViewPosition endPosition)
            => Selection.Create(editor.TextArea, editor.Document.GetOffset(startPosition.Location), editor.Document.GetOffset(endPosition.Location));
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    private sealed class MultiCaretRenderer(LatexEditor editor) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!editor.HasMultipleCarets || !textView.VisualLinesValid) return;
            var caretBrush = editor.Foreground;
            var pen = new Pen(caretBrush, 1.25);
            foreach (var range in editor._multiCarets)
            {
                var line = editor.Document.GetLineByOffset(range.End);
                if (!textView.VisualLines.Any(v => line.LineNumber >= v.FirstDocumentLine.LineNumber && line.LineNumber <= v.LastDocumentLine.LineNumber)) continue;
                var position = new TextViewPosition(editor.Document.GetLocation(range.End));
                var top = textView.GetVisualPosition(position, VisualYPosition.TextTop) - textView.ScrollOffset;
                var bottom = textView.GetVisualPosition(position, VisualYPosition.TextBottom) - textView.ScrollOffset;
                if (range.EndPadding > 0)
                {
                    var x = top.X + range.Padding * editor.GridCellWidth;
                    var width = (range.EndPadding - range.Padding) * editor.GridCellWidth;
                    if (width > 0) drawingContext.DrawRectangle(editor.TextArea.SelectionBrush, null,
                        new Rect(x, top.Y, width, Math.Max(1, bottom.Y - top.Y)));
                }
                if (range.Start == range.End && range.Padding == range.EndPadding)
                {
                    top.X += range.Padding * editor.GridCellWidth;
                    bottom.X = top.X;
                    drawingContext.DrawLine(pen, top, bottom);
                }
            }
        }
    }
}
