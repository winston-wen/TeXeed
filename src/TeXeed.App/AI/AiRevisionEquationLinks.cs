namespace TeXeed.App.AI;

internal static class AiRevisionEquationLinks
{
    internal sealed record AliasChange(AiTurn Owner, Dictionary<string, string>? Before, Dictionary<string, string>? After);
    private sealed record Reference(string Key, string Segment);
    private sealed record Formula(AiEquationTarget Target, string Row, string? SplitFrom);

    internal static Dictionary<int, AiHistoricalEquation>? Capture(AiConversation conversation, AiTurn owner)
    {
        Dictionary<int, AiHistoricalEquation>? result = null;
        var segments = AiRevisionText.Segments(owner).ToArray();
        var starts = new Dictionary<string, int>();
        var offset = 0;
        foreach (var segment in segments)
        {
            starts[segment.Id] = offset;
            offset += segment.Text.Length + 2; // ShowRevisionHistory joins segments with two line breaks.
        }
        foreach (var segment in segments)
        {
            foreach (var token in AiEquationIndex.Read(segment.Text))
            {
                if (token.Reference is not { } reference) continue;
                var qualified = AiEquationIndex.TrySplit(reference, out _, out var local);
                var number = 0;
                var target = qualified ? AiEquationIndex.TargetTurn(conversation.Turns, owner, reference, out number) : owner;
                if (target?.Id == owner.Id) target = owner;
                var key = AiEquationIndex.EffectiveKey(owner, reference, local, segment.Id);
                var formula = target is null ? null : AiEquationIndex.Resolve(target, key, qualified ? null : segment.Id);
                var sameTurn = formula?.TurnId == owner.Id;
                var caption = formula is null ? reference + " ?" : sameTurn ? formula.Equation.Number! : $"{number}:{formula.Equation.Number}";
                (result ??= [])[starts[segment.Id] + token.OriginalOffset] = new(reference, caption, formula?.Equation.Source,
                    sameTurn ? starts[formula!.SegmentId] + formula.Equation.OriginalOffset : null);
            }
        }
        return result;
    }

    private static IEnumerable<Reference> References(AiTurn turn)
    {
        // Sent annotation excerpts are snapshots. Only the user's prompt and the saved
        // answer contain live eqrefs that can follow a revised formula.
        IEnumerable<Reference> Read(string text, string segment) => AiEquationIndex.Read(text)
            .Where(t => t.Reference is not null).Select(t => new Reference(t.Reference!, segment));
        return Read(turn.Prompt ?? turn.Question, "").Concat(AiRevisionText.Segments(turn)
            .SelectMany(s => Read(s.Text, s.Id))).Concat(turn.UserRevisionDraft?.Rows.SelectMany(r => Read(r.Text, r.SegmentId)) ?? []).Distinct();
    }

    private static Formula[] Formulas(AiTurn turn)
    {
        var formulas = new List<Formula>();
        foreach (var segment in AiRevisionText.Segments(turn))
        {
            var tokens = AiEquationIndex.Read(segment.Text);
            if (!tokens.Any(t => t.Display)) continue;
            var rows = turn.RevisionHistory?.LastOrDefault()?.Rows.Where(r => r.SegmentId == segment.Id)
                ?? AiRevisionText.Parse(segment.Id, segment.Text);
            var ranges = AiRevisionText.DisplayRanges(segment.Text, rows).ToArray();
            foreach (var token in tokens.Where(t => t.Display))
            {
                var row = ranges.FirstOrDefault(range => range.Start <= token.OriginalOffset &&
                    token.OriginalOffset + token.OriginalLength <= range.End).Row;
                formulas.Add(new(new(turn.Id, segment.Id, token), row?.Id ?? "", row?.SplitFromRow));
            }
        }
        return [.. formulas];
    }

    private static bool Matches(Formula f, AiEquationTarget? target) => target is not null &&
        f.Target.SegmentId == target.SegmentId && f.Target.Equation.OriginalOffset == target.Equation.OriginalOffset;

