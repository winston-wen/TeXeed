using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private const string BookmarkQuestionPrefix = "@question:";
    private readonly Dictionary<(string Turn, string Segment), (string Text, IReadOnlyList<AiBookmarkFragment> Items)> _bookmarkCache = [];
    private readonly HashSet<MarkdownView> _bookmarkQuestions = [];
    private readonly Dictionary<MarkdownView, AiBookmarkMarkerLayer> _bookmarkMarkerLayers = [];
    private bool _liveBookmarkCheckpointReady;

    private AiTurn? StableLiveBookmarkQuestion()
    {
        var turn = _foldingLiveTurn;
        return _liveBookmarkCheckpointReady && turn is not null && turn.SentAnnotations.Count == 0 &&
            LiveQuestion.Markdown == turn.DisplayQuestion ? turn : null;
    }

    internal IReadOnlyList<AiBookmarkTarget> GetBookmarks()
    {
        var conversation = _book.Active;
        var results = new List<AiBookmarkTarget>();
        var activeKeys = new HashSet<(string Turn, string Segment)>();
        var turnIndices = new Dictionary<string, int>();
        var segmentIndices = new Dictionary<(string Turn, string Segment), int>();
        for (var turnIndex = 0; turnIndex < conversation.Turns.Count; turnIndex++)
        {
            var turn = conversation.Turns[turnIndex];
            turnIndices.TryAdd(turn.Id, turnIndex);
            var segments = turn.DisplaySegments;
            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                if (segment.Kind == "activity") continue;
                segmentIndices.TryAdd((turn.Id, segment.Id), segmentIndex);
                var key = (turn.Id, segment.Id); activeKeys.Add(key);
                if (!_bookmarkCache.TryGetValue(key, out var cached) || cached.Text != segment.Text)
                    _bookmarkCache[key] = cached = (segment.Text, MarkdownView.BookmarkFragments(segment.Text));
                foreach (var item in cached.Items)
                {
                    var label = $"{turnIndex + 1}:{item.Number}";
                    results.Add(new($"{turn.Id}:{segment.Id}:{item.Kind}:{item.Position}", label, item.Description,
                        turn.Id, segment.Id, item.Kind, turnIndex, segmentIndex, item.Position, null));
                }
            }
        }
        foreach (var key in _bookmarkCache.Keys.Where(key => !activeKeys.Contains(key)).ToArray()) _bookmarkCache.Remove(key);
        foreach (var bookmark in conversation.Bookmarks)
        {
            if (bookmark.Anchor is not { } anchor || string.IsNullOrWhiteSpace(bookmark.Label)) continue;
            if (!turnIndices.TryGetValue(anchor.TurnId, out var turnIndex))
            {
                if (StableLiveBookmarkQuestion()?.Id != anchor.TurnId) continue;
                turnIndex = conversation.Turns.Count;
            }
            var question = anchor.SegmentId.StartsWith(BookmarkQuestionPrefix, StringComparison.Ordinal);
            var segmentIndex = question && int.TryParse(anchor.SegmentId.AsSpan(BookmarkQuestionPrefix.Length), out var questionIndex)
                ? questionIndex - 2 : segmentIndices.GetValueOrDefault((anchor.TurnId, anchor.SegmentId), -1);
            if (question && (segmentIndex < -2 || segmentIndex >= 0) || !question && segmentIndex < 0) continue;
            results.Add(new(bookmark.Id, bookmark.Label, anchor.Quote, anchor.TurnId, anchor.SegmentId,
                AiBookmarkKind.User, turnIndex, segmentIndex, anchor.TextOffset >= 0 ? anchor.TextOffset : anchor.Offset, anchor));
        }
        return results.OrderBy(item => item.TurnIndex).ThenBy(item => item.SegmentIndex).ThenBy(item => item.Position).ToArray();
    }

    internal AiBookmarkSelection? CaptureBookmarkSelection(MarkdownView view)
    {
        if (!view.HasBookmarkSelection) return null;
        var turn = AnswerTurn(view) ?? (ReferenceEquals(view, LiveQuestion) ? StableLiveBookmarkQuestion() : null);
        if (turn is null) return null;
        string? segmentId = view.DataContext is AiSegment segment ? segment.Id : null;
        if (ReferenceEquals(view, LiveQuestion) && StableLiveBookmarkQuestion()?.Id == turn.Id) segmentId = BookmarkQuestionPrefix + "0";
        if (segmentId is null && _turnViews.TryGetValue(turn.Id, out var turnView))
        {
            var index = turnView.Questions.IndexOf(view);
            if (index >= 0) segmentId = BookmarkQuestionPrefix + index;
        }
        if (segmentId is null) return null;
        var selection = view.GetAnnotationSelection();
        if (string.IsNullOrWhiteSpace(selection.Quote) || selection.Quote.Length > 40000) return null;
        var context = view.BookmarkSelectionContext();
        var anchor = new AiAnswerAnchor(turn.Id, segmentId, selection.Offset, selection.Length, selection.Quote, view.Revision)
            { TextOffset = selection.TextOffset };
        return new(_book.Active.Id, anchor, view.BookmarkSelectionFoldKeys(), context.Before, context.After);
    }

    internal bool AddBookmarkFromSelection(AiBookmarkSelection selection, string label)
    {
        label = label.Trim();
        var conversation = _book.Active;
        if (conversation.Id != selection.ConversationId || label.Length == 0 || label.Length > 120) return false;
        var turn = conversation.Turns.FirstOrDefault(t => t.Id == selection.Anchor.TurnId) ??
            (StableLiveBookmarkQuestion()?.Id == selection.Anchor.TurnId ? StableLiveBookmarkQuestion() : null);
        if (turn is null) return false;
        if (ReferenceEquals(turn, _foldingLiveTurn))
        {
            if (StableLiveBookmarkQuestion() is null || selection.Anchor.SegmentId != BookmarkQuestionPrefix + "0" || AiStorage.Hash(turn.DisplayQuestion) != selection.Anchor.Revision) return false;
        }
        else if (!selection.Anchor.SegmentId.StartsWith(BookmarkQuestionPrefix, StringComparison.Ordinal))
        {
            var segment = turn.DisplaySegments.FirstOrDefault(s => s.Id == selection.Anchor.SegmentId);
            if (segment is null || AiStorage.Hash(segment.Text) != selection.Anchor.Revision) return false;
        }
        conversation.Bookmarks.Add(new() { Label = label, Anchor = selection.Anchor, FoldKeys = [.. selection.FoldKeys], ContextBefore = selection.ContextBefore, ContextAfter = selection.ContextAfter });
        RefreshBookmarkMarkers();
        Dirty(); SaveAnnotationChanges(); return true;
    }

    internal bool RemoveBookmark(AiBookmarkTarget bookmark)
    {
        if (bookmark.Kind != AiBookmarkKind.User || _book.Active.Bookmarks.RemoveAll(b => b.Id == bookmark.Id) == 0) return false;
        RefreshBookmarkMarkers();
        Dirty(); SaveAnnotationChanges(); return true;
    }

    internal async Task<bool> NavigateBookmarkAsync(AiBookmarkTarget bookmark)
    {
        var conversation = _book.Active;
        var turn = conversation.Turns.FirstOrDefault(t => t.Id == bookmark.TurnId);
        if (turn is null)
        {
            if (StableLiveBookmarkQuestion()?.Id != bookmark.TurnId || bookmark.Kind != AiBookmarkKind.User || bookmark.Anchor is not { } liveAnchor || LiveQuestion.Revision != liveAnchor.Revision) return false;
            VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(_transcriptItems.Count - 1);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (_disposed || _book.Active != conversation) return false;
            if (StableLiveBookmarkQuestion()?.Id != bookmark.TurnId) return await NavigateBookmarkAsync(bookmark);
            var liveSaved = conversation.Bookmarks.FirstOrDefault(item => item.Id == bookmark.Id);
            var livePointer = liveSaved is null ? null : LiveQuestion.ResolveUserBookmark(liveSaved);
            History.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (livePointer is null || LiveQuestion.BookmarkPoint(livePointer) is not { } livePoint) return false;
            var livePosition = LiveQuestion.TranslatePoint(livePoint, MessagesScroll);
            MessagesScroll.ScrollToVerticalOffset(Math.Max(0, MessagesScroll.VerticalOffset + livePosition.Y - 24));
            return true;
        }
        var index = _transcriptItems.IndexOf(turn);
        if (index < 0) return false;
        _readingRestoreVersion++; _restoringReading = false;
        VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(index);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_disposed || _book.Active != conversation || !_turnViews.TryGetValue(turn.Id, out var turnView)) return false;
        if (turn.Collapsed) turnView.Toggle();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_disposed || _book.Active != conversation) return false;
        MarkdownView? view;
        if (bookmark.SegmentId.StartsWith(BookmarkQuestionPrefix, StringComparison.Ordinal))
        {
            if (!int.TryParse(bookmark.SegmentId.AsSpan(BookmarkQuestionPrefix.Length), out var question) || question < 0 || question >= turnView.Questions.Count) return false;
            view = turnView.Questions[question];
        }
        else view = turnView.Answers.FirstOrDefault(answer => answer.DataContext is AiSegment segment && segment.Id == bookmark.SegmentId);
        if (view is null) return false;
        await view.RenderTask;
        if (_disposed || _book.Active != conversation) return false;
        TextPointer? pointer;
        if (bookmark.Kind == AiBookmarkKind.User)
        {
            if (bookmark.Anchor is not { } anchor || view.Revision != anchor.Revision) return false;
            var saved = conversation.Bookmarks.FirstOrDefault(b => b.Id == bookmark.Id);
            if (saved is null) return false;
            view.RevealBookmarkFolds(saved.FoldKeys);
            pointer = view.ResolveUserBookmark(saved);
            if (pointer is null && saved.FoldKeys.Count == 0)
            {
                view.RevealAllBookmarkFolds();
                pointer = view.ResolveUserBookmark(saved);
            }
        }
        else pointer = view.BookmarkBlock(bookmark.Kind, bookmark.Position) is TextElement block ? block.ContentStart : null;
        if (pointer is null) return false;
        History.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_disposed || _book.Active != conversation || view.BookmarkPoint(pointer) is not { } point) return false;
        var position = view.TranslatePoint(point, MessagesScroll);
        MessagesScroll.ScrollToVerticalOffset(Math.Max(0, MessagesScroll.VerticalOffset + position.Y - 24));
        return true;
    }

    private void BookmarkQuestionLoaded(MarkdownView view)
    {
        if (!_bookmarkQuestions.Add(view)) return;
        RefreshBookmarkMarker(view, true);
        var menu = new ContextMenu();
        var bookmark = new MenuItem { Header = "为所选内容添加标签" };
        ToolTipService.SetShowOnDisabled(bookmark, true);
        menu.Opened += (_, _) =>
        {
            var live = ReferenceEquals(view, LiveQuestion);
            var available = AnswerTurn(view) is not null || live && StableLiveBookmarkQuestion() is not null;
            bookmark.IsEnabled = view.HasBookmarkSelection && available;
            bookmark.ToolTip = !live || available ? null : !_liveBookmarkCheckpointReady ? "提问正在保存，请稍后添加标签。" :
                _foldingLiveTurn?.SentAnnotations.Count > 0 ? "带圈点的实时提问请在本轮结束后添加标签。" : "当前实时提问在本轮结束后可添加标签。";
        };
        bookmark.Click += (_, _) => { if (CaptureBookmarkSelection(view) is { } selection) RequestBookmarkLabel(selection); };
        menu.Items.Add(bookmark);
        menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = view });
        menu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll, CommandTarget = view });
        view.ContextMenu = menu;
    }

    private void BookmarkQuestionUnloaded(MarkdownView view) { _bookmarkQuestions.Remove(view); RemoveBookmarkMarker(view); }

    private void RemoveBookmarkMarker(MarkdownView view)
    {
        if (_bookmarkMarkerLayers.Remove(view, out var layer)) layer.Dispose();
    }
    private void ClearBookmarkMarkers()
    {
        foreach (var layer in _bookmarkMarkerLayers.Values) layer.Dispose();
        _bookmarkMarkerLayers.Clear();
    }
    private void RefreshBookmarkMarkers()
    {
        foreach (var view in _answerLayers.Keys) RefreshBookmarkMarker(view, false);
        foreach (var view in _bookmarkQuestions) RefreshBookmarkMarker(view, true);
    }
    private void RefreshBookmarkMarker(MarkdownView view, bool question)
    {
        var turn = AnswerTurn(view) ?? (ReferenceEquals(view, LiveQuestion) ? StableLiveBookmarkQuestion() : null);
        string? segment = view.DataContext is AiSegment answer ? answer.Id : null;
        if (question && ReferenceEquals(view, LiveQuestion)) segment = BookmarkQuestionPrefix + "0";
        else if (question && turn is not null && _turnViews.TryGetValue(turn.Id, out var owner))
        {
            var index = owner.Questions.IndexOf(view);
            segment = index >= 0 ? BookmarkQuestionPrefix + index : null;
        }
        var bookmarks = turn is null || segment is null ? [] : _book.Active.Bookmarks.Where(b =>
            b.Anchor is { } anchor && anchor.TurnId == turn.Id && anchor.SegmentId == segment).ToArray();
        if (bookmarks.Length == 0) { RemoveBookmarkMarker(view); return; }
        if (!_bookmarkMarkerLayers.TryGetValue(view, out var layer))
            _bookmarkMarkerLayers[view] = layer = new AiBookmarkMarkerLayer(view, question);
        layer.SetBookmarks(bookmarks);
    }
}
