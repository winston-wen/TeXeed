using System.Text.RegularExpressions;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

internal sealed record AiRelatedRevision(string Target, int Turn, string[] Reasons);

internal static class AiRevisionImpact
{
    private static readonly string[] GreekNames = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega".Split();
    private const string GreekLetters = "αβγδεζηθικλμνξοπρστυφχψω";
    private static readonly Regex Symbol = new(@"(?<base>\\[a-zA-Z]+|[a-zA-Zα-ωΑ-Ω])(?<script>_(?:\{[a-zA-Z0-9]+\}|[a-zA-Z0-9])|[₀-₉]+)?(?<prime>['′]+)?");
    private static readonly Regex TexWords = new(@"\\(?:begin|end|text|operatorname|mathrm)\s*\{[^{}]*\}");

    // The same notation in another answer is a reason for the model to read it.
    // Only the model, with that context, can decide whether it denotes the same quantity.
    private static void Collect(string source, bool prose, Dictionary<string, int> counts)
    {
        if (prose && !source.Any(c => c is >= 'Α' and <= 'ω')) return;
        foreach (Match match in Symbol.Matches(source))
        {
            var symbol = match.Groups["base"].Value;
            if (prose && (symbol[0] < 'Α' || symbol[0] > 'ω')) continue;
            if (symbol.StartsWith('\\'))
            {
                var name = symbol[1..];
                var i = Array.IndexOf(GreekNames, name.ToLowerInvariant());
                if (i < 0)
                {
                    if (!name.StartsWith("var") || Array.IndexOf(GreekNames, name[3..]) < 0) continue;
                }
                else symbol = (char.IsUpper(name[0]) ? char.ToUpperInvariant(GreekLetters[i]) : GreekLetters[i]).ToString();
            }
            var sub = match.Groups["script"].Value.TrimStart('_').Trim('{', '}');
            if (sub.Length > 0) symbol += "_" + string.Concat(sub.Select(c => c is >= '₀' and <= '₉' ? (char)('0' + c - '₀') : c));
            symbol += match.Groups["prime"].Value.Replace('′', '\'');
            counts[symbol] = counts.GetValueOrDefault(symbol) + 1;
        }
    }

    private static Dictionary<string, int> Symbols(string text, Dictionary<string, int>? expressions = null)
    {
        var result = new Dictionary<string, int>();
        if (text.IndexOfAny(['$', '\\']) < 0) { Collect(text, true, result); return result; }
        var start = 0;
        foreach (var math in MarkdownView.RevisionMath(text, 0).Tokens.Values)
        {
            if (math.OriginalOffset > start) Collect(text[start..math.OriginalOffset], true, result);
            if (math.Reference is null)
            {
                Collect(TexWords.Replace(math.Source, " "), false, result);
                if (expressions is not null) expressions[math.Source] = expressions.GetValueOrDefault(math.Source) + 1;
            }
            start = math.OriginalOffset + math.OriginalLength;
        }
        if (start < text.Length) Collect(text[start..], true, result);
        return result;
    }

    internal static string[] Branches(IEnumerable<AiConversation> conversations, AiConversation active, string revisedTurnId) =>
        conversations.Where(c => c.Id != active.Id && (c.ParentConversationId == active.Id &&
            c.BranchAnchor?.TurnId == revisedTurnId || c.InheritedTurns.Any(t => t.Id == revisedTurnId))).Select(c => c.Title).ToArray();

    private static HashSet<string> ChangedSymbols(string before, string after)
    {
        if (before == after) return [];
        var oldMath = new Dictionary<string, int>();
        var newMath = new Dictionary<string, int>();
        var previous = Symbols(before, oldMath);
        var latest = Symbols(after, newMath);
        var changed = previous.Keys.Union(latest.Keys).Where(s => previous.GetValueOrDefault(s) != latest.GetValueOrDefault(s)).ToHashSet();
        // Cancel equal formulas (including moved and repeated formulas). A changed
        // definition such as d=x-y → d=y-x can use exactly the same symbols.
        var touched = new Dictionary<string, int>();
        foreach (var expression in oldMath.Keys.Union(newMath.Keys))
            if (oldMath.GetValueOrDefault(expression) != newMath.GetValueOrDefault(expression))
                Collect(TexWords.Replace(expression, " "), false, touched);
        changed.UnionWith(touched.Keys);
        return changed;
    }

    internal static IReadOnlyList<AiRelatedRevision> Scan(IReadOnlyList<AiTurn> turns, AiTurn before, AiTurn after, CancellationToken token)
    {
        var number = turns.ToList().FindIndex(t => t.Id == after.Id) + 1;
        if (number == 0) return [];
        var mention = new Regex($@"第\s*0*{number}\s*轮|(?:公式|式)\s*[（(]\s*0*{number}\s*[:：]");
        var removed = Regex.Split(before.Answer, @"[。！？；\r\n]+").Select(s => s.Trim())
            .Where(s => s.Length >= 12 && !after.Answer.Contains(s, StringComparison.Ordinal)).Distinct().ToArray();
        var changedSymbols = ChangedSymbols(before.Answer, after.Answer);
        var related = new List<AiRelatedRevision>();
        for (var i = 0; i < turns.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var other = turns[i];
            if (other.Id == after.Id || !other.CanReviseAnswer) continue;
            var reasons = new HashSet<string>();
            if (other.SentAnnotations.Any(e => e.AnswerAnchor?.TurnId == after.Id))
                reasons.Add("提问引用了改稿轮次");
            foreach (var segment in AiRevisionText.Segments(other))
            {
                if (mention.IsMatch(segment.Text)) reasons.Add($"正文提及第 {number} 轮");
                if (removed.Any(s => segment.Text.Contains(s, StringComparison.Ordinal))) reasons.Add("正文沿用了已修改的表述");
                if (changedSymbols.Count > 0)
                {
                    var symbols = Symbols(segment.Text);
                    var shared = changedSymbols.Where(symbols.ContainsKey).ToArray();
                    if (shared.Length > 0) reasons.Add("正文也使用了改动涉及的符号 " + string.Join("、", shared) + "，请核对定义与结论");
                }
                if (!segment.Text.Contains(@"\eqref{") && !segment.Text.Contains(@"\ref{")) continue;
                foreach (var equation in AiEquationIndex.Read(segment.Text))
                {
                    if (equation.Reference is not { } reference ||
                        !AiEquationIndex.TrySplit(reference, out _, out var key) ||
                        AiEquationIndex.TargetTurn(turns, other, reference)?.Id != after.Id) continue;
                    var old = AiEquationIndex.Resolve(before, key);
                    var updated = AiEquationIndex.Resolve(after, AiEquationIndex.EffectiveKey(other, reference, key));
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
