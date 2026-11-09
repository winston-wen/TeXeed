using System.Text;
using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

internal static class AiDocumentPostscript
{
    private const string StartMarker = "% texeed-postscript-begin";
    private const string EndMarker = "% texeed-postscript-end";
    private const string LatestEndMarker = "% texeed-postscript-latest-end";

    internal static bool TryUpdate(AiSharedDocument document, string message, string author, bool preserveExisting, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(message)) return true;
        var source = document.Source;
        var start = source.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = source.IndexOf(EndMarker, StringComparison.Ordinal);
        var opening = DocumentStart(source);
        if (opening < 0 && source.Contains("\\documentclass", StringComparison.Ordinal))
        {
            error = "无法定位文稿的 document 环境，AI 附言未应用。";
            return false;
        }
        if ((start < 0) != (end < 0) || (start >= 0 && (end < start || !AtLine(source, start, StartMarker.Length) || !AtLine(source, end, EndMarker.Length) || start < Math.Max(0, opening) || !string.IsNullOrWhiteSpace(source[Math.Max(0, opening)..start]) || source.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0 || source.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)))
        {
            error = "文稿附言标记不完整或重复，AI 内容未应用；请检查附言标记后重试。";
            return false;
        }
        int position, length;
        var protectedNote = false;
        if (start >= 0)
        {
            position = start;
            length = end + EndMarker.Length - start;
            var existingLatest = source.IndexOf(LatestEndMarker, start + StartMarker.Length, StringComparison.Ordinal);
            protectedNote = preserveExisting || Protected(document, position, length) || (existingLatest >= 0 && existingLatest < end);
            if (protectedNote)
            {
                position = start + StartMarker.Length;
                var latestEnd = source.IndexOf(LatestEndMarker, position, StringComparison.Ordinal);
                length = !preserveExisting && latestEnd >= 0 && latestEnd < end && AtLine(source, latestEnd, LatestEndMarker.Length) &&
                    !Protected(document, position, latestEnd + LatestEndMarker.Length - position)
                    ? latestEnd + LatestEndMarker.Length - position : 0;
            }
        }
        else
        {
            position = Math.Max(0, opening);
            length = 0;
        }
        var summary = "附言（" + EscapeText(author) + "）：" + EscapeText(message);
        var block = StartMarker + "\n" + summary + "\n" + EndMarker;
        var replacement = protectedNote ? "\n" + summary + "\n" + LatestEndMarker : start >= 0 ? block : "\n\n" + block + "\n\n";
        if (!document.TryApplyEdits(document.Revision, [new AiDocumentEdit(position, length, replacement)], author, "ai"))
        {
            error = "文稿已变化，AI 附言未应用。";
            return false;
        }
        return true;
    }

    private static bool Protected(AiSharedDocument document, int start, int length) =>
        document.AuthorSpans.Any(span => span.Role == "user" && span.Start < start + length && span.Start + span.Length > start) ||
        document.Comments.Any(comment => !comment.Orphaned && comment.Start < start + length && comment.Start + Math.Max(comment.Length, 1) > start);

    internal static bool SameBlock(string before, string after)
    {
        static string? Block(string source)
        {
            var start = source.IndexOf(StartMarker, StringComparison.Ordinal);
            var end = source.IndexOf(EndMarker, StringComparison.Ordinal);
            return start < 0 || end < start ? null : source[start..(end + EndMarker.Length)];
        }
        return Block(before) == Block(after);
    }

    private static bool AtLine(string source, int start, int length) =>
        (start == 0 || source[start - 1] == '\n') &&
        (start + length == source.Length || source[start + length] is '\r' or '\n');

    private static string EscapeText(string text)
    {
        var result = new StringBuilder();
        foreach (var c in Regex.Replace(text.Trim(), @"\s*\r?\n\s*", " "))
        {
            if (c is '$' or '%' or '_' or '{' or '}' or '#' or '&') result.Append('\\').Append(c);
            else if (c is '\\' or '^' or '~') result.Append("\\verb|").Append(c).Append('|');
            else result.Append(c);
        }
        return result.ToString();
    }

    private static int DocumentStart(string source)
    {
        const string token = "\\begin{document}";
        var depth = 0;
        for (var i = 0; i < source.Length;)
        {
            if (source[i] == '%') { var eol = source.IndexOf('\n', i); i = eol < 0 ? source.Length : eol + 1; continue; }
            if (source[i] == '$')
            {
                var delimiter = i + 1 < source.Length && source[i + 1] == '$' ? "$$" : "$";
                var close = i + delimiter.Length;
                while (close < source.Length && !source.AsSpan(close).StartsWith(delimiter, StringComparison.Ordinal))
                { if (source[close] == '\\') close++; close++; }
                if (close >= source.Length) return -1;
                i = close + delimiter.Length; continue;
            }
            if (source[i] == '{') { depth++; i++; continue; }
            if (source[i] == '}') { depth = Math.Max(0, depth - 1); i++; continue; }
            if (source[i] != '\\') { i++; continue; }
            if (depth == 0 && source.AsSpan(i).StartsWith(token, StringComparison.Ordinal)) return i + token.Length;
            if (source.AsSpan(i).StartsWith("\\(", StringComparison.Ordinal) || source.AsSpan(i).StartsWith("\\[", StringComparison.Ordinal))
            {
                var delimiter = source[i + 1] == '(' ? "\\)" : "\\]";
                var close = source.IndexOf(delimiter, i + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                i = close + delimiter.Length; continue;
            }
            if (depth == 0 && (source.AsSpan(i).StartsWith("\\begin{verbatim}", StringComparison.Ordinal) || source.AsSpan(i).StartsWith("\\begin{lstlisting}", StringComparison.Ordinal)))
            {
                var code = Regex.Match(source[i..], @"^\\begin\{(verbatim|lstlisting)\}(?:\[[^\]\n]*\])?");
                if (code.Success)
                {
                    var delimiter = "\\end{" + code.Groups[1].Value + "}";
                    var close = source.IndexOf(delimiter, i + code.Length, StringComparison.Ordinal);
                    if (close < 0) return -1;
                    i = close + delimiter.Length; continue;
                }
            }
            if (source.AsSpan(i).StartsWith("\\verb", StringComparison.Ordinal))
            {
                var delimiter = i + 5;
                if (delimiter < source.Length && source[delimiter] == '*') delimiter++;
                if (delimiter < source.Length && !char.IsLetterOrDigit(source[delimiter]) && !char.IsWhiteSpace(source[delimiter]))
                { var close = source.IndexOf(source[delimiter], delimiter + 1); if (close < 0) return -1; i = close + 1; continue; }
            }
            i++;
            if (i < source.Length && char.IsLetter(source[i])) while (i < source.Length && char.IsLetter(source[i])) i++;
            else if (i < source.Length) i++;
        }
        return -1;
    }
}
