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
    private readonly Action<AiRevisionDraft> _saveDraft;
    private readonly Func<IReadOnlyList<AiRevisionRow>, Task> _finish;
    private List<AiRevisionRow> _rows;
    private readonly Stack<(List<AiRevisionRow> Rows, string Id, int Caret)> _undo = new();
    private readonly HashSet<string> _selected = [];
    private readonly Dictionary<string, RevisionLineEditor> _lines = [];
    private sealed record RowView(AiRevisionRow Row, bool Selecting, FrameworkElement Container, TextBlock? Marker, RevisionLineHost? Host);
    private Dictionary<string, RowView> _displayed = [];
    private readonly StackPanel _passages = new();
    private readonly Button _pick, _drop, _back;
    private readonly ComboBox _level = new() { ToolTip = "当前段落的标题层级", MinWidth = 80, Margin = new Thickness(2) };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _selecting, _painting, _updatingLevel;
    private string _active = "";

    internal TurnRevisionEditor(AiRevisionDraft draft, int number, string? imageBaseDirectory, Action<AiRevisionDraft> saveDraft,
        Func<IReadOnlyList<AiRevisionRow>, Task> finish, Func<Task> cancel)
    {
        _original = draft; _rows = [.. draft.Rows]; _number = number; _imageBaseDirectory = imageBaseDirectory;
        _saveDraft = saveDraft; _finish = finish;
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        Children.Add(tools);
        Button Add(string title, RoutedEventHandler click)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 2, 5, 2), Padding = new Thickness(8, 3, 8, 3) };
            button.Click += click; tools.Children.Add(button); return button;
        }
        Add("完成整理", async (_, _) => await RunAsync(() => _finish(KeptRows())));
        Add("取消整理", async (_, _) => await RunAsync(cancel));
        _back = Add("撤销", (_, _) => Undo());
        _pick = Add("选择段落", (_, _) => { _selecting = !_selecting; _selected.Clear(); Paint(); });
        _drop = Add("删除所选段落", (_, _) => DeleteSelected());
        _level.Items.Add("正文");
        for (var i = 1; i <= 6; i++) _level.Items.Add($"{i} 级标题");
        _level.SelectionChanged += (_, _) =>
        {
            if (_updatingLevel || _painting || _level.SelectedIndex < 0) return;
            var index = Index(_active);
            if (index < 0 || _rows[index].Locked || _rows[index].Level == _level.SelectedIndex) return;
            var caret = _lines.GetValueOrDefault(_active)?.CaretOffset ?? 0;
            Remember(_active, caret); _rows[index] = _rows[index] with { Level = _level.SelectedIndex };
            Changed(); Paint(_active, caret);
        };
        tools.Children.Add(_level);
        Children.Add(_notice); Children.Add(_passages);
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Undo(); }
            if (e.Key == Key.Escape && _selecting) { e.Handled = true; _pick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        }));
        EnsureRow(); _active = _rows.FirstOrDefault(r => !r.Locked)?.Id ?? ""; Paint();
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { _notice.Text = ex.Message; _notice.Visibility = Visibility.Visible; }
        finally { IsEnabled = true; }
    }
    internal FrameworkElement? AtReadingPosition(string segmentId, string text, int offset)
    {
        var position = 0;
        var best = int.MaxValue;
        RowView? match = null;
        foreach (var row in _rows.Where(row => row.SegmentId == segmentId))
        {
            if (row.Text.Contains(text, StringComparison.Ordinal) || text.Contains(row.Text, StringComparison.Ordinal))
            {
                var distance = Math.Abs(position - offset);
                if (distance < best) { best = distance; match = _displayed[row.Id]; }
            }
            position += row.Text.Length + 2;
        }
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
    private void Changed() { _saveDraft(_original with { Rows = [.. _rows] }); Controls(); }
    private void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        _rows = previous.Rows; _selecting = false; _selected.Clear();
        Changed(); Paint(previous.Id, previous.Caret);
    }
    private void Controls()
    {
        _pick.Content = _selecting ? "取消选择" : "选择段落";
        _back.IsEnabled = _undo.Count > 0;
        _drop.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        _drop.IsEnabled = _selected.Count > 0;
        _drop.Content = _selected.Count > 0 ? $"删除所选 {_selected.Count} 段" : "删除所选段落";
        var index = Index(_active);
        _updatingLevel = true;
        _level.SelectedIndex = index < 0 ? -1 : _rows[index].Level;
        _level.IsEnabled = !_selecting && index >= 0 && !_rows[index].Locked;
        _updatingLevel = false;
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
                if (view.Marker != null) view.Marker.Text = $"[{_number}:{headings.Next(row.Level)}] ";
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
            Controls();
        }
        finally { _painting = false; }
        if (focus == null || !_displayed.TryGetValue(focus, out var focused)) return;
        if (focused.Host is { } lineHost)
        {
            var target = lineHost.Activate();
            target.Focus(); target.TextArea.Focus(); target.CaretOffset = Math.Clamp(caret, 0, target.Document.TextLength);
        }
        else if (focused.Container is DockPanel block && block.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<MarkdownView>().FirstOrDefault() is { } locked)
        {
            locked.Focus(); locked.CaretPosition = caret == 0 ? locked.Document.ContentStart : locked.Document.ContentEnd;
        }
    }

    private RowView BuildRow(AiRevisionRow row, int index)
    {
        var block = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
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
                block.Children.Add(new RevisionLineEditor(RevisionBuffer.From(row), _imageBaseDirectory) { Focusable = false, IsReadOnly = true, IsHitTestVisible = false });
            else
            {
                var preview = new MarkdownView { ChatMode = true, ImageBaseDirectory = _imageBaseDirectory, Markdown = row.Text, Padding = new Thickness(2),
                    ToolTip = "回车在其后分段，Shift+Enter 在其前分段" };
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
                foreach (var link in row.Links.Where(link => !link.Id.StartsWith("bookmark:", StringComparison.Ordinal)))
                {
                    var label = new TextBlock { Text = "🗨️ " + link.Label };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); locked.Children.Add(label);
                }
                block.Children.Add(locked);
            }
        }
        else
        {
            host = new RevisionLineHost(RevisionBuffer.From(row), _imageBaseDirectory, line =>
            {
                _lines[row.Id] = line;
                line.GotKeyboardFocus += (_, _) => { _active = row.Id; Controls(); };
                line.Changed = () => TextChanged(row.Id, line);
                line.LostKeyboardFocus += (_, _) => CleanupAbandonedEmpty(row.Id, line);
                line.PasteText = value => Replace(row.Id, line, value);
                line.UndoEdit = Undo;
                line.PreviewKeyDown += (_, e) => KeyDownInLine(row.Id, line, e);
            });
            block.Children.Add(host);
        }
        if (_selecting && !row.Locked)
        {
            var pick = new Button { Content = block, Tag = row.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2) };
            System.Windows.Automation.AutomationProperties.SetName(pick, $"选择第 {index + 1} 段");
            pick.Click += (_, _) =>
            {
                if (_selected.Add(row.Id)) pick.SetResourceReference(BackgroundProperty, "TextSelectionBackground");
                else { _selected.Remove(row.Id); pick.Background = Brushes.Transparent; }
                Controls();
            };
            return new(row, _selecting, pick, marker, null);
        }
        return new(row, _selecting, block, marker, host);
    }

    private void CleanupAbandonedEmpty(string id, RevisionLineEditor line)
    {
        if (_painting) return;
        var index = Index(id);
        if (index < 0 || !string.IsNullOrWhiteSpace(_rows[index].Text) || _rows[index].Links.Count != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var i = Index(id);
            if (_rows.Count < 2 || i < 0 || _rows[i].Links.Count > 0 || !string.IsNullOrWhiteSpace(_rows[i].Text) ||
                line.IsKeyboardFocusWithin || !_passages.IsKeyboardFocusWithin || !ReferenceEquals(_lines.GetValueOrDefault(id), line)) return;
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
        _rows[i] = next; _displayed[id] = _displayed[id] with { Row = next }; Changed();
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
        if (e.Key is not (Key.Back or Key.Delete) || Keyboard.Modifiers != ModifierKeys.None) return;
        var forward = e.Key == Key.Delete;
        if (line.SelectionLength > 0) { e.Handled = true; Replace(id, line, ""); return; }
        var at = line.CaretOffset; var buffer = line.Buffer;
        var empty = string.IsNullOrWhiteSpace(_rows[i].Text);
        if (!empty && at != (forward ? buffer.Display.Length : 0))
        {
            if (buffer.Atoms.Any(a => a.At == (forward ? at : at - 1))) { e.Handled = true; line.CaretOffset = at + (forward ? 1 : -1); }
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
        var left = RevisionBuffer.From(_rows[leftIndex]);
        var right = RevisionBuffer.From(_rows[leftIndex + 1]);
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
        var content = RevisionBuffer.From(row);
        var at = content.Display.Length;
        foreach (var atom in content.Atoms.Reverse())
        {
            if (atom.At != at - 1 || atom.Source.Length > 0) break;
            at--;
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
            var protectedRow = RevisionBuffer.From(row).ProtectedOnly().Apply(row);
            if (protectedRow.Text.Length > 0) kept.Add(protectedRow);
            else if (kept.Count == 0 && row.Links.Count > 0) kept.Add(protectedRow with { Level = 0 });
            else if (kept.Count > 0 && row.Links.Count > 0) AppendLinks(kept, kept.Count - 1, row.Links);
        }
        _rows = kept; EnsureRow(); _selecting = false; _selected.Clear(); Changed(); Paint();
    }
}
