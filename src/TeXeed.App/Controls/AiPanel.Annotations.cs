using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    public event EventHandler? AnnotationsChanged;
    internal event EventHandler? AnswerMarkingStarted;
    internal IReadOnlyList<AiEntry> AnnotationEntries => _book.Entries;
    private bool _answerMarking;
    private Window? _answerMarkWindow;
    private const string AnswerActiveHint = "回答批注模式：选择文字或内容区域；Esc 退出。";
    private const string AnswerExitHint = "已退出回答批注模式。";
    private DispatcherTimer? _answerExitHintTimer;
    private DependencyPropertyDescriptor? _answerExitHintDescriptor;
    internal bool HasActiveOperation => _operation is not null;
    internal void RetargetProject(string path) => _project = path;
    private readonly Dictionary<MarkdownView, AiAnnotationLayer> _answerLayers = [];
    private void AnnotationChanged(object? sender, PropertyChangedEventArgs e) => Dirty();
    internal async void SaveAnnotationChanges()
    {
        try { await SaveAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }
    internal void RemoveAnnotation(AiEntry entry)
    {
        if (entry.State == "提交中" || AiSessionExcerptReferences.IsBranchOrigin(_book.Active, entry)) return;
        InvalidateAttachmentFeedback(entry);
        if (entry.HasReference && entry.HasAttachments) { RemoveReference(entry); return; }
        _book.Entries.Remove(entry); entry.PropertyChanged -= AnnotationChanged; Rebind(); Dirty();
    }
    internal void ConfirmAnnotation(AiEntry entry)
    {
        if (!_book.Entries.Contains(entry)) return;
        entry.Selected = true; entry.ReferenceSelected = true;
        Dirty();
    }
    private static AiTurn? AnswerTurn(MarkdownView view)
    {
        for (DependencyObject? current = view; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: AiTurn turn }) return turn;
        return null;
    }
    private void Answer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownView view || _answerLayers.ContainsKey(view)) return;
        view.ChatMode = true;
        view.EquationSource = AnswerTurn(view);
        view.TurnNumber = view.EquationSource is { } answerTurn ? _book.Turns.IndexOf(answerTurn) + 1 : _book.Turns.Count + 1;
        view.EquationPanel = this;
        view.RefreshEquationReferences();
        view.ImageBaseDirectory = System.IO.Path.GetDirectoryName(_project);
        if (AnswerTurn(view) is { } formTurn) ConfigureAnswerForms(view, formTurn);
        var layer = new AiAnnotationLayer(view, () => AnnotationEntries, entry =>
        {
            if (view.DataContext is not AiSegment segment || AnswerTurn(view) is not { } turn || entry.AnswerAnchor is not { } anchor
                || anchor.TurnId != turn.Id || anchor.SegmentId != segment.Id || anchor.Revision != view.Revision) return null;
            return view.AnnotationPoint(entry.Id, anchor);
        }, RemoveAnnotation, margin: true, confirm: ConfirmAnnotation, commit: SaveAnnotationChanges);
        _answerLayers.Add(view, layer);
        AddBranchLinks(view);
        RefreshBookmarkMarker(view, false);
        view.PreviewMouseLeftButtonUp += Answer_MouseUp;
        view.PreviewMouseRightButtonDown += Answer_RightMouseDown;
        view.PreviewKeyDown += Answer_KeyDown;
        view.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(Answer_QueryCursor), true);
        view.IsVisibleChanged += Answer_VisibilityChanged;
        var menu = new ContextMenu();
        var annotate = new MenuItem { Header = "批注到当前会话" };
        annotate.Click += (_, _) =>
        {
            if (view.HasBookmarkSelection) AnnotateAnswer(view);
            else SetAnswerMarking(true);
        };
        menu.Opened += (_, _) => annotate.IsEnabled = AnswerTurn(view) is not null;
        menu.Items.Add(annotate);
        var stopMarking = new MenuItem { Header = "退出批注模式", Visibility = Visibility.Collapsed };
        stopMarking.Click += (_, _) => SetAnswerMarking(false);
        menu.Opened += (_, _) => stopMarking.Visibility = _answerMarking ? Visibility.Visible : Visibility.Collapsed;
        menu.Items.Add(stopMarking);
        AddBranchMenu(view, menu);
        var bookmark = new MenuItem { Header = "添加标签" };
        ToolTipService.SetShowOnDisabled(bookmark, true);
        menu.Opened += (_, _) =>
        {
            var completed = AnswerTurn(view) is not null;
            bookmark.IsEnabled = view.HasBookmarkSelection && completed;
            bookmark.ToolTip = completed ? null : "回答生成结束后可添加标签。";
        };
        bookmark.Click += (_, _) => { if (CaptureBookmarkSelection(view) is { } selection) RequestBookmarkLabel(selection); };
        menu.Items.Add(bookmark);
        menu.Items.Add(new MenuItem { Header = "复制所选", Command = ApplicationCommands.Copy, CommandTarget = view });
        var copyParagraph = new MenuItem { Header = "复制所在段落" };
        menu.Opened += (_, _) => copyParagraph.IsEnabled = view.HasContextParagraph;
        copyParagraph.Click += (_, _) =>
        {
            try { view.CopyContextParagraph(); }
            catch (System.Runtime.InteropServices.ExternalException ex) { ShowError(ex); }
        };
        menu.Items.Add(copyParagraph);
        AddRevisionCommands(menu, () => AnswerTurn(view), view);
        view.ContextMenu = menu;
    }
    private void Answer_Unloaded(object sender, RoutedEventArgs e)
    {
        // Layers follow Loaded/Unloaded themselves; keep only currently attached answers in the panel.
        if (sender is MarkdownView view) { if (_answerLayers.Remove(view, out var layer)) layer.Dispose(); RemoveBookmarkMarker(view); view.PreviewMouseLeftButtonUp -= Answer_MouseUp; view.PreviewMouseRightButtonDown -= Answer_RightMouseDown; view.PreviewKeyDown -= Answer_KeyDown; view.RemoveHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(Answer_QueryCursor)); view.IsVisibleChanged -= Answer_VisibilityChanged; }
    }
    private static void Answer_RightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MarkdownView view) return;
        var point = e.GetPosition(view);
        view.PrepareContextSelection(point);
        view.CaptureContextParagraph(point);
    }
    private void Answer_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (sender is MarkdownView { IsVisible: true } view) AddBranchLinks(view); }
    private void Answer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_answerMarking || sender is not MarkdownView view || Keyboard.Modifiers != ModifierKeys.None || ImagePreview.IsPreviewImage(e.OriginalSource)) return;
        view.Dispatcher.BeginInvoke(new Action(() => { if (_answerMarking && view.HasBookmarkSelection) AnnotateAnswer(view); }));
    }
    internal void CancelAnswerMarking() { if (_answerMarking) SetAnswerMarking(false); }
    internal void SetAnswerMarking(bool active)
    {
        if (_answerMarking == active) return;
        CancelAnswerExitHint();
        _answerMarking = active;
        if (active)
        {
            AnswerMarkingStarted?.Invoke(this, EventArgs.Empty);
            _answerMarkWindow = Window.GetWindow(this);
            if (_answerMarkWindow is not null) _answerMarkWindow.PreviewMouseDown += AnswerMarkWindow_MouseDown;
            IsVisibleChanged += AnswerMarkVisibilityChanged;
        }
        else
        {
            if (_answerMarkWindow is not null) _answerMarkWindow.PreviewMouseDown -= AnswerMarkWindow_MouseDown;
            _answerMarkWindow = null;
            IsVisibleChanged -= AnswerMarkVisibilityChanged;
        }
        if (active) StatusText.Text = AnswerActiveHint;
        else if (IsVisible && !_disposed && (StatusText.Text == AnswerActiveHint || string.IsNullOrEmpty(StatusText.Text)))
        {
            StatusText.Text = AnswerExitHint;
            _answerExitHintDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            _answerExitHintDescriptor?.AddValueChanged(StatusText, AnswerExitHintChanged);
            Unloaded += AnswerExitHintUnloaded;
            _answerExitHintTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
            _answerExitHintTimer.Tick += AnswerExitHintElapsed;
            _answerExitHintTimer.Start();
        }
        else if (StatusText.Text == AnswerActiveHint) StatusText.Text = "";
        Mouse.UpdateCursor();
    }
    private void AnswerExitHintChanged(object? sender, EventArgs e) => CancelAnswerExitHint();
    private void AnswerExitHintUnloaded(object sender, RoutedEventArgs e)
    {
        var clear = StatusText.Text == AnswerExitHint;
        CancelAnswerExitHint();
        if (clear) StatusText.Text = "";
    }
    private void AnswerExitHintElapsed(object? sender, EventArgs e)
    {
        var clear = !_disposed && !_answerMarking && StatusText.Text == AnswerExitHint;
        CancelAnswerExitHint();
        if (clear) StatusText.Text = "";
    }
    private void CancelAnswerExitHint()
    {
        if (_answerExitHintTimer is not null)
        {
            _answerExitHintTimer.Stop();
            _answerExitHintTimer.Tick -= AnswerExitHintElapsed;
            _answerExitHintTimer = null;
        }
        _answerExitHintDescriptor?.RemoveValueChanged(StatusText, AnswerExitHintChanged);
        _answerExitHintDescriptor = null;
        Unloaded -= AnswerExitHintUnloaded;
    }
    private void AnswerMarkWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node switch
             { System.Windows.Media.Visual visual => System.Windows.Media.VisualTreeHelper.GetParent(visual), FrameworkContentElement content => content.Parent, ContentElement content => ContentOperations.GetParent(content), _ => LogicalTreeHelper.GetParent(node) })
            if (ReferenceEquals(node, ReadingArea)) return;
        CancelAnswerMarking();
    }
    private void AnswerMarkVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (!IsVisible) CancelAnswerMarking(); }
    private void Answer_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is MarkdownView view && (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            view.CaptureKeyboardContextParagraph();
        if (!_answerMarking || e.Key != Key.Escape) return;
        SetAnswerMarking(false); e.Handled = true;
    }
    private void Answer_QueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (!_answerMarking || sender is not MarkdownView view || AnswerTurn(view) is null) return;
        e.Cursor = Cursors.Pen; e.Handled = true;
    }
    internal void AnnotateAnswer(MarkdownView view)
    {
        if (AnswerTurn(view) is not { } turn || view.DataContext is not AiSegment segment || !_answerLayers.TryGetValue(view, out var layer)) return;
        if (!view.HasBookmarkSelection) { StatusText.Text = "请先选择需要批注的内容。"; return; }
        var selection = view.GetAnnotationSelection();
        var quote = selection.Quote.Trim();
        if (quote.Length == 0 || quote.Length > 40000) { StatusText.Text = "请选择不超过 4 万字符的回答内容。"; return; }
        var preview = view.SelectedReferenceObject();
        var context = view.BookmarkSelectionContext();
        var table = view.SelectedReferenceTableMarkdown();
        var entry = new AiEntry { Title = "AI 回答批注", AnswerAnchor = new(turn.Id, segment.Id,
            selection.Offset, selection.Length, quote, view.Revision)
            { TextOffset = selection.TextOffset, PreviewKind = preview.Kind, PreviewSource = preview.Source,
              PreviewImageUri = preview.Kind == "image" ? view.SelectedReferenceImageUri() : null, PreviewTableMarkdown = table,
              PreviewParts = preview.Kind is null && table is null ? view.SelectedReferenceParts() : null,
              PreviewNumber = preview.Number, PreviewBefore = context.Before, PreviewAfter = context.After } };
        if (AddEntry(entry, false)) layer.Edit(entry);
    }
}
