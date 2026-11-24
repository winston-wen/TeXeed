using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private readonly Dictionary<string, (int First, int Last)> _foldRanges = [];

    // One virtualized item per visible row. The notebook keeps every turn and its own fold flag.
    private void SyncTranscriptRows()
    {
        var turns = _book.Turns;
        var live = _foldingLiveTurn;
        var includeLive = live?.Collapsed == true && turns.LastOrDefault()?.Id != live.Id;
        var count = turns.Count + (includeLive ? 1 : 0);
        var desired = new List<AiTurn>();
        _foldRanges.Clear();
        AiTurn At(int index) => index == turns.Count ? live! : turns[index];
        for (var i = 0; i < count; i++)
        {
            var turn = At(i);
            desired.Add(turn);
            if (!turn.Collapsed) continue;
            var first = i + 1;
            while (i + 1 < count && At(i + 1).Collapsed) i++;
            _foldRanges[turn.Id] = (first, i + 1);
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
    }

    private void UpdateFoldedView(AiTurnView view)
    {
        var number = _foldRanges.TryGetValue(view.Turn.Id, out var range) ? range.First : _book.Turns.IndexOf(view.Turn) + 1;
        if (number == 0) return;
        view.TurnNumber = number;
        view.SetFoldRange(range.Last > 0 ? range.Last : number);
    }

    private AiTurn FoldRepresentative(AiTurn turn)
    {
        if (!turn.Collapsed) return turn;
        var index = ReferenceEquals(turn, _foldingLiveTurn) ? _book.Turns.Count : _book.Turns.IndexOf(turn);
        while (index > 0 && _book.Turns[index - 1].Collapsed) turn = _book.Turns[--index];
        return turn;
    }

    private void ChangeTurnFold(AiTurn turn, FrameworkElement trigger)
    {
        var wasVisible = trigger.IsVisible && trigger.IsDescendantOf(MessagesScroll);
        var y = wasVisible ? trigger.TranslatePoint(new Point(), MessagesScroll).Y : 0;
        var byKeyboard = trigger.IsKeyboardFocusWithin;
        var live = _foldingLiveTurn;
        var liveActive = live is not null && _book.Turns.LastOrDefault()?.Id != live.Id;
        if (turn.Collapsed && _foldRanges.TryGetValue(turn.Id, out var range))
        {
            for (var i = range.First - 1; i < Math.Min(range.Last, _book.Turns.Count); i++) _book.Turns[i].Collapsed = false;
            if (liveActive && range.Last > _book.Turns.Count) live!.Collapsed = false;
        }
        else turn.Collapsed = !turn.Collapsed;

        SyncTranscriptRows();
        if (liveActive) LiveContainer.Visibility = live!.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (wasVisible && y >= 0 && y < MessagesScroll.ViewportHeight)
        {
            var representative = FoldRepresentative(turn);
            var isLive = liveActive && !turn.Collapsed && ReferenceEquals(turn, live);
            var index = isLive ? _transcriptItems.Count - 1 : _transcriptItems.IndexOf(representative);
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
                    MessagesScroll.ScrollToVerticalOffset(MessagesScroll.VerticalOffset + target.TranslatePoint(new Point(), MessagesScroll).Y - y);
                    if (byKeyboard) (isLive ? LiveSentTime : view!.FoldTarget).Focus();
                }
            }
        }
        Dirty(); ScheduleViewportCapture();
    }

    private int RevealableTurnIndex(AiTurn turn)
    {
        if (turn.Collapsed)
        {
            turn.Collapsed = false;
            SyncTranscriptRows();
            if (ReferenceEquals(turn, _foldingLiveTurn)) LiveContainer.Visibility = Visibility.Visible;
            Dirty();
        }
        return _transcriptItems.IndexOf(turn);
    }
}
