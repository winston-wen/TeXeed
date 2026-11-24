using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<string, TurnRevisionEditor> _revisionEditors = [];

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
            Add("整理这一轮", turn => OpenRevisionEditor(turn, source));
            Add("请 AI 改这一轮", turn => RequestAiRevision(turn));
            Add("编辑历史", ShowRevisionHistory);
        }
        menu.Opened += Opened;
    }

    private void OpenRevisionEditor(AiTurn turn, MarkdownView? source)
    {
        if (!_turnViews.TryGetValue(turn.Id, out var view)) return;
        var context = source?.RevisionContext(MessagesScroll);
        if (!_revisionEditors.TryGetValue(turn.Id, out var editor))
        {
            var draft = turn.UserRevisionDraft ?? BeginRevision(turn);
            turn.UserRevisionDraft = draft;
            Dirty();
            editor = new TurnRevisionEditor(draft, view.TurnNumber, System.IO.Path.GetDirectoryName(_project),
                next => { turn.UserRevisionDraft = next; Dirty(); },
                async rows =>
                {
                    await CommitRevisionAsync(turn.Id, draft.Version, rows, "user");
                    CloseRevisionEditor(turn.Id);
                },
                async () =>
                {
                    var previous = turn.UserRevisionDraft;
                    turn.UserRevisionDraft = null;
                    try { await SaveAsync(); }
                    catch { turn.UserRevisionDraft = previous; throw; }
                    CloseRevisionEditor(turn.Id);
                });
            _revisionEditors[turn.Id] = editor;
        }
        view.SetRevisionEditor(editor);
        if (context is { } place && source?.DataContext is AiSegment segment && place.Y >= 0 && place.Y < MessagesScroll.ViewportHeight)
        {
            History.UpdateLayout();
            if (editor.AtReadingPosition(segment.Id, place.Text, place.Offset) is { } row)
                MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset + row.TranslatePoint(new Point(), MessagesScroll).Y - place.Y);
        }
    }
    private void CloseRevisionEditor(string turnId)
    {
        _revisionEditors.Remove(turnId);
        if (_turnViews.TryGetValue(turnId, out var current)) current.SetRevisionEditor(null);
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
            viewer.Markdown = version.Sources.Count > 0 ? string.Join("\n\n", version.Sources.Values) : AiRevisionText.Source(version.Rows);
            viewer.ScrollToHome();
        };
        list.SelectedIndex = 0;
        var history = new Window { Title = $"第 {_book.Turns.IndexOf(turn) + 1} 轮 · 编辑历史",
            Owner = Window.GetWindow(this), Content = layout, Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        history.SetResourceReference(BackgroundProperty, "EditorBackground"); history.Show();
    }
}
