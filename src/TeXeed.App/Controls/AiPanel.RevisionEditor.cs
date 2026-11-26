using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<string, TurnRevisionEditor> _revisionEditors = [];

    internal static bool IsRevisionInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TurnRevisionEditor) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void AddRevisionCommands(ContextMenu menu, Func<AiTurn?> getTurn, MarkdownView? source = null)
    {
        // Most turns are only read. Materialize these commands on the first menu opening.
        void Opened(object sender, RoutedEventArgs e)
        {
            if (getTurn() is not { IsIncomplete: false }) return;
            menu.Opened -= Opened;
            if (menu.Items.Count != 0) menu.Items.Add(new Separator());
            void Add(string label, Action<AiTurn> action)
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) =>
                {
                    try { if (getTurn() is { } turn) action(turn); }
                    catch (Exception ex) { ShowError(ex); }
                };
                menu.Items.Add(item);
            }
            Add(source is null ? "整理这一轮" : "整理这一段", turn => OpenRevisionEditor(turn, source));
            Add("请 AI 改这一轮", turn => RequestAiRevision(turn));
            Add("编辑历史", ShowRevisionHistory);
        }
        menu.Opened += Opened;
    }

    private void OpenRevisionEditor(AiTurn turn, MarkdownView? source)
    {
        if (!_turnViews.TryGetValue(turn.Id, out var view)) return;
        var context = source?.RevisionContext(MessagesScroll);
        var segment = source?.DataContext as AiSegment;
        var draft = turn.UserRevisionDraft ?? BeginRevision(turn);
        string? targetId = null;
        if (context is { SourceOffset: >= 0 } hit && segment is not null)
        {
            var readingRows = turn.RevisionHistory?.LastOrDefault()?.Rows ??
                (turn.UserRevisionDraft is null ? draft.Rows : AiRevisionText.Parse(segment.Id, segment.Text));
            targetId = RevisionRowAt(source!.Markdown, hit.SourceOffset, readingRows.Where(r => r.SegmentId == segment.Id), draft.Rows);
        }
        if (!_revisionEditors.TryGetValue(turn.Id, out var editor))
        {
            turn.UserRevisionDraft = draft;
            Dirty();
            editor = new TurnRevisionEditor(draft, view.TurnNumber, System.IO.Path.GetDirectoryName(_project),
                next => { turn.UserRevisionDraft = next; Dirty(); },
                async rows =>
                {
                    var anchor = editor?.ActiveReadingAnchor(MessagesScroll);
                    var version = _readingRestoreVersion;
                    await CommitRevisionAsync(turn.Id, draft.Version, rows, "user");
                    if (!_revisionEditors.TryGetValue(turn.Id, out var currentEditor) || currentEditor != editor) return;
                    CloseRevisionEditor(turn.Id);
                    if (anchor is { } place)
                        await RestoreFinishedParagraphAsync(turn.Id, rows, place, version);
                },
                async () =>
                {
                    var previous = turn.UserRevisionDraft;
                    turn.UserRevisionDraft = null;
                    try { await SaveAsync(); }
                    catch { turn.UserRevisionDraft = previous; throw; }
                    CloseRevisionEditor(turn.Id);
                }, (this, turn));
            _revisionEditors[turn.Id] = editor;
        }
        view.SetRevisionEditor(editor);
        // A newly inserted row must be arranged before WPF can give its input surface focus.
        History.UpdateLayout();
        if (context is { } place && segment is not null)
        {
            if (editor.AtReadingPosition(segment.Id, place.Text, place.Offset, activate: true, rowId: targetId) is { } row)
            {
                History.UpdateLayout();
                if (place.Y >= 0 && place.Y < MessagesScroll.ViewportHeight)
                    MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset + row.TranslatePoint(new Point(), MessagesScroll).Y - place.Y);
                return;
            }
        }
        editor.FocusFirstParagraph();
    }

    private static string? RevisionRowAt(string displayed, int offset, IEnumerable<AiRevisionRow> readingRows, List<AiRevisionRow> draftRows)
    {
        // Resolve against the published text first. A saved draft may already have
        // changed the words or moved this paragraph, while its row ID remains stable.
        var rows = readingRows.ToList();
        foreach (var range in AiRevisionText.DisplayRanges(displayed, rows))
        {
            if (offset >= range.End) continue;
            if (draftRows.Any(r => r.Id == range.Row.Id)) return range.Row.Id;
            // The clicked paragraph was deleted from the draft. Open its nearest surviving neighbor.
            var i = rows.IndexOf(range.Row);
            return rows.Select((r, index) => (r.Id, Distance: Math.Abs(index - i)))
                .OrderBy(item => item.Distance).Select(item => item.Id).FirstOrDefault(id => draftRows.Any(r => r.Id == id));
        }
        return null;
    }
    private void CloseRevisionEditor(string turnId)
    {
        _revisionEditors.Remove(turnId);
        if (_turnViews.TryGetValue(turnId, out var current)) current.SetRevisionEditor(null);
    }

    private async Task RestoreFinishedParagraphAsync(string turnId, IReadOnlyList<AiRevisionRow> rows,
        (string RowId, string SegmentId, double Y) place, int version)
    {
        var conversation = _book.Active;
        bool Current() => !_disposed && IsVisible && version == _readingRestoreVersion && _book.Active == conversation;
        if (!Current()) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!Current()) return;
        var turn = conversation.Turns.FirstOrDefault(t => t.Id == turnId);
        if (turn is null || turn.Collapsed) return;
        if (!_turnViews.TryGetValue(turnId, out var view) || !view.IsLoaded || !ReferenceEquals(view.Turn, turn))
        {
            // Replacing a long item with its persisted version can recycle its presenter.
            var index = _transcriptItems.IndexOf(turn);
            if (index < 0) return;
            VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!Current() || !_turnViews.TryGetValue(turnId, out view)) return;
        }
        var answer = view.Answers.FirstOrDefault(v => v.DataContext is AiSegment s && s.Id == place.SegmentId);
        if (answer is null) return;
        await answer.RenderTask;
        if (!Current() || !answer.IsLoaded || !answer.IsVisible) return;
        var saved = turn.RevisionHistory?.LastOrDefault()?.Rows ?? rows;
        if (answer.RevisionRowStart(saved.Where(r => r.SegmentId == place.SegmentId), place.RowId) is not { } pointer) return;
        for (var pass = 0; pass < 2; pass++)
        {
            MessagesScroll.UpdateLayout();
            if (!Current() || !answer.IsLoaded || !pointer.IsInSameDocument(answer.Document.ContentStart) ||
                !TryLayoutAnchorY(answer, pointer, out var y)) return;
            var offset = Math.Clamp(MessagesScroll.VerticalOffset + y - place.Y, 0, MessagesScroll.ScrollableHeight);
            if (Math.Abs(offset - MessagesScroll.VerticalOffset) <= .5) return;
            MessagesScroll.ScrollToVerticalOffset(offset);
        }
    }
    private void ShowRevisionHistory(AiTurn turn)
    {
        var versions = turn.RevisionHistory ?? [new AiTurnRevision("original", "ai", turn.AnsweredAt ?? turn.CreatedAt, BeginRevision(turn).Rows)
            { Sources = AiRevisionText.Segments(turn).ToDictionary(s => s.Id, s => s.Text) }];
        var layout = new DockPanel { Margin = new Thickness(16) };
        var list = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(list, Dock.Top); layout.Children.Add(list);
        var viewer = new MarkdownView { ChatMode = true, TurnNumber = _book.Turns.IndexOf(turn) + 1,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ImageBaseDirectory = System.IO.Path.GetDirectoryName(_project) };
        layout.Children.Add(viewer);
        var chronological = versions.AsEnumerable().Reverse().ToArray();
        foreach (var version in chronological)
        {
            var label = version.Id == "original" ? "原答 · AI" : version.Author == "user" ? "用户" : "AI";
            list.Items.Add($"{version.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}　{label}");
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var version = chronological[list.SelectedIndex];
            var source = version.Sources.Count > 0 ? string.Join("\n\n", version.Sources.Values) : AiRevisionText.Source(version.Rows);
            viewer.SetHistorySource(source, revision: version.Id != "original");
            viewer.ScrollToHome();
        };
        list.SelectedIndex = 0;
        var history = new Window { Title = $"第 {_book.Turns.IndexOf(turn) + 1} 轮 · 编辑历史",
            Owner = Window.GetWindow(this), Content = layout, Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        history.SetResourceReference(BackgroundProperty, "EditorBackground"); history.Show();
    }
}
