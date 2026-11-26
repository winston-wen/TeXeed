using System.Text.Json;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private sealed record SourceReference(string Id, string Label, AiAnswerAnchor Anchor, string? Before, string? After);

    private IEnumerable<SourceReference> RevisionReferences(AiTurn turn)
    {
        foreach (var branch in _book.Conversations.Where(c => c.ParentConversationId == ConversationId && c.BranchAnchor?.TurnId == turn.Id))
            yield return new(branch.Id, branch.Title, branch.BranchAnchor!, branch.BranchContextBefore, branch.BranchContextAfter);
        foreach (var segment in AiRevisionText.Segments(turn))
            foreach (var link in SameConversationLinks(turn.Id, segment.Id))
                yield return new(link.Id, "[" + link.TargetNumber + "]", link.Anchor, link.Before, link.After);
        foreach (var mark in _book.Active.Bookmarks.Where(mark => mark.Anchor?.TurnId == turn.Id && !mark.Anchor.SegmentId.StartsWith(BookmarkQuestionPrefix, StringComparison.Ordinal)))
            yield return new("bookmark:" + mark.Id, mark.Label, mark.Anchor!, mark.ContextBefore, mark.ContextAfter);
    }

    internal AiRevisionDraft BeginRevision(AiTurn turn)
    {
        var segments = AiRevisionText.Segments(turn).ToList();
        var latest = turn.RevisionHistory?.LastOrDefault();
        var rows = latest is null ? segments.SelectMany(s => AiRevisionText.Parse(s.Id, s.Text)).ToList() : AiRevisionText.Clone(latest.Rows);
        if (rows.Count == 0 && segments.FirstOrDefault() is { } empty)
            rows.Add(new(empty.Id + "/0", empty.Id, "", 0, false, []));
        var refs = RevisionReferences(turn).DistinctBy(r => r.Id).ToDictionary(r => r.Id);
        var assigned = new HashSet<string>();
        foreach (var row in rows)
        {
            row.Links.RemoveAll(link => !refs.ContainsKey(link.Id));
            for (var i = 0; i < row.Links.Count; i++)
            {
                var link = row.Links[i];
                row.Links[i] = link with { Label = refs[link.Id].Label };
                assigned.Add(link.Id);
            }
        }
        foreach (var reference in refs.Values.Where(r => !assigned.Contains(r.Id)))
        {
            var candidates = rows.Where(r => r.SegmentId == reference.Anchor.SegmentId).ToArray();
            if (candidates.Length == 0) continue;
            var (row, offset) = LocateSourceReference(candidates, reference, latest != null);
            row.Links.Add(new(reference.Id, offset, reference.Label));
        }
        foreach (var row in rows)
        {
            var ordered = row.Links.OrderBy(l => l.Offset).ToArray();
            row.Links.Clear(); row.Links.AddRange(ordered);
        }
        var sourceVersion = latest?.Id ?? AiStorage.Hash(JsonSerializer.Serialize(segments.Select(s => new[] { s.Id, s.Text })));
        var version = sourceVersion + "/" + AiStorage.Hash(JsonSerializer.Serialize(rows.SelectMany(r => r.Links).Select(l => l.Id)));
        return new(turn.Id, version, rows);
    }

    private static (AiRevisionRow Row, int Offset) LocateSourceReference(AiRevisionRow[] candidates, SourceReference reference, bool revision)
    {
        var anchor = reference.Anchor;
        var quote = anchor.Quote.Trim();
        var before = reference.Before ?? anchor.PreviewBefore ?? "";
        var after = reference.After ?? anchor.PreviewAfter ?? "";
        var joined = string.Join("\n\n", candidates.Select(r => r.Text));
        var sourceOffset = Math.Clamp(Math.Max(0, anchor.TextOffset), 0, joined.Length);
        var end = !reference.Id.StartsWith("bookmark:", StringComparison.Ordinal);
        if (anchor is { PreviewKind: "formula", PreviewSource.Length: > 0 })
        {
            // The copied TeX can contain an automatic tag that is absent from the source.
            var matches = AiEquationIndex.Read(joined).Where(t => t.Display && t.Source == anchor.PreviewSource).ToArray();
            var exact = matches.Length == 1 ? matches : matches.Where(t => t.Number == anchor.PreviewNumber).ToArray();
            if (exact.Length == 1)
            {
                var position = exact[0].OriginalOffset + (end ? exact[0].OriginalLength : 0);
                foreach (var candidate in candidates)
                {
                    if (position <= candidate.Text.Length) return (candidate, Math.Max(0, position));
                    position -= candidate.Text.Length + 2;
                }
            }
        }
        int Match(string text)
        {
            var best = -1; var score = -1; var nearest = int.MaxValue;
            for (var i = quote.Length == 0 ? -1 : text.IndexOf(quote, StringComparison.Ordinal); i >= 0; i = text.IndexOf(quote, i + 1, StringComparison.Ordinal))
            {
                var left = 0; var right = 0;
                while (left < before.Length && left < i && before[before.Length - left - 1] == text[i - left - 1]) left++;
                while (right < after.Length && i + quote.Length + right < text.Length && after[right] == text[i + quote.Length + right]) right++;
                var distance = Math.Abs(sourceOffset - i);
                if (left + right < score || left + right == score && distance >= nearest) continue;
                score = left + right; nearest = distance; best = i;
            }
            return best;
        }
        var found = Match(joined);
        if (found < 0 || joined.Contains("](", StringComparison.Ordinal) || joined.Contains("][", StringComparison.Ordinal))
        {
            var visible = candidates.Select(row => AiRevisionText.SelectionText(row.Text, revision: revision)).ToArray();
            var position = Match(string.Join("\r\n", visible.Select(item => item.Text)));
            if (position >= 0)
            {
                position += end ? quote.Length - 1 : 0;
                for (var i = 0; i < visible.Length; i++)
                {
                    var item = visible[i];
                    if (position < item.Text.Length && position >= 0)
                        return (candidates[i], Math.Clamp(end ? item.Ends[position] : item.Starts[position], 0, candidates[i].Text.Length));
                    position -= item.Text.Length + 2;
                }
            }
        }
        var offset = found >= 0 ? found + (end ? quote.Length : 0) : sourceOffset + (end ? quote.Length : 0);
        foreach (var row in candidates)
        {
            if (offset <= row.Text.Length) return (row, Math.Max(0, offset));
            offset -= row.Text.Length + 2;
        }
        return (candidates[^1], candidates[^1].Text.Length);
    }

    internal async Task CommitRevisionAsync(string turnId, string expectedRevision, IReadOnlyList<AiRevisionRow> rows, string author, string? operationId = null,
        CancellationToken cancellationToken = default, bool approved = false)
    {
        var conversation = _book.Active;
        var project = _project;
        var store = _store;
        AiTurn? replaced = null;
        AiTurn? updated = null;
        IReadOnlyList<AiRevisionEquationLinks.AliasChange> aliases = [];
        var at = -1;
        var previousTime = conversation.UpdatedAt;
        var writer = author.ToLowerInvariant();
        if (writer is not ("ai" or "user")) throw new ArgumentException("修订需标记作者。", nameof(author));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        var startingTurn = conversation.Turns.FirstOrDefault(t => t.Id == turnId)
            ?? throw new InvalidOperationException("找不到指定轮次。");
        if (!string.IsNullOrEmpty(operationId) && startingTurn.RevisionHistory?.Any(rev => rev.OperationId == operationId) == true) return;
        var initial = BeginRevision(startingTurn);
        if (initial.Version != expectedRevision) throw new InvalidOperationException("该轮的正文或引用已更新，请重新打开最新稿。");
        var mathScope = writer == "user" ? RevisionMathScope.Inline : RevisionMathScope.All;
        var proposed = AiRevisionText.Normalize(initial, rows, mathScope);
        if (writer == "user") rows = await AiRevisionInlineMath.ValidateAsync(initial.Rows, proposed, RepairRevisionInlineMathAsync, token);
        else await AiRevisionMath.CheckAsync(initial.Rows, proposed, token);
        void Apply()
        {
            token.ThrowIfCancellationRequested();
            if (_book.Active != conversation || _project != project) throw new InvalidOperationException("会话已经切换。");
            if (writer == "ai" && (conversation.Permission == AiPermission.ReadOnly ||
                conversation.Permission == AiPermission.AskBeforeShell && !approved))
                throw new UnauthorizedAccessException("当前权限不允许保存这次改稿，请根据现有权限重新提交。");
            at = conversation.Turns.FindIndex(t => t.Id == turnId);
            if (at < 0) throw new InvalidOperationException("找不到指定轮次。");
            var original = conversation.Turns[at];
            if (!string.IsNullOrEmpty(operationId) && original.RevisionHistory?.Any(rev => rev.OperationId == operationId) == true) return;
            if (writer == "ai" && original.UserRevisionDraft is not null) throw new InvalidOperationException("该轮有未完成的手动整理，请先保存或取消手动整理。");
            var draft = BeginRevision(original);
            if (draft.Version != expectedRevision) throw new InvalidOperationException("该轮的正文或引用已更新，请重新打开最新稿。");
            var normalized = AiRevisionText.Normalize(draft, rows, mathScope);
            bool Same(IEnumerable<AiRevisionRow> left, IEnumerable<AiRevisionRow> right) =>
                left.Select(row => (row.Id, row.Text, row.Level, row.SegmentId, Links: JsonSerializer.Serialize(row.Links))).SequenceEqual(
                    right.Select(row => (row.Id, row.Text, row.Level, row.SegmentId, Links: JsonSerializer.Serialize(row.Links))));
            var revised = !Same(draft.Rows, normalized);
            if (!revised && (writer != "user" || original.UserRevisionDraft is null)) return;
            var segments = AiRevisionText.Segments(original).ToArray();
            var sources = segments.ToDictionary(s => s.Id, s => s.Text);
            var current = new Dictionary<string, string>(sources);
            foreach (var segment in segments)
            {
                var prior = draft.Rows.Where(r => r.SegmentId == segment.Id).ToList();
                var next = normalized.Where(r => r.SegmentId == segment.Id).ToList();
                if (!prior.Select(r => (r.Id, r.Text, r.Level)).SequenceEqual(next.Select(r => (r.Id, r.Text, r.Level))))
                {
                    var leading = System.Text.RegularExpressions.Regex.Match(segment.Text, @"\A(?:[ \t]*\r?\n)+").Value;
                    var trailing = System.Text.RegularExpressions.Regex.Match(segment.Text, @"(?:\r?\n[ \t]*)+\z", System.Text.RegularExpressions.RegexOptions.RightToLeft).Value;
                    var text = AiRevisionText.Source(next, includeMargins: false);
                    current[segment.Id] = text.Length == 0 ? "" : leading + text + trailing;
                }
            }
            var history = original.RevisionHistory?.ToList() ?? [];
            if (history.Count == 0) history.Add(new("original", "ai", original.AnsweredAt ?? original.CreatedAt, AiRevisionText.Clone(draft.Rows))
                { Sources = sources, EquationPreviews = AiRevisionEquationLinks.Capture(conversation, original) });
            var now = DateTimeOffset.UtcNow;
            if (revised) history.Add(new(Guid.NewGuid().ToString("N"), writer, now, normalized) { Sources = current, OperationId = operationId });
            var newSegments = (original.Segments.Count == 0 ? segments.AsEnumerable() : original.Segments)
                .Select(s => { var copy = s.Snapshot(); if (current.TryGetValue(s.Id, out var text)) copy.Text = text; return copy; }).ToList();
            var textChanged = current.Any(item => item.Value != sources[item.Key]);
            var answer = textChanged ? string.Join("\n\n", newSegments.Where(s => current.ContainsKey(s.Id) && s.Text.Length > 0).Select(s => s.Text)) : original.Answer;
            updated = original with { Answer = answer, Segments = newSegments, RevisionHistory = history.Count > 1 ? history : original.RevisionHistory,
                CollapsedSections = RevisionFoldKeys(original, segments, draft.Rows, normalized, current), UserRevisionDraft = null, Incomplete = original.IsIncomplete };
            if (textChanged)
            {
                aliases = AiRevisionEquationLinks.Prepare(conversation, original, updated);
                if (writer == "ai") AiRevisionMath.CheckReferences(conversation, original, updated);
            }
            if (revised)
                history[^1] = history[^1] with { EquationPreviews = AiRevisionEquationLinks.Capture(conversation, updated) };
            replaced = original; previousTime = conversation.UpdatedAt;
            conversation.Turns[at] = updated; conversation.UpdatedAt = now;
            foreach (var change in aliases) change.Owner.EquationReferenceAliases = change.After;
        }
        void Rollback()
        {
            if (replaced is null || at < 0 || at >= conversation.Turns.Count || !ReferenceEquals(conversation.Turns[at], updated)) return;
            conversation.Turns[at] = replaced; conversation.UpdatedAt = previousTime;
            foreach (var change in aliases) change.Owner.EquationReferenceAliases = change.Before;
        }
        if (store is not null) await store.SaveWithMutationAsync(Apply, Rollback);
        else
        {
            await _saveGate.WaitAsync();
            try
            {
                Apply();
                if (updated is not null && project is not null)
                {
                    var snapshot = _book.Snapshot();
                    await Task.Run(() => AiStorage.Save(project, snapshot));
                }
            }
            catch { Rollback(); throw; }
            finally { _saveGate.Release(); }
        }
        if (updated is null || _book.Active != conversation || _disposed) return;
        InvalidateSameConversationLinks(); SyncTranscriptRows(); RefreshMessageMatches(); RefreshVisibleBranchLinks();
        foreach (var view in _bookmarkQuestions) view.RefreshSnapshotOrigins(turnId);
    }

    private static Dictionary<string, bool> RevisionFoldKeys(AiTurn original, AiSegment[] segments,
        List<AiRevisionRow> before, List<AiRevisionRow> after, Dictionary<string, string> sources)
    {
        var result = new Dictionary<string, bool>(original.CollapsedSections);
        if (result.Count == 0) return result;
        foreach (var segment in segments.Where(s => s.Text != sources[s.Id]))
        {
            Dictionary<string, string> Keys(string source, IEnumerable<AiRevisionRow> rows) => rows.Where(r => r.Level > 0 && r.SegmentId == segment.Id)
                .Zip(MarkdownView.BookmarkFragments(source).Where(f => f.Kind == AiBookmarkKind.Heading), (r, f) => (r.Id, Key: segment.Id + ":" + f.Position))
                .ToDictionary(v => v.Id, v => v.Key);
            var old = Keys(segment.Text, before);
            var next = Keys(sources[segment.Id], after);
            foreach (var key in old.Values) result.Remove(key);
            foreach (var (id, key) in old)
                if (original.CollapsedSections.TryGetValue(key, out var folded) && next.TryGetValue(id, out var newKey)) result[newKey] = folded;
        }
        return result;
    }
}
