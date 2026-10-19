using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    internal static bool IsAnnotationInput(IInputElement? input) => input is FrameworkElement { Tag: AiEntry } or FrameworkElement { DataContext: AiEntry };
    private bool _restoringReading;
    private readonly Dictionary<string, AiTurnView> _turnViews = [];
    private bool _readingQueued;
    private int _readingRestoreVersion;
    private AiTurnView? ActiveReadingTurn() => _turnViews.Values.Where(v => v.IsLoaded && v.IsVisible)
        .OrderBy(v => v.TranslatePoint(new Point(), MessagesScroll).Y)
        .FirstOrDefault(v => v.TranslatePoint(new Point(0, v.ActualHeight), MessagesScroll).Y > 0);
    private void CaptureReadingAnchor()
    {
        if (_restoringReading) return;
        if (ActiveReadingTurn() is { } active)
        {
            _book.Active.ReadingTurnId = active.Turn.Id;
            _book.Active.ReadingTurnOffset = Math.Max(0, -active.TranslatePoint(new Point(), MessagesScroll).Y);
        }
    }
    private void RestoreReadingAnchor() => _ = RestoreReadingAnchorAsync(_book.Active, ++_readingRestoreVersion);
    private async Task RestoreReadingAnchorAsync(AiConversation conversation, int version)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (version != _readingRestoreVersion || _book.Active != conversation || _disposed) return;
            var index = conversation.Turns.FindIndex(t => t.Id == conversation.ReadingTurnId);
            if (index >= 0) VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index + 1);
            else MessagesScroll.ScrollToVerticalOffset(conversation.ReadingOffset);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (version != _readingRestoreVersion || _book.Active != conversation || _disposed) return;
            if (conversation.ReadingTurnId is { } id && _turnViews.TryGetValue(id, out var view))
            {
                await Task.WhenAll(view.Answers.Select(a => a.RenderTask));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                if (version != _readingRestoreVersion || _book.Active != conversation || _disposed) return;
                MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset + view.TranslatePoint(new Point(), MessagesScroll).Y + conversation.ReadingTurnOffset);
            }
            _restoringReading = false; ScheduleReadingContext();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _restoringReading = false; if (!_disposed) ShowError(ex); }
    }
    private void ScheduleReadingContext()
    {
        if (_readingQueued || _disposed) return;
        _readingQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        { _readingQueued = false; if (!_disposed) UpdateReadingContext(); }));
    }
    private void UpdateReadingContext()
    {
        if (!IsLoaded || ReadingContext is null) return;
        // Only native virtualized containers in/near the viewport are registered.
        // Never walk document descendants or force layout of off-screen history.
        var active = ActiveReadingTurn();
        CaptureReadingAnchor();
        if (active is null || active.TranslatePoint(new Point(), MessagesScroll).Y >= -24)
        { ReadingContext.Visibility = Visibility.Collapsed; return; }
        ReadingContextTitle.Text = (active.Turn.Prompt ?? active.Turn.DisplayQuestion).Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "圈点提问";
        if (string.IsNullOrWhiteSpace(ReadingContextTitle.Text)) ReadingContextTitle.Text = "圈点提问";
        ReadingContextTitle.ToolTip = ReadingContextTitle.Text;
        ReadingContextAnnotations.Text = active.Turn.SentAnnotations.Count == 0 ? "本轮没有圈点" : string.Join("   ", active.Turn.SentAnnotations.Select((e, n) => $"{n + 1}. {AiTurnView.AnnotationSummary(e)}"));
        ReadingContextAnnotations.ToolTip = ReadingContextAnnotations.Text;
        ReadingContext.Visibility = Visibility.Visible;
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private void MigrateSnapshots()
    {
        foreach (var conversation in _book.Conversations)
        {
            for (var i = 0; i < conversation.Turns.Count; i++)
            {
                var turn = conversation.Turns[i];
                if (turn.Prompt is not null || turn.SentAnnotations.Count > 0) continue;
                var entries = conversation.Entries.Where(e => turn.Question.Contains(e.DisplayTitle + "：", StringComparison.Ordinal)).ToArray();
                if (entries.Length == 0) continue;
                var lines = turn.Question.Split('\n').Where(line => !entries.Any(e => line.StartsWith(e.DisplayTitle + "：", StringComparison.Ordinal)));
                conversation.Turns[i] = turn with { Prompt = string.Join('\n', lines), SentAnnotations = entries.Select((e, n) => e.Snapshot(n + 1)).ToList() };
            }
            var saved = conversation.Turns.SelectMany(t => t.SentAnnotations).Select(e => e.Id).ToHashSet();
            conversation.Entries.RemoveAll(e => e.State == "已完成" && saved.Contains(e.Id));
        }
    }
    private bool _headerTemporary;
    private readonly System.Windows.Threading.DispatcherTimer _headerCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private void ApplyChatHeader()
    {
        var open = _headerTemporary || _book.Active.HeaderPinned;
        HeadingContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatHeader.MinHeight = 18;
        HeadingArrow.RenderTransform = open ? new RotateTransform(180, 5, 2) : Transform.Identity;
        HeadingPeek.SetResourceReference(BackgroundProperty, _book.Active.HeaderPinned ? "Accent" : "ChatQuestionBackground");
        System.Windows.Automation.AutomationProperties.SetName(HeadingPeek, open ? "收起对话标题" : "展开对话标题");
    }
    private void Header_MouseEnter(object sender, MouseEventArgs e)
    {
        _headerCloseTimer.Stop();
        if (_disposed || _headerTemporary) return;
        _headerTemporary = true; ApplyChatHeader();
    }
    private void Header_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_disposed || ChatHeader.IsMouseOver) return;
        _headerCloseTimer.Stop(); _headerCloseTimer.Start();
    }
    private void Header_CloseTick(object? sender, EventArgs e)
    {
        _headerCloseTimer.Stop();
        if (_disposed || ChatHeader.IsMouseOver) return;
        // Keep the target stable until a press/drag finishes, even outside the box.
        if (ChatHeader.IsMouseCaptureWithin) { _headerCloseTimer.Start(); return; }
        _headerTemporary = false; ApplyChatHeader();
    }
    private void Header_Unloaded(object sender, RoutedEventArgs e) => ResetHeaderHover();
    private void ResetHeaderHover()
    {
        _headerCloseTimer.Stop(); _headerTemporary = false; ApplyChatHeader();
    }
    private void ToggleHeaderPin()
    {
        _headerCloseTimer.Stop(); _book.Active.HeaderPinned = !_book.Active.HeaderPinned;
        _headerTemporary = ChatHeader.IsMouseOver; ApplyChatHeader(); Dirty();
    }
    private void Header_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true; if (e.IsRepeat) return;
        ToggleHeaderPin();
    }
    private void Header_Click(object sender, RoutedEventArgs e)
    {
        // Mouse single-click does not compete with hover; double-click pins the header.
        if (Keyboard.IsKeyDown(Key.Enter) || Keyboard.IsKeyDown(Key.Space)) ToggleHeaderPin();
    }
    private void Header_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ToggleHeaderPin(); e.Handled = true;
    }
    private void OpenChatSource(string reference)
    {
        var match = System.Text.RegularExpressions.Regex.Match(reference, @"^(.+?)(?::(\d+)(?:[–—-](\d+))?)?$");
        var path = match.Groups[1].Value.Replace('\\', '/');
        var candidates = (ProjectFiles?.Invoke() ?? []).Where(file => file.IsEditable && (file.RelativePath.Replace('\\', '/') == path || System.IO.Path.GetFileName(file.RelativePath) == path)).ToArray();
        if (candidates.Length != 1) { StatusText.Text = candidates.Length == 0 ? "当前项目未找到：" + path : "存在同名源码，请使用项目内完整相对路径。"; return; }
        var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = candidates[0].Content, IsReadOnly = true, ShowLineNumbers = true, FontFamily = Composer.FontFamily, FontSize = 14, WordWrap = true };
        editor.SetResourceReference(BackgroundProperty, "EditorBackground"); editor.SetResourceReference(ForegroundProperty, "Ink");
        var preview = new Window { Owner = Window.GetWindow(this), Title = "当前项目源码 · " + reference, Width = 760, Height = 600, Content = editor };
        preview.Show(); if (int.TryParse(match.Groups[2].Value, out var line)) { line = Math.Clamp(line, 1, editor.Document.LineCount); editor.ScrollToLine(line); editor.Select(editor.Document.GetLineByNumber(line).Offset, editor.Document.GetLineByNumber(line).Length); }
    }
    private void Entries_Click(object sender, RoutedEventArgs e) => EntriesPopup.IsOpen = !EntriesPopup.IsOpen;
    private void CloseEntries_Click(object sender, RoutedEventArgs e) => EntriesPopup.IsOpen = false;
    private async void Entry_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSendGesture(e.Key, Keyboard.Modifiers)) return;
        e.Handled = true;
        if (!e.IsRepeat && sender is FrameworkElement { DataContext: AiEntry entry }) await SubmitAsync(entry);
    }
    internal Task SubmitEntryAsync(AiEntry entry) => SubmitAsync(entry);
    private void Turn_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AiTurnView view) return;
        _turnViews[view.Turn.Id] = view;
        view.Unloaded -= Turn_Unloaded; view.Unloaded += Turn_Unloaded;
        ScheduleReadingContext();
        view.Changed = Dirty;
        view.SourceRequested = OpenChatSource;
        view.PreviewChanged = _ => { foreach (var other in VisualChildren<AiTurnView>(History).Where(v => v != view)) other.ClosePreview(); };
        view.Refill = () => RetryTurn_Click(view, new RoutedEventArgs());
        view.Delete = async () => { try { await DeleteTurnAsync(view.Turn.Id); } catch (Exception ex) { ShowError(ex); } };
        view.AnswerReady = answer => Answer_Loaded(answer, new RoutedEventArgs());
        view.AnswerGone = answer => Answer_Unloaded(answer, new RoutedEventArgs());
        foreach (var answer in view.Answers.Where(a => a.IsLoaded)) Answer_Loaded(answer, new RoutedEventArgs());
    }
    private void Turn_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is AiTurnView view && _turnViews.GetValueOrDefault(view.Turn.Id) == view) _turnViews.Remove(view.Turn.Id);
    }
}
