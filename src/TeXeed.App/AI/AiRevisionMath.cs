using TeXeed.App.Controls;
using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

internal static class AiRevisionMath
{
    private static readonly Regex ReferenceCommand = new(@"(?<!\\)\\(?:eqref|ref)\s*\{", RegexOptions.CultureInvariant);

    private static int MalformedReferences(string? source)
    {
        if (source is null || !source.Contains(@"\eqref", StringComparison.Ordinal) && !source.Contains(@"\ref", StringComparison.Ordinal)) return 0;
        var protectedSpans = AiRevisionText.ProtectedSpans(source);
        return ReferenceCommand.Matches(source).Count(match => !protectedSpans.Any(span => span.Start <= match.Index && match.Index < span.Start + span.Length));
    }

    internal static async Task CheckAsync(IReadOnlyList<AiRevisionRow> before, IReadOnlyList<AiRevisionRow> after, CancellationToken token)
    {
        var oldText = before.ToDictionary(row => row.Id, row => row.Text);
        var changed = after.Where(row => row.Text != oldText.GetValueOrDefault(row.Id)).ToList();
        var formulas = new List<ChatMath.Token>();
        foreach (var row in changed)
        {
            if (row.Text.IndexOfAny(['$', '\\']) < 0) continue;
            var math = MarkdownView.RevisionMath(row.Text, 0);
            if (math.HasUnclosedMath) throw new InvalidOperationException("改稿中存在未闭合的数学定界符，请修正后重新提交。");
            var malformed = MalformedReferences(row.Text);
            if (malformed > 0 && malformed > MalformedReferences(oldText.GetValueOrDefault(row.Id)))
                throw new InvalidOperationException(@"公式引用缺少有效标签或完整括号，请写成 \eqref{标签}。");
            formulas.AddRange(math.Tokens.Values.Where(item => item.Reference is null));
        }
        if (formulas.Count == 0) return;
        var known = before.Where(row => row.Text.IndexOfAny(['$', '\\']) >= 0)
            .SelectMany(row => MarkdownView.RevisionMath(row.Text, 0).Tokens.Values)
            .Where(item => item.Reference is null).Select(item => (item.Display, item.Source)).ToHashSet();
        var added = formulas.Where(item => !known.Contains((item.Display, item.Source))).ToArray();
        if (added.Any(formula => ReferenceCommand.IsMatch(formula.Source)))
            throw new InvalidOperationException(@"请将 \eqref{标签} 或 \ref{标签} 写在数学定界符外，以便预览和跳转。");
        var check = await AiMathProofreading.CheckFormulasAsync(added, false, token);
        if (check.Error is not null) throw new InvalidOperationException("改稿中的公式未通过排版校验，请修正后重新提交。\n" + check.Error);
    }

    internal static void CheckReferences(AiConversation conversation, AiTurn before, AiTurn after)
    {
        Dictionary<(string Segment, string Key), int> Unresolved(AiTurn source)
        {
            var missing = new Dictionary<(string, string), int>();
            foreach (var segment in AiRevisionText.Segments(source))
            {
                var items = AiEquationIndex.Read(segment.Text);
                foreach (var references in items.Where(token => token.Reference is not null).GroupBy(token => token.Reference!))
                {
                    var key = references.Key;
                    if (AiEquationIndex.TrySplit(key, out _, out var localKey))
                    {
                        var target = AiEquationIndex.TargetTurn(conversation.Turns, source, key);
                        if (target is not null && AiEquationIndex.Resolve(target.Id == source.Id ? source : target, localKey) is not null) continue;
                    }
                    else if (items.Count(item => item.Display && (item.Label == key || "eq-" + item.Number == key)) == 1) continue;
                    missing[(segment.Id, key)] = references.Count();
                }
            }
            return missing;
        }
        var unresolved = Unresolved(after);
        if (unresolved.Count == 0) return;
        var previous = Unresolved(before);
        var broken = unresolved.Where(item => item.Value > previous.GetValueOrDefault(item.Key)).Select(item => item.Key.Key).Distinct().ToArray();
        if (broken.Length > 0)
            throw new InvalidOperationException("改稿中的公式引用找不到唯一目标，请核对公式标签和轮次后重新提交：" + string.Join("、", broken));
    }
}
