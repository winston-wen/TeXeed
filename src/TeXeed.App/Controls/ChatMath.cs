using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TeXeed.App.Controls;

/// <summary>Protect TeX before Markdown consumes backslash escapes and underscores. Code remains literal.</summary>
internal sealed class ChatMath
{
    internal sealed record Token(string Source, bool Display, string? Label, string? Number, string? Reference = null, bool Parenthesize = false)
    {
        internal string CopyTex { get; init; } = Source;
    }
    internal Dictionary<string, Token> Tokens { get; } = [];
    internal Dictionary<string, string> Labels { get; } = [];
    internal string Prefix { get; } = "TEXEED" + Guid.NewGuid().ToString("N") + "M";
    internal bool HasUnclosedMath { get; private set; }
    private static readonly Regex Formula = new(@"(?<!\\)(?:\\\[(?<display>[\s\S]*?)\\\]|\\\((?<inline>[\s\S]*?)\\\)|\$\$(?<display>[\s\S]*?)\$\$|\$(?!\s)(?<inline>[^$\r\n]*?\S)\$|\\begin\{(?<env>equation\*?|align\*?)\}(?<environment>[\s\S]*?)\\end\{\k<env>\}|\\(?<ref>eqref|ref)\{(?<key>[^{}\r\n]+)\})", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex Metadata = new(@"\\(?<kind>label|tag\*?)\{(?<value>[^{}]+)\}", RegexOptions.Compiled);
    internal string Prepare(string markdown, MarkdownPipeline pipeline)
    {
        var parsed = Markdig.Markdown.Parse(markdown, pipeline);
        var protectedSpans = parsed.Descendants().Where(b => b is CodeBlock).Select(b => b.Span)
            .Concat(parsed.Descendants<LeafBlock>().Where(b => b.Inline is not null).SelectMany(b => b.Inline!.Descendants<CodeInline>()).Select(i => i.Span)).OrderBy(s => s.Start).ToArray();
        var output = new StringBuilder(); int copied = 0, spanIndex = 0, number = 0;
        var unresolved = markdown.ToCharArray();
        foreach (var span in protectedSpans)
            if (span.Start >= 0 && span.End >= span.Start && span.Start < unresolved.Length)
                Array.Fill(unresolved, ' ', span.Start, Math.Min(span.End + 1, unresolved.Length) - span.Start);
        try
        {
            foreach (Match match in Formula.Matches(markdown))
            {
                while (spanIndex < protectedSpans.Length && protectedSpans[spanIndex].End < match.Index) spanIndex++;
                if (spanIndex < protectedSpans.Length && protectedSpans[spanIndex].Start < match.Index + match.Length) continue;
                Array.Fill(unresolved, ' ', match.Index, match.Length);
                Token token;
                if (match.Groups["ref"].Success) token = new("", false, null, null, match.Groups["key"].Value, match.Groups["ref"].Value == "eqref");
                else
                {
                    var display = match.Groups["display"].Success || match.Groups["environment"].Success;
                    var source = match.Groups["environment"].Success ? match.Groups["environment"].Value : match.Groups[display ? "display" : "inline"].Value;
                    var copyTex = source.Trim();
                    string? label = null, tag = null;
                    source = Metadata.Replace(source, m => { if (m.Groups["kind"].Value == "label") label = m.Groups["value"].Value; else tag = m.Groups["value"].Value; return ""; }).Trim();
                    if (match.Groups["env"].Value.StartsWith("align"))
                    {
                        source = @"\begin{aligned}" + source + @"\end{aligned}";
                        copyTex = @"\begin{aligned}" + copyTex + @"\end{aligned}";
                    }
                    if (display) { number++; tag ??= number.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                    if (label is not null && tag is not null) Labels.TryAdd(label, tag);
                    token = new(source, display, label, tag) { CopyTex = copyTex };
                }
                var key = Prefix + Tokens.Count + "END";
                Tokens.Add(key, token);
                output.Append(markdown, copied, match.Index - copied);
                if (token.Display) output.Append("\n\n");
                output.Append(key);
                if (token.Display) output.Append("\n\n");
                copied = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException) { /* Preserve the remainder as selectable source. */ }
        HasUnclosedMath = Regex.IsMatch(new string(unresolved), @"(?<!\\)(?:\\[\[(]|\$\$|\$(?=\S)(?!\d)|\\begin\{(?:equation|align))", RegexOptions.None, TimeSpan.FromMilliseconds(500));
        return output.Append(markdown, copied, markdown.Length - copied).ToString();
    }
}
