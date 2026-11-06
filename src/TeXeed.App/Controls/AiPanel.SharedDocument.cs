using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<(string Project, string Conversation), AiSharedDocument> _pendingDocumentVersions = [];
    private readonly SemaphoreSlim _documentVersionGate = new(1, 1);

    private void InitializeSharedDocument()
    {
        SharedDocumentView.RoundIdProvider = () => _book.Turns.LastOrDefault()?.Id;
        SharedDocumentView.DocumentEdited += (_, _) =>
        {
            if (_project is null) return;
            _pendingDocumentVersions[(_project, _book.Active.Id)] = _book.Active.SharedDocument.Snapshot();
            Dirty();
        };
        SharedDocumentView.VersionRequested += async (_, _) => await ShowDocumentVersionsAsync();
        SharedDocumentView.CarryRequested += async (_, _) => await ShowCarrySelectionAsync();
        SharedDocumentView.SearchRequested += (_, _) => OpenDocumentSearch();
    }

    private void BindSharedDocument()
    {
        var document = _book.Active.SharedDocument;
        var model = document.AuthorSpans.LastOrDefault(span => span.Role == "ai")?.Author ?? Settings.SelectedModel?.Name ?? "AI";
        SharedDocumentView.Bind(document, _book.Turns.FirstOrDefault()?.Question ?? "", "用户", model);
    }

    private async Task FlushDocumentVersionAsync()
    {
        await _documentVersionGate.WaitAsync();
        try
        {
            foreach (var entry in _pendingDocumentVersions.ToArray())
            {
                await AiDocumentRepository.SaveVersionAsync(entry.Key.Project, entry.Key.Conversation, entry.Value, "用户", "修改文稿与评论");
                if (_pendingDocumentVersions.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value))
                    _pendingDocumentVersions.Remove(entry.Key);
            }
        }
        finally { _documentVersionGate.Release(); }
    }

    private async Task SaveSharedVersionAsync(string author, string description)
    {
        if (_project is null) throw new InvalidOperationException("没有打开的项目。");
        await FlushDocumentVersionAsync();
        await AiDocumentRepository.SaveVersionAsync(_project, _book.Active.Id, _book.Active.SharedDocument, author, description);
    }

    private async Task ShowDocumentVersionsAsync()
    {
        if (_project is null) return;
        var project = _project; var conversation = _book.Active; var document = conversation.SharedDocument;
        try
        {
            await FlushDocumentVersionAsync();
            var versions = await AiDocumentRepository.ListVersionsAsync(project, conversation.Id);
            var window = NewDocumentDialog("文稿版本", 850, 620);
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var list = new ListBox { Margin = new Thickness(0, 0, 12, 0) };
            foreach (var version in versions) list.Items.Add(new ListBoxItem { Content = $"{version.CreatedAt.LocalDateTime:g}  {version.Author}\n{version.Description}", Tag = version });
            var source = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(source, 1); grid.Children.Add(list); grid.Children.Add(source);
            var restore = new Button { Content = "恢复所选版本", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsEnabled = false };
            Grid.SetRow(restore, 1); Grid.SetColumnSpan(restore, 2); grid.Children.Add(restore);
            AiDocumentVersion? selected = null; AiSharedDocument? selectedDocument = null;
            list.SelectionChanged += async (_, _) =>
            {
                selected = (list.SelectedItem as ListBoxItem)?.Tag as AiDocumentVersion; selectedDocument = null; restore.IsEnabled = false;
                if (selected is null) return;
                var requested = selected;
                try { var loaded = await AiDocumentRepository.ReadVersionAsync(project, conversation.Id, requested.Commit); if (selected != requested) return; selectedDocument = loaded; source.Text = loaded.Source; restore.IsEnabled = true; }
                catch (Exception ex) { source.Text = ex.Message; }
            };
            restore.Click += async (_, _) =>
            {
                if (selectedDocument is null || _book.Active != conversation) return;
                restore.IsEnabled = false;
                var historical = selectedDocument;
                var before = document.Snapshot();
                try
                {
                    await AiDocumentRepository.SaveVersionAsync(project, conversation.Id, before, "用户", "恢复前的文稿");
                    if (!document.TryRestore(before, historical)) { MessageBox.Show(window, "文稿在读取期间已改变，请重新选择版本。", "文稿版本"); return; }
                    SharedDocumentView.RefreshDocument();
                    _pendingDocumentVersions[(project, conversation.Id)] = document.Snapshot();
                    Dirty(); await SaveAsync(); window.Close();
                }
                catch (Exception ex) { MessageBox.Show(window, ex.Message, "无法恢复版本"); }
                finally { restore.IsEnabled = true; }
            };
            window.Content = grid; window.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ShowCarrySelectionAsync()
    {
        if (_project is null) return;
        try
        {
            await SaveAsync();
            var records = AiGlobalCarry.List();
            var window = NewDocumentDialog("选择携带的轮次", 620, 520);
            var panel = new StackPanel { Margin = new Thickness(16) };
            foreach (var conversation in records.OrderBy(c => c.Project, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Title))
            {
                foreach (var turn in conversation.Rounds.OrderBy(r => r.Number))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                    var picker = new ComboBox { Width = 82, Margin = new Thickness(0, 0, 12, 0), DisplayMemberPath = "Label", SelectedValuePath = "Value" };
                    picker.ItemsSource = new[] { new CarryOption("不选", "none"), new CarryOption("长期", "long"), new CarryOption("临时", "temporary") };
                    picker.SelectedValue = turn.Mode;
                    var resetting = false;
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (resetting || picker.SelectedValue is not string value) return;
                        try
                        {
                            AiGlobalCarry.SetMode(conversation.Project, conversation.ConversationId, turn.Id, value);
                            turn.Mode = value;
                            if (string.Equals(System.IO.Path.GetFullPath(conversation.Project), System.IO.Path.GetFullPath(_project), StringComparison.OrdinalIgnoreCase))
                            {
                                var local = _book.Conversations.FirstOrDefault(c => c.Id == conversation.ConversationId);
                                if (local is not null) { local.CommentCarry[turn.Id] = value; Dirty(); }
                            }
                        }
                        catch (Exception ex) { resetting = true; picker.SelectedValue = turn.Mode; resetting = false; MessageBox.Show(window, ex.Message, "无法更新携带方式"); }
                    };
                    DockPanel.SetDock(picker, Dock.Left); row.Children.Add(picker);
                    row.Children.Add(new TextBlock { Text = $"{System.IO.Path.GetFileName(conversation.Project)} · {conversation.Title} · 第 {turn.Number} 轮\n{turn.Question}", ToolTip = conversation.Project, TextWrapping = TextWrapping.Wrap });
                    panel.Children.Add(row);
                }
            }
            window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize; window.AllowsTransparency = true; window.Background = System.Windows.Media.Brushes.Transparent;
            var frame = new Border { CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), ClipToBounds = true };
            frame.SetResourceReference(Border.BackgroundProperty, "EditorBackground"); frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new Grid();
            var title = new TextBlock { Text = "选择携带的轮次", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var close = new Button { Content = "×", Width = 24, Height = 24, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0), ToolTip = "关闭" };
            close.Click += (_, _) => window.Close();
            window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { window.Close(); e.Handled = true; } };
            header.Children.Add(title); header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) window.DragMove(); };
            var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroller, 1); layout.Children.Add(header); layout.Children.Add(scroller); frame.Child = layout; window.Content = frame;
            window.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Window NewDocumentDialog(string title, double width, double height) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 350, MinHeight = 250,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this)
    };

    private sealed record CarryOption(string Label, string Value);
}
