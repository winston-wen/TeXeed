using Markdig;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

internal sealed record AiRevisionFormulaIssue(string Id, string Tex, string Error);

internal static class AiRevisionInlineMath
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private sealed record Formula(string RowId, int Start, int Length, string Tex, string Open, string Close);

    internal static async Task<IReadOnlyList<AiRevisionRow>> ValidateAsync(
        IReadOnlyList<AiRevisionRow> before, IReadOnlyList<AiRevisionRow> after,
        Func<IReadOnlyList<AiRevisionFormulaIssue>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> repair,
        CancellationToken token)
    {
        var textBefore = before.Where(row => !row.Locked).ToDictionary(row => row.Id, row => row.Text);
        var changed = after.Where(row => !row.Locked && textBefore.GetValueOrDefault(row.Id) != row.Text).ToArray();
        if (changed.Length == 0) return after;
        var formulas = new List<Formula>();
        foreach (var row in changed)
        {
            var parsed = Scan(row, out var unclosed);
            if (unclosed) throw new InvalidOperationException("行内公式的定界符没有配对，请在整理稿中补全后再保存。");
            formulas.AddRange(parsed);
        }
        if (formulas.Count == 0) return after;
        var formerFormulas = before.Where(row => !row.Locked).SelectMany(row => Scan(row, out _))
            .GroupBy(formula => formula.Tex, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bool Reuse(Formula formula)
        {
            if (!formerFormulas.TryGetValue(formula.Tex, out var count) || count == 0) return false;
            formerFormulas[formula.Tex] = count - 1;
            return true;
        }
        foreach (var row in after.Where(row => !row.Locked && textBefore.GetValueOrDefault(row.Id) == row.Text))
            foreach (var formula in Scan(row, out _)) Reuse(formula);
        formulas.RemoveAll(Reuse);
        var expressions = formulas.Select(formula => formula.Tex).Distinct(StringComparer.Ordinal).ToArray();
        var failures = (await Task.WhenAll(expressions.Select((tex, index) => CompileAsync(tex, index, token))))
            .OfType<AiRevisionFormulaIssue>().ToArray();
        if (failures.Length == 0) return after;
        var corrected = await repair(failures, token);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var issue in failures)
        {
            if (!corrected.TryGetValue(issue.Id, out var tex) || string.IsNullOrWhiteSpace(tex))
                throw new InvalidOperationException("AI 未返回完整的行内公式修正，整理稿已保留。");
            replacements.Add(issue.Tex, tex.Trim());
        }
        var result = after.ToList();
        var byRow = formulas.Where(formula => replacements.ContainsKey(formula.Tex)).ToLookup(formula => formula.RowId);
        for (var i = 0; i < result.Count; i++)
        {
            var row = result[i];
            if (!byRow.Contains(row.Id)) continue;
            var starts = byRow[row.Id].Select(formula => formula.Start).ToHashSet();
            var expected = Scan(row, out _).Select(formula => starts.Contains(formula.Start) ? replacements[formula.Tex] : formula.Tex).ToArray();
            foreach (var formula in byRow[row.Id].OrderByDescending(formula => formula.Start))
            {
                var replacement = formula.Open + replacements[formula.Tex] + formula.Close;
                var end = formula.Start + formula.Length;
                var difference = replacement.Length - formula.Length;
                row = row with
                {
                    Text = row.Text[..formula.Start] + replacement + row.Text[end..],
                    Links = row.Links.Select(link => link with
                    {
                        Offset = link.Offset <= formula.Start ? link.Offset : link.Offset >= end ? link.Offset + difference :
                            Math.Min(link.Offset, formula.Start + replacement.Length)
                    }).ToList()
                };
            }
            var actual = Scan(row, out var unclosed);
            if (unclosed || !actual.Select(f => f.Tex).SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidOperationException("AI 返回的修正改变了行内公式的定界范围，整理稿已保留。");
            result[i] = row;
        }
        var remaining = await Task.WhenAll(replacements.Values.Distinct(StringComparer.Ordinal).Select((tex, index) => CompileAsync(tex, index, token)));
        if (remaining.OfType<AiRevisionFormulaIssue>().FirstOrDefault() is { } failed)
            throw new InvalidOperationException("AI 修正后的行内公式仍无法编译，整理稿已保留。" + Environment.NewLine + failed.Error);
        return result;
    }

    private static List<Formula> Scan(AiRevisionRow row, out bool unclosed)
    {
        unclosed = false;
        if (row.Text.IndexOfAny(['$', '\\']) < 0) return [];
        var math = new ChatMath();
        math.Prepare(row.Text, Pipeline);
        unclosed = math.HasUnclosedMath;
        return math.Tokens.Values.Where(item => !item.Display && item.Reference is null).OrderBy(item => item.OriginalOffset)
            .Select(item => row.Text.AsSpan(item.OriginalOffset).StartsWith(@"\(", StringComparison.Ordinal)
                ? new Formula(row.Id, item.OriginalOffset, item.OriginalLength, item.Source, @"\(", @"\)")
                : new Formula(row.Id, item.OriginalOffset, item.OriginalLength, item.Source, "$", "$")).ToList();
    }

    private static async Task<AiRevisionFormulaIssue?> CompileAsync(string tex, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await ChatTexRenderer.RenderAsync(tex, false).WaitAsync(token);
            return null;
        }
        catch (ChatTexRenderer.TexSyntaxException error) { return new((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), tex, error.Message); }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new InvalidOperationException("无法校验行内公式，整理稿已保留。" + Environment.NewLine + error.Message, error);
        }
    }
}
