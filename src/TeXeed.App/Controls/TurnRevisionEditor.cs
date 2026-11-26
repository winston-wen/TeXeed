using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal sealed class TurnRevisionEditor : StackPanel
{
    private readonly AiRevisionDraft _original;
    private readonly int _number;
    private readonly string? _imageBaseDirectory;
    private readonly (AiPanel Panel, AiTurn Source)? _equations;
    private readonly Action<AiRevisionDraft> _saveDraft;
    private readonly Func<IReadOnlyList<AiRevisionRow>, Task> _finish;
    private readonly Dictionary<string, Dictionary<string, string>> _equationLabels = [];
    private readonly Dictionary<string, int> _precedingDisplays = [];
    private List<AiRevisionRow> _rows;
    private readonly Stack<(List<AiRevisionRow> Rows, string Id, int Caret)> _undo = new();
    private readonly HashSet<string> _selected = [];
    private readonly Dictionary<string, RevisionLineEditor> _lines = [];
    private sealed record RowView(AiRevisionRow Row, bool Selecting, FrameworkElement Container, TextBlock? Marker, RevisionLineHost? Host);
    private Dictionary<string, RowView> _displayed = [];
    private readonly StackPanel _passages = new();
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _selecting, _painting, _busy;
    private string _active = "";
    private int _lastCaret;
    private Window? _window;

    internal TurnRevisionEditor(AiRevisionDraft draft, int number, string? imageBaseDirectory, Action<AiRevisionDraft> saveDraft,
        Func<IReadOnlyList<AiRevisionRow>, Task> finish, Func<Task> cancel,
        (AiPanel Panel, AiTurn Source)? equations = null)
    {
        _original = draft; _rows = [.. draft.Rows]; _number = number; _imageBaseDirectory = imageBaseDirectory;
        _saveDraft = saveDraft; _finish = finish;
        _equations = equations;
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, false);
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window == null) return;
            _window.PreviewMouseDown += WindowMouseDown;
            _window.Deactivated += WindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            if (_window == null) return;
            _window.PreviewMouseDown -= WindowMouseDown;
            _window.Deactivated -= WindowDeactivated;
            _window = null;
        };
        IndexEquations();
        ContextMenu = new ContextMenu();
        ContextMenu.Opened += (_, _) => BuildMenu(cancel);
        Children.Add(_notice); Children.Add(_passages);
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(async (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (!e.IsRepeat) await RunAsync(() => _finish(KeptRows()));
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (!_busy) Undo();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_selecting && !_busy) ToggleSelection();
                Focus();
            }
        }));
        EnsureRow(); _active = _rows.FirstOrDefault(r => !r.Locked)?.Id ?? ""; Paint();
    }

    private void WindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Non-focusable whitespace normally leaves WPF's keyboard focus on the
        // previous editor. Capture the active host before the click is dispatched.
        if (e.ChangedButton != MouseButton.Left || !IsKeyboardFocusWithin ||
            _displayed.GetValueOrDefault(_active)?.Host is not { IsKeyboardFocusWithin: true } host || host.IsMouseOver) return;
        if (_passages.IsMouseOver && _displayed.Values.Any(view => view.Host?.IsMouseOver == true)) return;
        Focus();
    }

    private void WindowDeactivated(object? sender, EventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), this);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        var source = _lines.GetValueOrDefault(_active);
        int? caret = source?.IsKeyboardFocusWithin == true ? source.CaretOffset : null;
        // Keep this editor as the keyboard target while validation or disk I/O is pending.
        // Repeated shortcuts still belong to this turn, even after its source loses focus.
        if (IsKeyboardFocusWithin) Focus();
        _passages.IsEnabled = false;
        _notice.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception ex)
        {
            _notice.Text = ex.Message; _notice.Visibility = Visibility.Visible;
            _passages.IsEnabled = true;
            if (caret is { } at && IsKeyboardFocused) _displayed.GetValueOrDefault(_active)?.Host?.FocusSource(at);
        }
        finally { _passages.IsEnabled = true; _busy = false; }
    }
    internal void FocusFirstParagraph() => _displayed.Values.FirstOrDefault(view => view.Host != null)?.Host?.FocusSource(0);

    internal (string RowId, string SegmentId, double Y)? ActiveReadingAnchor(FrameworkElement viewport)
    {
        if (!_displayed.TryGetValue(_active, out var view) || !view.Container.IsVisible ||
            !viewport.IsAncestorOf(view.Container)) return null;
        var y = view.Container.TranslatePoint(new Point(), viewport).Y;
        return y < viewport.ActualHeight && y + view.Container.ActualHeight > 0
            ? (view.Row.Id, view.Row.SegmentId, y) : null;
    }

    internal FrameworkElement? AtReadingPosition(string segmentId, string text, int offset, bool activate = false, string? rowId = null)
    {
        var position = 0;
        var best = int.MaxValue;
        RowView? match = rowId == null ? null : _displayed.GetValueOrDefault(rowId);
        if (match == null)
        {
            foreach (var row in _rows.Where(row => row.SegmentId == segmentId))
            {
                var visible = row.Text;
                if (!visible.Contains(text, StringComparison.Ordinal) && !text.Contains(visible, StringComparison.Ordinal))
                    visible = AiRevisionText.SelectionText(row.Text).Text;
                if (visible.Length > 0 && (visible.Contains(text, StringComparison.Ordinal) || text.Contains(visible, StringComparison.Ordinal)))
                {
                    var distance = Math.Abs(position - offset);
                    if (distance < best) { best = distance; match = _displayed[row.Id]; }
                }
                position += row.Text.Length + 2;
            }
        }
        if (activate && match != null) FocusRow(match, 0);
        return match?.Container;
    }
    private int Index(string id) => _rows.FindIndex(row => row.Id == id);
    private static AiRevisionRow NewRow(string segment) => new(Guid.NewGuid().ToString("N"), segment, "", 0, false, []);
    private void EnsureRow()
    {
        if (_rows.Count == 0) _rows.Add(NewRow(_original.Rows.FirstOrDefault()?.SegmentId ?? _original.TurnId + "-final"));
    }
    private List<AiRevisionRow> KeptRows() => _rows.Where(row => row.Locked || row.Links.Count > 0 || !string.IsNullOrWhiteSpace(row.Text)).ToList();
    private void Remember(string id, int caret) => _undo.Push((new List<AiRevisionRow>(_rows), id, caret));
    private void Changed()
    {
        _notice.Visibility = Visibility.Collapsed;
        _saveDraft(_original with { Rows = [.. _rows] });
    }
    private void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        _rows = previous.Rows; _selecting = false; _selected.Clear();
        Changed(); Paint(previous.Id, previous.Caret);
    }
    private void BuildMenu(Func<Task> cancel)
    {
        var menu = ContextMenu;
        menu.Items.Clear();
        if (_busy) return;
        // All rows share one menu. WPF sets PlacementTarget to the right-clicked row
        // even when the insertion caret is in a different paragraph.
        var target = menu.PlacementTarget as FrameworkElement;
        var id = target?.Tag as string ?? _active;
        var index = Index(id);
        MenuItem Add(string title, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = title };
            item.Click += action;
            menu.Items.Add(item);
            return item;
        }
        if (!_selecting && index >= 0 && !_rows[index].Locked)
        {
            var levels = new MenuItem { Header = "段落类型" };
            for (var i = 0; i <= 6; i++)
            {
                var level = i;
                var item = new MenuItem { Header = level == 0 ? "正文" : $"{level} 级标题", IsChecked = _rows[index].Level == level };
                item.Click += (_, _) => ChangeLevel(id, level);
                levels.Items.Add(item);
            }
            menu.Items.Add(levels);
        }
        Add(_selecting ? "取消选择" : "选择段落", (_, _) => ToggleSelection());
        if (_selecting)
            Add($"删除所选 {_selected.Count} 段", (_, _) => DeleteSelected()).IsEnabled = _selected.Count > 0;
        Add("取消整理", async (_, _) => await RunAsync(cancel));
    }

    private void ChangeLevel(string id, int level)
    {
        var index = Index(id);
        if (index < 0 || _rows[index].Locked || _rows[index].Level == level) return;
        var caret = _lines.GetValueOrDefault(id)?.CaretOffset ?? (id == _active ? _lastCaret : 0);
        Remember(id, caret); _rows[index] = _rows[index] with { Level = level };
        Changed(); Paint(id, caret);
    }

    private void ToggleSelection()
    {
        Focus();
        _selecting = !_selecting; _selected.Clear(); Paint();
    }

    private void Paint(string? focus = null, int caret = 0)
    {
        _painting = true;
        try
        {
            var byId = _rows.ToDictionary(row => row.Id);
            foreach (var (id, old) in _displayed)
                if (!byId.TryGetValue(id, out var current) || !ReferenceEquals(old.Row, current) || old.Selecting != _selecting)
                    _passages.Children.Remove(old.Container);
            var displayed = new Dictionary<string, RowView>();
            _lines.Clear();
            var headings = new AiHeadingNumbers();
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var fresh = !_displayed.TryGetValue(row.Id, out var previous) || !ReferenceEquals(previous.Row, row) || previous.Selecting != _selecting;
                var view = fresh ? BuildRow(row, i) : previous!;
                if (view.Marker is { } marker)
                {
                    marker.Tag = $"[{_number}:{headings.Next(row.Level)}] ";
                    marker.Text = view.Host?.IsKeyboardFocusWithin == true ? new string('#', row.Level) + " " : (string)marker.Tag;
                }
                if (view.Host?.Editor is { } realized) _lines[row.Id] = realized;
                if (i >= _passages.Children.Count || !ReferenceEquals(_passages.Children[i], view.Container))
                {
                    if (view.Container.Parent is Panel parent) parent.Children.Remove(view.Container);
                    _passages.Children.Insert(i, view.Container);
                }
                displayed.Add(row.Id, view);
            }
            if (_passages.Children.Count > _rows.Count) _passages.Children.RemoveRange(_rows.Count, _passages.Children.Count - _rows.Count);
            _displayed = displayed;
        }
        finally { _painting = false; }
        if (focus != null && _displayed.TryGetValue(focus, out var focused)) FocusRow(focused, caret);
    }

    private static void FocusRow(RowView focused, int caret)
    {
        if (focused.Host is { } lineHost) lineHost.FocusSource(caret);
        else if (focused.Container is DockPanel block && block.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<MarkdownView>().FirstOrDefault() is { } locked)
        {
            locked.Focus(); locked.CaretPosition = caret == 0 ? locked.Document.ContentStart : locked.Document.ContentEnd;
        }
    }

    private void IndexEquations()
    {
        // Rows retain their segment order. Plain paragraphs need no Markdown pass.
        string? segment = null;
        var count = 0;
        foreach (var row in _rows)
        {
            if (row.SegmentId != segment) { segment = row.SegmentId; count = 0; }
            if (!row.Text.Contains("$$", StringComparison.Ordinal) && !row.Text.Contains(@"\[", StringComparison.Ordinal) &&
                !row.Text.Contains(@"\begin{", StringComparison.Ordinal)) continue;
            var math = MarkdownView.RevisionMath(row.Text, count);
            _precedingDisplays[row.Id] = count;
            foreach (var token in math.Tokens.Values.Where(token => token.Display))
            {
                count++;
                if (!_equationLabels.TryGetValue(row.SegmentId, out var labels)) _equationLabels[row.SegmentId] = labels = [];
                labels.TryAdd("eq-" + token.Number, token.Number!);
                if (token.Label is { } label) labels.TryAdd(label, token.Number!);
            }
        }
    }

    private FrameworkElement RenderRow(AiRevisionRow row)
    {
        if (!row.Locked && row.Links.Count == 0 && row.Text.AsSpan().IndexOfAny(MarkupCharacters) < 0 &&
            !row.Text.Contains("www.", StringComparison.OrdinalIgnoreCase) && !(row.Text.Length > 0 && char.IsAsciiDigit(row.Text[0]) && row.Text.Contains('.')) &&
            !row.Text.StartsWith("    ", StringComparison.Ordinal) && !row.Text.Any(char.IsSurrogate))
        {
            var text = new TextBlock { Text = row.Text, FontFamily = ChatReadingTypography.Family, FontSize = AiChatTypography.FontSize,
                TextWrapping = TextWrapping.Wrap, LineHeight = AiChatTypography.LineHeight, Padding = new Thickness(2, 1, 2, 1),
                MinHeight = AiChatTypography.LineHeight, Background = Brushes.Transparent };
            System.Windows.Documents.Typography.SetStylisticSet4(text, true);
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return text;
        }
        var preview = new MarkdownView { ChatMode = true, TurnNumber = _number, EquationPanel = _equations?.Panel, EquationSource = _equations?.Source,
            ImageBaseDirectory = _imageBaseDirectory, Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(-AiChatTypography.RichTextInset, 0, -AiChatTypography.RichTextInset, 0),
            RevisionEquationLabels = _equationLabels.GetValueOrDefault(row.SegmentId), RevisionEquationSegment = row.SegmentId,
            PrecedingDisplayCount = _precedingDisplays.GetValueOrDefault(row.Id),
            MinHeight = AiChatTypography.LineHeight, Focusable = row.Locked };
        preview.SetRevisionPreview(row);
        return preview;
    }
    private static readonly System.Buffers.SearchValues<char> MarkupCharacters =
        System.Buffers.SearchValues.Create("*_~$\\`[<>#-+!|:&{\n\r\t)@^=\uFE0F\u20E3");

    private RowView BuildRow(AiRevisionRow row, int index)
    {
        var block = new DockPanel { Tag = row.Id, ContextMenu = ContextMenu, Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
        TextBlock? marker = null;
        RevisionLineHost? host = null;
        if (row.Level is >= 1 and <= 6)
        {
            marker = new TextBlock { VerticalAlignment = VerticalAlignment.Top,
                FontFamily = ChatReadingTypography.Family, FontSize = AiChatTypography.FontSize };
            marker.SetResourceReference(TextBlock.ForegroundProperty, "ChatHeading"); block.Children.Add(marker);
        }
        if (row.Locked || _selecting)
        {
            if (_selecting && !row.Locked)
            {
                var reading = RenderRow(row);
                reading.IsHitTestVisible = false;
                block.Children.Add(reading);
            }
            else
            {
                var preview = (MarkdownView)RenderRow(row);
                preview.ToolTip = "回车在其后分段，Shift+Enter 在其前分段";
                preview.GotKeyboardFocus += (_, _) => _active = row.Id;
                preview.PreviewKeyDown += (_, e) =>
                {
                    if (_selecting || e.Key != Key.Enter || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
                    var i = Index(row.Id);
                    if (i < 0) return;
                    e.Handled = true; Remember(row.Id, 0);
                    var next = NewRow(row.SegmentId);
                    _rows.Insert(i + (Keyboard.Modifiers == ModifierKeys.Shift ? 0 : 1), next);
                    Changed(); Paint(next.Id);
                };
                if (_selecting) preview.IsHitTestVisible = false;
                var locked = new StackPanel(); locked.Children.Add(preview);
                block.Children.Add(locked);
            }
        }
        else
        {
            host = new RevisionLineHost(row, _imageBaseDirectory, RenderRow, line =>
            {
                _lines[row.Id] = line;
                line.GotKeyboardFocus += (_, _) =>
                {
                    _active = row.Id;
                    if (marker != null) marker.Text = new string('#', row.Level) + " ";
                };
                line.Changed = () => TextChanged(row.Id, line);
                line.PasteText = value => Replace(row.Id, line, value);
                line.UndoEdit = Undo;
                line.PreviewKeyDown += (_, e) => KeyDownInLine(row.Id, line, e);
            }, line =>
            {
                if (_active == row.Id) _lastCaret = line.CaretOffset;
                if (marker?.Tag is string number) marker.Text = number;
                if (ReferenceEquals(_lines.GetValueOrDefault(row.Id), line)) _lines.Remove(row.Id);
                if (!_painting && host != null) CleanupAbandonedEmpty(row.Id, host);
            });
            block.Children.Add(host);
            if (marker != null) marker.MouseLeftButtonDown += (_, e) => { host.FocusSource(0); e.Handled = true; };
        }
        if (_selecting && !row.Locked)
        {
            var pick = new Button { Content = block, Tag = row.Id, ContextMenu = ContextMenu, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            System.Windows.Automation.AutomationProperties.SetName(pick, $"选择第 {index + 1} 段");
            pick.Click += (_, _) =>
            {
                if (_selected.Add(row.Id)) pick.SetResourceReference(BackgroundProperty, "TextSelectionBackground");
                else { _selected.Remove(row.Id); pick.Background = Brushes.Transparent; }
            };
            return new(row, _selecting, pick, marker, null);
        }
        return new(row, _selecting, block, marker, host);
    }

    private void CleanupAbandonedEmpty(string id, RevisionLineHost host)
    {
        if (_painting) return;
        var index = Index(id);
        if (index < 0 || !ReferenceEquals(_displayed.GetValueOrDefault(id)?.Host, host) ||
            !string.IsNullOrWhiteSpace(_rows[index].Text) || _rows[index].Links.Count != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var i = Index(id);
            if (_rows.Count < 2 || i < 0 || _rows[i].Links.Count > 0 || !string.IsNullOrWhiteSpace(_rows[i].Text) ||
                !ReferenceEquals(_displayed.GetValueOrDefault(id)?.Host, host) || host.IsKeyboardFocusWithin || !_passages.IsKeyboardFocusWithin) return;
            _rows.RemoveAt(i); Changed(); Paint();
        }));
    }

    private void TextChanged(string id, RevisionLineEditor line)
    {
        var i = Index(id);
        if (_painting || i < 0 || !ReferenceEquals(_lines.GetValueOrDefault(id), line)) return;
        var old = _rows[i];
        var next = line.Buffer.Apply(old);
        if (old.Text == next.Text && old.Links.SequenceEqual(next.Links)) return;
        Remember(id, line.CaretOffset);
        _rows[i] = next; _displayed[id] = _displayed[id] with { Row = next }; _displayed[id].Host!.Row = next; Changed();
        if (!string.IsNullOrWhiteSpace(old.Text) && string.IsNullOrWhiteSpace(next.Text))
        {
            // Let native text input (including an IME commit) finish updating its caret first.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var index = Index(id);
                if (index >= 0 && ReferenceEquals(_rows[index], next) && line.IsKeyboardFocusWithin) RemoveEmpty(index, false);
            }));
        }
    }

    private void Replace(string id, RevisionLineEditor line, string input)
    {
        var i = Index(id); if (i < 0) return;
        input = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var current = line.Buffer;
        var start = line.SelectionLength == 0 ? line.CaretOffset : line.SelectionStart;
        var end = start + line.SelectionLength;
        if (start == end)
        {
            var containing = current.Atoms.FirstOrDefault(atom => start > atom.At && start < atom.End);
            if (containing is not null) start = end = containing.End;
        }
        else (start, end) = current.WholeAtoms(start, end);
        var left = current.Slice(0, start);
        var keep = current.Slice(start, end - start).ProtectedOnly();
        var right = keep.Append(current.Slice(end, current.Display.Length - end));
        var words = input.Split('\n');
        Remember(id, start);
        var row = _rows[i];
        var prefix = left.Append(RevisionBuffer.Plain(words[0]));
        if (words.Length == 1)
        {
            _rows[i] = prefix.Append(right).Apply(row);
            if (string.IsNullOrWhiteSpace(_rows[i].Text) && row.Text.Trim().Length > 0) { RemoveEmpty(i, false); return; }
        }
        else
        {
            _rows[i] = prefix.Apply(row);
            for (var n = 1; n < words.Length; n++)
            {
                var part = RevisionBuffer.Plain(words[n]);
                if (n == words.Length - 1) part = part.Append(right);
                _rows.Insert(i + n, part.Apply(NewRow(row.SegmentId)));
            }
        }
        Changed(); Paint(_rows[i + words.Length - 1].Id, words.Length == 1 ? prefix.Display.Length : words[^1].Length);
    }

    private void KeyDownInLine(string id, RevisionLineEditor line, KeyEventArgs e)
    {
        if (e.Handled || e.Key == Key.ImeProcessed) return;
        var i = Index(id); if (i < 0) return;
        if (e.Key == Key.Enter) { e.Handled = true; Replace(id, line, "\n"); return; }
        if (line.SelectionLength == 0 && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var movingForward = e.Key is Key.Right or Key.Down;
            if (e.Key is Key.Left or Key.Right && line.CaretOffset != (movingForward ? line.Document.TextLength : 0)) return;
            if (e.Key is Key.Up or Key.Down)
            {
                var view = line.TextArea.TextView;
                if (!view.VisualLinesValid || view.VisualLines.Count == 0) return;
                var edge = movingForward ? view.VisualLines[^1] : view.VisualLines[0];
                var caret = line.TextArea.Caret;
                if (caret.Line < edge.FirstDocumentLine.LineNumber || caret.Line > edge.LastDocumentLine.LineNumber ||
                    edge.GetTextLine(caret.VisualColumn, caret.Position.IsAtEndOfLine) != (movingForward ? edge.TextLines[^1] : edge.TextLines[0])) return;
            }
            var adjacent = i + (movingForward ? 1 : -1);
            if (adjacent >= 0 && adjacent < _rows.Count)
            {
                var row = _rows[adjacent];
                e.Handled = true;
                FocusRow(_displayed[row.Id], movingForward ? 0 : EndOfText(row));
            }
            return;
        }
        if (e.Key is not (Key.Back or Key.Delete) || Keyboard.Modifiers != ModifierKeys.None) return;
        var forward = e.Key == Key.Delete;
        if (line.SelectionLength > 0) { e.Handled = true; Replace(id, line, ""); return; }
        var at = line.CaretOffset; var buffer = line.Buffer;
        var empty = string.IsNullOrWhiteSpace(_rows[i].Text);
        if (!empty && at != (forward ? buffer.Display.Length : 0))
        {
            var adjacent = forward ? at : at - 1;
            var atom = buffer.Atoms.FirstOrDefault(a => adjacent >= a.At && adjacent < a.End);
            if (atom is not null) { e.Handled = true; line.CaretOffset = forward ? atom.End : atom.At; }
            return;
        }
        e.Handled = true;
        var neighbour = i + (forward ? 1 : -1);
        if (neighbour < 0 || neighbour >= _rows.Count) return;
        if (empty)
        {
            Remember(id, at);
            if (i == 0 && _rows[i].Links.Count > 0 && !_rows[neighbour].Locked) Merge(i, forward);
            else RemoveEmpty(i, forward);
            return;
        }
        if (_rows[neighbour].Locked) return;
        Remember(id, at); Merge(i, forward);
    }

    private void Merge(int index, bool forward)
    {
        var leftIndex = forward ? index : index - 1;
        var left = RevisionBuffer.From(_rows[leftIndex], RevisionMathScope.Inline);
        var right = RevisionBuffer.From(_rows[leftIndex + 1], RevisionMathScope.Inline);
        var container = _rows[forward ? leftIndex + 1 : leftIndex];
        _rows[leftIndex] = left.Append(right).Apply(container);
        _rows.RemoveAt(leftIndex + 1); Changed(); Paint(container.Id, left.Display.Length);
    }
    private void RemoveEmpty(int index, bool forward)
    {
        var row = _rows[index];
        if (index == 0 && row.Links.Count > 0)
        {
            _rows[0] = row with { Text = "", Level = 0, Links = row.Links.Select(l => l with { Offset = 0 }).ToList() };
            Changed(); Paint(row.Id); return;
        }
        if (index > 0) AppendLinks(_rows, index - 1, row.Links);
        _rows.RemoveAt(index);
        EnsureRow();
        var target = Math.Clamp(forward ? index : index - 1, 0, _rows.Count - 1);
        var offset = !forward && index > 0 ? EndOfText(_rows[target]) : 0;
        Changed(); Paint(_rows[target].Id, offset);
    }
    private static int EndOfText(AiRevisionRow row)
    {
        var content = RevisionBuffer.From(row, RevisionMathScope.Inline);
        var at = content.Display.Length;
        foreach (var atom in content.Atoms.Reverse())
        {
            if (atom.End != at || atom.Source.Length > 0) break;
            at = atom.At;
        }
        return at;
    }
    private static void AppendLinks(List<AiRevisionRow> rows, int i, IReadOnlyList<AiRevisionLink> links)
    {
        var previous = rows[i];
        rows[i] = previous with { Links = [.. previous.Links, .. links.Select(l => l with { Offset = previous.Text.Length })] };
    }
    private void DeleteSelected()
    {
        if (_selected.Count == 0) return;
        Remember(_active, _lines.GetValueOrDefault(_active)?.CaretOffset ?? 0);
        var kept = new List<AiRevisionRow>();
        foreach (var row in _rows)
        {
            if (row.Locked || !_selected.Contains(row.Id)) { kept.Add(row); continue; }
            var protectedRow = RevisionBuffer.From(row, RevisionMathScope.Inline).ProtectedOnly().Apply(row);
            if (protectedRow.Text.Length > 0) kept.Add(protectedRow);
            else if (kept.Count == 0 && row.Links.Count > 0) kept.Add(protectedRow with { Level = 0 });
            else if (kept.Count > 0 && row.Links.Count > 0) AppendLinks(kept, kept.Count - 1, row.Links);
        }
        _rows = kept; EnsureRow(); _selecting = false; _selected.Clear(); Changed(); Paint();
    }
}
