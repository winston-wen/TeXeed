using System.Text.RegularExpressions;

namespace TeXeed.Core;

public static class LatexFormatter
{
    public static string Format(string text)
    {
        if (text.Length == 0) return text;
        var scan = LatexSyntax.Analyze(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n');
        var codeLines = scan.Code.Split('\n');
        var indent = 0;
        var offset = 0;
        var output = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var code = codeLines[i].TrimEnd('\r');
            var trimmed = code.TrimStart();
            var literal = scan.LiteralRanges.Any(r => r.Start < offset + lines[i].Length && r.End > offset);
            var leadingCloses = 0;
            var cursor = 0;
            while (cursor < trimmed.Length)
            {
                if (trimmed[cursor] == '}') { leadingCloses++; cursor++; }
                else
                {
                    var end = Regex.Match(trimmed[cursor..], @"^\\end\s*\{[^}]+\}|^\\\]");
                    if (!end.Success) break;
                    leadingCloses++; cursor += end.Length;
                }
                while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor])) cursor++;
            }
            var lineIndent = Math.Max(0, indent - leadingCloses);
            // Never rewrite a line containing a verbatim span, including its surrounding whitespace.
            output[i] = literal ? line : string.IsNullOrWhiteSpace(line) ? "" : new string(' ', lineIndent * 2) + line.TrimStart();
            var commands = LatexSyntax.Commands(code).Where(c => c.Name is "begin" or "end" && c.Arguments.Count > 0).ToList();
            var delta = commands.Sum(c => c.Name == "begin" ? 1 : -1);
            var braceCode = code.ToCharArray();
            foreach (var command in commands)
                for (var k = command.Index; k < command.End; k++) braceCode[k] = ' ';
            for (var k = 0; k < braceCode.Length; k++)
            {
                if (LatexSyntax.IsEscaped(code, k)) continue;
                if (braceCode[k] == '{') delta++;
                else if (braceCode[k] == '}') delta--;
                else if (braceCode[k] == '\\' && k + 1 < braceCode.Length)
                {
                    if (braceCode[k + 1] == '[') delta++;
                    else if (braceCode[k + 1] == ']') delta--;
                }
            }
            indent = Math.Clamp(indent + delta, 0, 100);
            offset += lines[i].Length + 1;
        }
        return string.Join(newline, output);
    }
}