    private static Formula? Counterpart(Formula original, Formula[] before, Formula[] after)
    {
        var token = original.Target.Equation;
        var row = after.Where(f => f.Row.Length > 0 && f.Row == original.Row ||
            f.SplitFrom == original.Row && !before.Any(previous => previous.Row == f.Row)).ToArray();
        if (row.Length > 1 && row.Any(f => f.SplitFrom == original.Row))
        {
            var labeled = row.Where(f => token.Label is not null && f.Target.Equation.Label == token.Label).ToArray();
            return labeled.Length == 1 ? labeled[0] : null;
        }
        if (row.Length == 1 && before.Count(f => f.Row == original.Row) == 1) return row[0];
        var exact = row.Where(f => f.Target.Equation.Source == token.Source && f.Target.Equation.Label == token.Label).ToArray();
        if (exact.Length == 1) return exact[0];
        // Moving an unchanged, uniquely labeled equation to another paragraph is safe.
        // An unrelated equation in a different paragraph cannot inherit its label silently.
        if (token.Label is null) return null;
        exact = after.Where(f => f.Target.Equation.Label == token.Label && f.Target.Equation.Source == token.Source).ToArray();
        return exact.Length == 1 ? exact[0] : null;
    }

    internal static IReadOnlyList<AliasChange> Prepare(AiConversation conversation, AiTurn before, AiTurn after)
    {
        var changes = new List<AliasChange>();
        var currentRefs = References(after).ToHashSet();
        var retained = currentRefs.Select(r => AiEquationIndex.BindingKey(r.Key, r.Segment)).ToHashSet();
        if (before.EquationReferenceAliases is { } existing && existing.Keys.Any(key => !retained.Contains(key)))
        {
            var kept = existing.Where(pair => retained.Contains(pair.Key)).ToDictionary();
            after.EquationReferenceAliases = kept.Count == 0 ? null : kept;
        }
        var oldFormulas = Formulas(before);
        if (oldFormulas.Length == 0) return changes;
        var newFormulas = Formulas(after);
        if (oldFormulas.Select(f => (f.Target.SegmentId, f.Target.Equation.CopyTex, f.Row)).SequenceEqual(
            newFormulas.Select(f => (f.Target.SegmentId, f.Target.Equation.CopyTex, f.Row)))) return changes;

        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var reader = conversation.Turns[i];
            var own = reader.Id == before.Id;
            var owner = own ? after : reader;
            Dictionary<string, string>? changed = null;
            foreach (var reference in References(reader))
            {
                if (own && !currentRefs.Contains(reference)) continue;
                var qualified = AiEquationIndex.TrySplit(reference.Key, out _, out var local);
                if (qualified ? AiEquationIndex.TargetTurn(conversation.Turns, reader, reference.Key)?.Id != before.Id :
                    !own || reference.Segment.Length == 0) continue;
                var segment = qualified ? null : reference.Segment;
                var key = AiEquationIndex.EffectiveKey(reader, reference.Key, local, segment);
                var oldTarget = AiEquationIndex.Resolve(before, key, segment);
                if (oldTarget is null) continue; // Preserve old unresolved references without binding them by guesswork.
                var old = oldFormulas.FirstOrDefault(f => Matches(f, oldTarget));
                var next = old is null ? null : Counterpart(old, oldFormulas, newFormulas);
                var resolved = AiEquationIndex.Resolve(after, local, segment);
                var effective = local;
                if (next is null || !Matches(next, resolved))
                {
                    // A symbolic label is part of the public identity. Only legacy positional
                    // spellings can be rebound as numbering changes.
                    if (next is null || oldTarget.Equation.Label == local || key == local &&
                        local != oldTarget.Equation.Number && local != "eq-" + oldTarget.Equation.Number)
                        throw Broken(i, reference.Key);
                    effective = next.Target.Equation.Label ?? "eq-" + next.Target.Equation.Number;
                    if (!Matches(next, AiEquationIndex.Resolve(after, effective, segment))) throw Broken(i, reference.Key);
                }
                var binding = AiEquationIndex.BindingKey(reference.Key, segment);
                var bound = owner.EquationReferenceAliases?.GetValueOrDefault(binding) ?? local;
                if (effective == bound) continue;
                changed ??= owner.EquationReferenceAliases is { } aliases ? new(aliases) : [];
                if (effective == local) changed.Remove(binding);
                else changed[binding] = effective;
            }
            if (changed is null) continue;
            var result = changed.Count == 0 ? null : changed;
            if (own) after.EquationReferenceAliases = result;
            else changes.Add(new(reader, reader.EquationReferenceAliases, result));
        }
        return changes;
    }

    private static InvalidOperationException Broken(int readerIndex, string key) =>
        new($"第 {readerIndex + 1} 轮仍引用公式 \\eqref{{{key}}}。此次修改会删除目标、使标签有歧义或将引用转向另一公式。请保留被引公式和原标签；需要改引时，先向用户确认目标。");
}
