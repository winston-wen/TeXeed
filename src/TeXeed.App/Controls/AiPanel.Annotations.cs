using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    public event EventHandler? AnnotationsChanged;
    internal IReadOnlyList<AiEntry> AnnotationEntries => _book.Entries;
    internal bool IsMarking { get; set; }
    private readonly Dictionary<MarkdownView, AiAnnotationLayer> _answerLayers = [];
    private void AnnotationChanged(object? sender, PropertyChangedEventArgs e) => Dirty();
    internal void RemoveAnnotation(AiEntry entry)
    {
        if (entry.State == "提交中") return;
        _book.Entries.Remove(entry); entry.PropertyChanged -= AnnotationChanged; Rebind(); Dirty();
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
        var layer = new AiAnnotationLayer(view, () => AnnotationEntries, entry =>
        {
            if (view.DataContext is not AiSegment segment || AnswerTurn(view) is not { } turn || entry.AnswerAnchor is not { } anchor
                || anchor.TurnId != turn.Id || anchor.SegmentId != segment.Id || anchor.Revision != view.Revision) return null;
            var pointer = view.AnnotationPointer(entry.Id, anchor);
            if (pointer is null || !pointer.HasValidLayout) return null;
            return pointer.GetCharacterRect(LogicalDirection.Forward).TopRight;
        }, RemoveAnnotation, margin: true);
        _answerLayers.Add(view, layer);
        view.PreviewMouseLeftButtonUp += Answer_MouseUp;
        var menu = new ContextMenu();
        var annotate = new MenuItem { Header = "批注所选内容" };
        annotate.Click += (_, _) => AnnotateAnswer(view);
        menu.Opened += (_, _) => annotate.IsEnabled = AnswerTurn(view) is not null;
        menu.Items.Add(annotate);
        menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = view });
        var copyAnswer = new MenuItem { Header = "复制这段回答" };
        copyAnswer.Click += (_, _) =>
        {
            try { view.CopyToClipboard(view.Document.ContentStart, view.Document.ContentEnd); }
            catch (System.Runtime.InteropServices.ExternalException ex) { ShowError(ex); }
        };
        menu.Items.Add(copyAnswer);
        view.ContextMenu = menu;
    }
    private void Answer_Unloaded(object sender, RoutedEventArgs e)
    {
        // Layers follow Loaded/Unloaded themselves; keep only currently attached answers in the panel.
        if (sender is MarkdownView view) { if (_answerLayers.Remove(view, out var layer)) layer.Dispose(); view.PreviewMouseLeftButtonUp -= Answer_MouseUp; }
    }
    private void Answer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsMarking || sender is not MarkdownView view || Keyboard.Modifiers != ModifierKeys.None) return;
        view.Dispatcher.BeginInvoke(new Action(() => AnnotateAnswer(view)));
    }
    internal void AnnotateAnswer(MarkdownView view)
    {
        if (AnswerTurn(view) is not { } turn || view.DataContext is not AiSegment segment || !_answerLayers.TryGetValue(view, out var layer)) return;
        var start = view.Selection.Start; var end = view.Selection.End;
        if (view.Selection.IsEmpty && start.Paragraph is { } paragraph) { start = paragraph.ContentStart; end = paragraph.ContentEnd; }
        var quote = view.CopySource(start, end).Trim();
        if (quote.Length == 0 || quote.Length > 40000) { StatusText.Text = "请选择不超过 4 万字符的回答内容。"; return; }
        var entry = new AiEntry { Title = "AI 回答批注", AnswerAnchor = new(turn.Id, segment.Id,
            view.Document.ContentStart.GetOffsetToPosition(start), start.GetOffsetToPosition(end), quote, view.Revision)
            { TextOffset = view.CopySource(view.Document.ContentStart, start).Length } };
        if (AddEntry(entry, false)) layer.Edit(entry);
    }
}
