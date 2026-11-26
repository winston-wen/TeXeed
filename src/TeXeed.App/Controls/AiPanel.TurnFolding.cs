using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<string, (int First, int Last, bool Compacted)> _foldRanges = [];

    private int TranscriptTurnCount => _book.Turns.Count + (PendingLiveTurn is null ? 0 : 1);
    private AiTurn? PendingLiveTurn => _foldingLiveTurn is { } turn && _book.Turns.LastOrDefault()?.Id != turn.Id ? turn : null;
    private AiTurn TranscriptTurn(int index) => index < _book.Turns.Count ? _book.Turns[index] : PendingLiveTurn!;

    // One virtualized item per visible row. The notebook keeps every turn and its own fold flag.
    private void SyncTranscriptRows()
    {
        var turns = _book.Turns;
        var live = PendingLiveTurn;
        var count = turns.Count + (live?.Collapsed == true ? 1 : 0);
        var desired = new List<AiTurn>();
        AiTurn At(int index) => index < turns.Count ? turns[index] : live!;
        _foldRanges.Clear();
        // Deleting one of two compacted turns can leave a single member behind.
        for (var i = 0; i < count; i++)
        {
            var turn = At(i);
            if (turn.FoldGroup is not { } group) continue;
            if (!turn.Collapsed || group.Length == 0 ||
                (i == 0 || At(i - 1) is not { Collapsed: true } left || left.FoldGroup != group) &&
                (i == count - 1 || At(i + 1) is not { Collapsed: true } right || right.FoldGroup != group))
                turn.FoldGroup = null;
        }
        for (var i = 0; i < count; i++)
        {
            var turn = At(i);
            desired.Add(turn);
            if (!turn.Collapsed) continue;
            var first = i;
            while (i + 1 < count && At(i + 1) is { Collapsed: true } next && next.FoldGroup == turn.FoldGroup) i++;
            if (i == first) continue;
            if (turn.FoldGroup is not null) _foldRanges[turn.Id] = (first, i, true);
            else
            {
                for (var n = first + 1; n <= i; n++) desired.Add(At(n));
                var range = (first, i, false);
                _foldRanges[turn.Id] = range;
                _foldRanges[At(i).Id] = range;
            }
        }

        // Preserve unchanged presenters on both sides, including any open editors and previews.
        var oldCount = _transcriptItems.Count - 2;
        var prefix = 0;
        while (prefix < desired.Count && prefix < oldCount && ReferenceEquals(desired[prefix], _transcriptItems[prefix + 1])) prefix++;
        var suffix = 0;
        while (suffix < desired.Count - prefix && suffix < oldCount - prefix &&
            ReferenceEquals(desired[desired.Count - 1 - suffix], _transcriptItems[oldCount - suffix])) suffix++;
        for (var i = oldCount - suffix; i > prefix; i--) _transcriptItems.RemoveAt(i);
        for (var i = prefix; i < desired.Count - suffix; i++) _transcriptItems.Insert(i + 1, desired[i]);
        foreach (var view in _turnViews.Values) UpdateFoldedView(view);
        RefreshConversationEquationReferences();
    }

    private void UpdateFoldedView(AiTurnView view)
    {
        var hasRange = _foldRanges.TryGetValue(view.Turn.Id, out var range);
        var number = hasRange && range.Compacted ? range.First + 1 :
            ReferenceEquals(view.Turn, PendingLiveTurn) ? TranscriptTurnCount : _book.Turns.IndexOf(view.Turn) + 1;
        if (number == 0) return;
        view.TurnNumber = number;
        view.SetFoldRange(range.Compacted ? range.Last + 1 : number, offerCompact: hasRange && !range.Compacted);
        view.SetFoldRunSpacing(view.Turn is { Collapsed: true, FoldGroup: null } && number < TranscriptTurnCount &&
            TranscriptTurn(number) is { Collapsed: true, FoldGroup: null });
    }

    private AiTurn FoldRepresentative(AiTurn turn)
    {
        if (!turn.Collapsed || turn.FoldGroup is not { } group) return turn;
        var index = ReferenceEquals(turn, PendingLiveTurn) ? _book.Turns.Count : _book.Turns.IndexOf(turn);
        while (index > 0 && TranscriptTurn(index - 1) is { Collapsed: true } previous && previous.FoldGroup == group)
        {
            turn = previous;
            index--;
        }
        return turn;
    }

    private void SplitFoldGroup(AiTurn turn)
    {
        if (turn.FoldGroup is not { } group) return;
        var first = FoldRepresentative(turn);
        var index = ReferenceEquals(first, PendingLiveTurn) ? _book.Turns.Count : _book.Turns.IndexOf(first);
        var count = TranscriptTurnCount;
        for (; index >= 0 && index < count && TranscriptTurn(index) is { Collapsed: true } member && member.FoldGroup == group; index++)
            member.FoldGroup = null;
    }

    private void ChangeTurnFold(AiTurn turn, FrameworkElement trigger)
    {
        if (turn.Collapsed && _foldRanges.TryGetValue(turn.Id, out var range) && range.Compacted)
        {
            ChangeFoldGroup(turn, trigger);
            return;
        }
        var anchor = CaptureFoldAnchor(trigger);
        turn.Collapsed = !turn.Collapsed;
        turn.FoldGroup = null;
        if (turn.Collapsed)
        {
            var index = ReferenceEquals(turn, PendingLiveTurn) ? _book.Turns.Count : _book.Turns.IndexOf(turn);
            var count = TranscriptTurnCount;
            var left = index > 0 && TranscriptTurn(index - 1).Collapsed ? TranscriptTurn(index - 1).FoldGroup : null;
            var right = index >= 0 && index + 1 < count && TranscriptTurn(index + 1).Collapsed ? TranscriptTurn(index + 1).FoldGroup : null;
            turn.FoldGroup = left ?? right;
            if (left is not null && right is not null && left != right)
                for (var i = index + 1; i < count && TranscriptTurn(i) is { Collapsed: true } member && member.FoldGroup == right; i++)
                    member.FoldGroup = left;
        }
        FinishFoldChange(FoldRepresentative(turn), anchor);
    }

    private void ChangeFoldGroup(AiTurn turn, FrameworkElement trigger)
    {
        if (!turn.Collapsed || !_foldRanges.TryGetValue(turn.Id, out var range)) return;
        var anchor = CaptureFoldAnchor(trigger);
        var group = range.Compacted ? null : Guid.NewGuid().ToString("N");
        for (var i = range.First; i <= range.Last; i++) TranscriptTurn(i).FoldGroup = group;
        FinishFoldChange(TranscriptTurn(range.First), anchor);
    }

    private (double Y, bool Focus, bool Hint) CaptureFoldAnchor(FrameworkElement trigger)
    {
        CancelReadingRestore();
        CancelLayoutAnchor();
        var hint = trigger is Button;
        var row = hint && trigger is Button { Tag: FrameworkElement divider } ? divider : trigger;
        var y = row.IsVisible && row.IsDescendantOf(MessagesScroll) ? row.TranslatePoint(new Point(), MessagesScroll).Y : -1;
        return (y, trigger.IsKeyboardFocusWithin, hint);
    }

    private void FinishFoldChange(AiTurn turn, (double Y, bool Focus, bool Hint) anchor)
    {
        SyncTranscriptRows();
        var live = PendingLiveTurn;
        if (live is not null) LiveContainer.Visibility = live.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (anchor.Y >= 0 && anchor.Y < MessagesScroll.ViewportHeight)
        {
            var isLive = !turn.Collapsed && ReferenceEquals(turn, live);
            var index = isLive ? _transcriptItems.Count - 1 : _transcriptItems.IndexOf(turn);
            if (index >= 0)
            {
                if (History.ItemContainerGenerator.ContainerFromIndex(index) is null)
                    VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
                History.UpdateLayout();
                // Loaded callbacks can still be queued for a newly merged range.
                var item = History.ItemContainerGenerator.ContainerFromIndex(index);
                var view = item is null || isLive ? null : VisualChildren<AiTurnView>(item).FirstOrDefault();
                if (view is not null) UpdateFoldedView(view);
                FrameworkElement? target = isLive ? LiveQuestion : turn.Collapsed ? view?.FoldTarget : view;
                if (target?.IsVisible == true)
                {
                    MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset + target.TranslatePoint(new Point(), MessagesScroll).Y - anchor.Y);
                    if (anchor.Focus)
                    {
                        var hint = anchor.Hint && view?.FoldGroupHint is { IsVisible: true } active ? active : null;
                        (isLive ? LiveSentTime : (FrameworkElement?)hint ?? view!.FoldTarget).Focus();
                    }
                }
            }
        }
        Dirty(); ScheduleViewportCapture();
    }

    private int RevealableTurnIndex(AiTurn turn)
    {
        if (turn.Collapsed)
        {
            SplitFoldGroup(turn);
            turn.Collapsed = false;
            SyncTranscriptRows();
            if (ReferenceEquals(turn, _foldingLiveTurn)) LiveContainer.Visibility = Visibility.Visible;
            Dirty();
        }
        return _transcriptItems.IndexOf(turn);
    }
}
