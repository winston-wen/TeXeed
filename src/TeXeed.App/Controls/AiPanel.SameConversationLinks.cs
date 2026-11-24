using System.Windows;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private sealed record SameConversationLink(string Id, string TargetTurnId, int TargetNumber, AiAnswerAnchor Anchor, string? Before, string? After);
    private Dictionary<(string Turn, string Segment), List<SameConversationLink>>? _sameConversationLinks;

    private void InvalidateSameConversationLinks() => _sameConversationLinks = null;

    private IReadOnlyList<SameConversationLink> SameConversationLinks(string turnId, string segmentId)
    {
        if (_sameConversationLinks is null)
        {
            var index = new Dictionary<(string, string), List<SameConversationLink>>();
            var active = _book.Active;
            var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var conversation in _book.Conversations)
                foreach (var turn in conversation.Turns)
                    if (!owners.TryAdd(turn.Id, conversation.Id) && owners[turn.Id] != conversation.Id) owners[turn.Id] = null;
            var sourceNumbers = active.Turns.Select((turn, i) => (turn.Id, Number: i + 1))
                .GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Number, StringComparer.Ordinal);
            void AddTurn(AiTurn target, int number)
            {
                for (var i = 0; i < target.SentAnnotations.Count; i++)
                {
                    var entry = target.SentAnnotations[i];
                    if (entry.AnswerAnchor is not { } anchor || !sourceNumbers.TryGetValue(anchor.TurnId, out var sourceNumber) || sourceNumber >= number) continue;
                    string? before = anchor.PreviewBefore, after = anchor.PreviewAfter;
                    if (entry.AdditionalData?.ContainsKey(AiSessionExcerptReferences.MetadataKey) == true)
                    {
                        if (!AiSessionExcerptReferences.TryRead(entry, out var reference) || reference!.SourceConversationId != active.Id) continue;
                        before = reference.ContextBefore ?? before; after = reference.ContextAfter ?? after;
                    }
                    else if (!owners.TryGetValue(anchor.TurnId, out var owner) || owner != active.Id) continue;
                    var key = (anchor.TurnId, anchor.SegmentId);
                    if (!index.TryGetValue(key, out var links)) index.Add(key, links = []);
                    links.Add(new("turn:" + target.Id + ":" + i, target.Id, number, anchor, before, after));
                }
            }
            for (var i = 0; i < active.Turns.Count; i++) AddTurn(active.Turns[i], i + 1);
            if (_liveBookmarkCheckpointReady && _foldingLiveTurn is { } pending && active.Turns.All(turn => turn.Id != pending.Id))
                AddTurn(pending, active.Turns.Count + 1);
            _sameConversationLinks = index;
        }
        return _sameConversationLinks.TryGetValue((turnId, segmentId), out var result) ? result : [];
    }

    private async void OpenSameConversationTurn(string turnId)
    {
        try { if (!await NavigateSameConversationTurnAsync(turnId)) StatusText.Text = "无法定位目标轮次。"; }
        catch (Exception ex) { ShowError(ex); }
    }

    internal async Task<bool> NavigateSameConversationTurnAsync(string turnId)
    {
        CancelLayoutAnchor();
        var conversation = _book.Active;
        if (_liveBookmarkCheckpointReady && _foldingLiveTurn is { } pending && pending.Id == turnId && conversation.Turns.All(t => t.Id != turnId))
        { CancelReadingRestore(); RevealableTurnIndex(pending); RevealLiveTurn(); return true; }
        var turn = conversation.Turns.FirstOrDefault(item => item.Id == turnId);
        var index = turn is null ? -1 : RevealableTurnIndex(turn);
        if (turn is null || index < 0) return false;
        CancelReadingRestore();
        var version = _readingRestoreVersion;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != version || !IsVisible) return true;
        VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != version || !IsVisible) return true;
        if (_disposed || _book.Active != conversation || !_turnViews.TryGetValue(turn.Id, out var view)) return false;
        History.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_readingRestoreVersion != version || !IsVisible) return true;
        if (_disposed || _book.Active != conversation || !view.IsLoaded) return false;
        var top = view.TranslatePoint(new Point(), MessagesScroll).Y;
        MessagesScroll.ScrollToVerticalOffset(Math.Clamp(MessagesScroll.VerticalOffset + top, 0, MessagesScroll.ScrollableHeight));
        return true;
    }
}
