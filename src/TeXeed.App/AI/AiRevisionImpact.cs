using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

internal sealed record AiRelatedRevision(string Target, int Turn, string[] Reasons);

internal static class AiRevisionImpact
{
    internal static IReadOnlyList<AiRelatedRevision> Scan(IReadOnlyList<AiTurn> turns, AiTurn before, AiTurn after, CancellationToken token)
    {
        var number = turns.ToList().FindIndex(t => t.Id == after.Id) + 1;
        if (number == 0) return [];
        var mention = new Regex($@"第\s*0*{number}\s*轮|(?:公式|式)\s*[（(]\s*0*{number}\s*[:：]");
        var removed = Regex.Split(before.Answer, @"[。！？；\r\n]+").Select(s => s.Trim())
            .Where(s => s.Length >= 12 && !after.Answer.Contains(s, StringComparison.Ordinal)).Distinct().ToArray();
        var related = new List<AiRelatedRevision>();
        for (var i = 0; i < turns.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var other = turns[i];
            if (other.Id == after.Id || other.IsIncomplete) continue;
            var reasons = new HashSet<string>();
            if (other.SentAnnotations.Any(e => e.AnswerAnchor?.TurnId == after.Id))
                reasons.Add("提问引用了改稿轮次");
            foreach (var segment in AiRevisionText.Segments(other))
            {
                if (mention.IsMatch(segment.Text)) reasons.Add($"正文提及第 {number} 轮");
                if (removed.Any(s => segment.Text.Contains(s, StringComparison.Ordinal))) reasons.Add("正文沿用了已修改的表述");
                if (!segment.Text.Contains(@"\eqref{") && !segment.Text.Contains(@"\ref{")) continue;
                foreach (var equation in AiEquationIndex.Read(segment.Text))
                {
                    if (equation.Reference is not { } reference ||
                        !AiEquationIndex.TrySplit(reference, out _, out var key) ||
                        AiEquationIndex.TargetTurn(turns, other, reference)?.Id != after.Id) continue;
                    var old = AiEquationIndex.Resolve(before, key);
                    var updated = AiEquationIndex.Resolve(after, key);
                    if (old?.SegmentId != updated?.SegmentId || old?.Equation.Source != updated?.Equation.Source ||
                        old?.Equation.Label != updated?.Equation.Label || old?.Equation.Number != updated?.Equation.Number)
                        reasons.Add($@"\eqref{{{reference}}} 的目标已变化");
                }
            }
            if (reasons.Count > 0) related.Add(new(other.Id, i + 1, [.. reasons]));
        }
        return related;
    }

    // The saved previous version lets a retried tool call reconstruct the scan
    // after an interruption between committing the answer and returning its result.
    internal static AiTurn AtVersion(AiTurn current, AiTurnRevision revision)
    {
        var segments = AiRevisionText.Segments(current).Select(s => s.Snapshot()).ToList();
        foreach (var segment in segments) if (revision.Sources.TryGetValue(segment.Id, out var text)) segment.Text = text;
        return current with { Answer = string.Join("\n\n", segments.Where(s => s.Text.Length > 0).Select(s => s.Text)), Segments = segments };
    }
}
