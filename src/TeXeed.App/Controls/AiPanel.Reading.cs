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
        if (active is null || active.Turn.Collapsed || active.TranslatePoint(new Point(), MessagesScroll).Y >= -24)
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
    private void Entry_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AiEntry entry }) HandleEntryKey(entry, e, Keyboard.Modifiers);
    }
    internal void HandleEntryKey(AiEntry entry, KeyEventArgs e, ModifierKeys modifiers)
    {
        if (!IsSendGesture(e.Key, modifiers)) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        ConfirmAnnotation(entry);
        EntriesPopup.IsOpen = false;
        SaveAnnotationChanges();
    }
    private void Turn_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AiTurnView view) return;
        view.TurnNumber = _book.Turns.IndexOf(view.Turn) + 1;
        view.ImageBaseDirectory = System.IO.Path.GetDirectoryName(_project);
        _turnViews[view.Turn.Id] = view;
        view.Unloaded -= Turn_Unloaded; view.Unloaded += Turn_Unloaded;
        ScheduleReadingContext();
        view.Changed = () => { Dirty(); ScheduleReadingContext(); };
        view.SourceRequested = OpenChatSource;
        view.PreviewChanged = _ => { foreach (var other in VisualChildren<AiTurnView>(History).Where(v => v != view)) other.ClosePreview(); };
        view.AnswerReady = answer => Answer_Loaded(answer, new RoutedEventArgs());
        view.AnswerGone = answer => Answer_Unloaded(answer, new RoutedEventArgs());
        foreach (var answer in view.Answers.Where(a => a.IsLoaded)) Answer_Loaded(answer, new RoutedEventArgs());
    }
    private void Turn_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is AiTurnView view && _turnViews.GetValueOrDefault(view.Turn.Id) == view) _turnViews.Remove(view.Turn.Id);
    }
}
