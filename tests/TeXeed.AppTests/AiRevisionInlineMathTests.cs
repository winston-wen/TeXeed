using System.IO;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckRevisionInlineMathAsync(bool realTex)
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        const string alpha = "A", sum = "a+b";
        if (!realTex)
        {
            var mathHome = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg")).FullName;
            using var capital = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.inline-axis.capital.svg")
                ?? throw new Exception("Missing reference formula fixture");
            using (var target = File.Create(Path.Combine(mathHome, ChatTexRenderer.CacheFileName(alpha, false)))) capital.CopyTo(target);
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            File.Copy(Path.Combine(root, "tests", "TeXeed.AppTests", "Fixtures", "branch-formulas", "inline-a-plus-b.svg"),
                Path.Combine(mathHome, ChatTexRenderer.CacheFileName(sum, false)), overwrite: true);
        }
        var before = new AiRevisionRow("row", "seg", @"看 \(x\)，由 \eqref{target} 得出。", 0, false, []);
        var valid = before with { Text = @"看 \(A\)，由 \eqref{target} 得出，另有 $a+b$。" };
        var protectedText = valid.Text.IndexOf(@"\eqref", StringComparison.Ordinal);
        var quoteAt = protectedText - 2;
        var output = await AiRevisionInlineMath.ValidateAsync([before], [valid],
            (_, _) => throw new Exception("Valid inline source should not call AI"), CancellationToken.None);
        Require(output.Single().Text == valid.Text, "Compilable edited formulas have to save unchanged");
        var draft = new AiRevisionDraft("turn", "rev", [before]);
        Require(AiRevisionText.Normalize(draft, [valid], RevisionMathScope.Inline).Single().Text == valid.Text,
            "The manual save gate would reject valid inline edits");
        var modifiedEqref = false;
        try { AiRevisionText.Normalize(draft, [valid with { Text = valid.Text.Replace("target", "other") }], RevisionMathScope.Inline); }
        catch (InvalidOperationException) { modifiedEqref = true; }
        Require(modifiedEqref, "The manual save gate should still protect equation references");
        var broken = realTex ? "x^2^3" : @"\frca{a}{b}";
        var body = valid.Text.Replace(@"A\)", broken + @"\)");
        var link = new AiRevisionLink("branch", quoteAt + broken.Length - 1, "追问");
        var invalid = valid with { Text = body, Links = [link] };
        var calls = 0;
        Task<IReadOnlyDictionary<string, string>> Repair(IReadOnlyList<AiRevisionFormulaIssue> issues, CancellationToken _)
        {
            calls++;
            Require(issues.Count == 1 && issues.Single().Tex == broken && !string.IsNullOrEmpty(issues.Single().Error),
                "AI should receive only the broken expression and its diagnostic");
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [issues.Single().Id] = alpha });
        }
        output = await AiRevisionInlineMath.ValidateAsync([before], [invalid], Repair, CancellationToken.None);
        Require(calls == 1 && output.Single().Text == valid.Text && output.Single().Links.Single().Offset == quoteAt,
            "Repair changed surrounding prose, delimiters or citation position");
        var rejected = false;
        try
        {
            await AiRevisionInlineMath.ValidateAsync([before], [invalid],
                (issues, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [issues[0].Id] = broken }),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("仍无法编译", StringComparison.Ordinal)) { rejected = true; }
        Require(rejected && invalid.Text == body && invalid.Links.Single().Offset == link.Offset,
            "A failed correction must leave the original local draft exactly as entered");
        var wrongBoundaries = false;
        try
        {
            await AiRevisionInlineMath.ValidateAsync([before], [invalid],
                (issues, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [issues[0].Id] = @"A\) added \(A" }),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("定界范围", StringComparison.Ordinal)) { wrongBoundaries = true; }
        Require(wrongBoundaries, "Correction could change content outside the failing formula");
        await CheckAiRevisionMathAsync(realTex);
        Console.WriteLine($"PASS: inline TeX ({(realTex ? "local LaTeX/dvisvgm" : "isolated cache and TeX input validation")}), repair, second check, preserved eqref and citation offsets.");
    }
}
