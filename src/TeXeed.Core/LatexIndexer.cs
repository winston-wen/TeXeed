using System.Text.RegularExpressions;

namespace TeXeed.Core;

public static partial class LatexIndexer
{
    private static readonly Dictionary<string, int> SectionLevels = new(StringComparer.Ordinal)
    {
        ["part"] = 0, ["chapter"] = 1, ["section"] = 2, ["subsection"] = 3,
        ["subsubsection"] = 4, ["paragraph"] = 5, ["subparagraph"] = 6
    };
    private static readonly HashSet<string> IndexCommands = new(SectionLevels.Keys.Concat(new[]
        { "label", "bibitem", "todo", "pdfbookmark", "currentpdfbookmark", "subpdfbookmark", "belowpdfbookmark", "bookmark",
          "input", "include", "subfile", "import", "subimport", "inputfrom", "subinputfrom", "includefrom", "subincludefrom" }), StringComparer.Ordinal);

    public static ProjectIndex Build(IEnumerable<ProjectFile> files)
    {
        var outline = new List<IndexEntry>();
        var labels = new List<IndexEntry>();
        var citations = new List<IndexEntry>();
        var todos = new List<IndexEntry>();
        var editable = files.Where(f => f.IsEditable).ToArray();
        var bookmarkEvents = new List<BookmarkEvent>();
        foreach (var file in editable)
        {
            var scan = LatexSyntax.Analyze(file.Content);
            var lines = new LatexSyntax.LineCursor(file.Content);
            var heading = "";
            foreach (var command in LatexSyntax.Commands(scan.Code, IndexCommands))
            {
                if (command.Arguments.Count == 0) continue;
                var value = command.Arguments[0].Trim();
                var line = lines.LineAt(command.Index);
                if (SectionLevels.TryGetValue(command.Name, out var level))
                {
                    heading = LatexTitle.PlainText(value);
                    outline.Add(new(heading, file.Path, line, command.Name, level) { Latex = value });
                }
                else if (command.Name == "label" && value.Length > 0)
                    labels.Add(new(value, file.Path, line, heading.Length > 0 ? $"{heading} · {file.RelativePath}" : file.RelativePath));
                else if (command.Name == "bibitem" && value.Length > 0)
                    citations.Add(new(value, file.Path, line, "bibitem"));
                else if (command.Name == "todo")
                    todos.Add(new(Readable(value), file.Path, line, "TODO"));
                else if (command.Name is "pdfbookmark" or "currentpdfbookmark" or "subpdfbookmark" or "belowpdfbookmark" or "bookmark")
                    bookmarkEvents.Add(new(file.Path, Bookmark(command, file, line, value), null));
                else if (command.Name is "input" or "include" or "subfile") bookmarkEvents.Add(new(file.Path, null, value));
                else if (command.Name is "import" or "subimport" or "inputfrom" or "subinputfrom" or "includefrom" or "subincludefrom" && command.Arguments.Count > 1)
                    bookmarkEvents.Add(new(file.Path, null, value.TrimEnd('/') + "/" + command.Arguments[1]));
            }
            var commentLines = new LatexSyntax.LineCursor(file.Content);
            foreach (var comment in scan.Comments)
            {
                var marker = Regex.Match(comment.Text, @"\b(TODO|FIXME)\b\s*:?\s*(.*)", RegexOptions.IgnoreCase);
                if (marker.Success)
                    todos.Add(new(marker.Groups[2].Value.Trim() is { Length: > 0 } text ? text : marker.Groups[1].Value,
                        file.Path, commentLines.LineAt(comment.Index), marker.Groups[1].Value.ToUpperInvariant()));
            }
            if (file.Kind == ProjectFileKind.Bibliography)
                IndexBibliography(file, scan.Code, citations);
        }
        return new ProjectIndex(outline, labels, citations, todos)
            { Bookmarks = OrderBookmarks(editable, bookmarkEvents), BookmarkEvents = bookmarkEvents };
    }

    private static IndexEntry Bookmark(LatexCommand command, ProjectFile file, int line, string title)
    {
        var level = 0; var relative = false; var offset = 0; var keep = false;
        var destination = command.Arguments.Count > 1 ? command.Arguments[1].Trim() : "";
        if (command.Name == "pdfbookmark") level = BookmarkLevel(command.OptionalArguments.FirstOrDefault() ?? "0");
        else if (command.Name == "bookmark")
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var block in command.OptionalArguments)
            {
                var start = 0; var depth = 0;
                for (var i = 0; i <= block.Length; i++)
                {
                    if (i < block.Length && !LatexSyntax.IsEscaped(block, i))
                    { if (block[i] == '{') depth++; else if (block[i] == '}') depth--; }
                    if (i != block.Length && (block[i] != ',' || depth != 0)) continue;
                    var option = block[start..i].Trim(); var equals = option.IndexOf('=');
                    if (option.Length != 0) options[equals < 0 ? option : option[..equals].Trim()] = equals < 0 ? "true" : option[(equals + 1)..].Trim().Trim('{', '}');
                    start = i + 1;
                }
            }
            destination = options.GetValueOrDefault("dest", "");
            relative = !options.ContainsKey("level");
            if (options.TryGetValue("level", out var absolute)) level = BookmarkLevel(absolute);
            if (options.TryGetValue("rellevel", out var delta)) { relative = true; offset = BookmarkLevel(delta); }
            keep = options.GetValueOrDefault("keeplevel") == "true";
        }
        else
        {
            relative = true; offset = command.Name == "currentpdfbookmark" ? 0 : 1;
            keep = command.Name == "belowpdfbookmark";
        }
        return new IndexEntry(LatexTitle.PlainText(title), file.Path, line, destination, level)
            { Latex = title, BookmarkRelativeLevel = relative, BookmarkLevelOffset = offset, BookmarkKeepLevel = keep };
    }
    private static int BookmarkLevel(string value) => int.TryParse(value.Trim(), out var level) ? Math.Clamp(level, -32, 32) : value.Trim() switch
    { "part" => -1, "chapter" => 0, "section" => 1, "subsection" => 2, "subsubsection" => 3, "paragraph" => 4, "subparagraph" => 5, _ => 0 };
    internal static IReadOnlyList<IndexEntry> ResolveBookmarkLevels(IEnumerable<IndexEntry> entries)
    {
        var current = 0; var resolved = new List<IndexEntry>();
        foreach (var entry in entries)
        {
            var level = Math.Clamp(entry.BookmarkRelativeLevel ? current + entry.BookmarkLevelOffset : entry.Level, -32, 32);
            resolved.Add(level == entry.Level ? entry : entry with { Level = level });
            if (!entry.BookmarkKeepLevel) current = level;
        }
        return resolved;
    }

    public static IReadOnlyList<IndexEntry> Search(IEnumerable<ProjectFile> files, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<IndexEntry>();
        foreach (var file in files.Where(f => f.IsEditable))
        {
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(new(lines[i].Trim(), file.Path, i + 1, file.RelativePath));
        }
        return results;
    }

    private static void IndexBibliography(ProjectFile file, string code, List<IndexEntry> citations)
    {
        var lines = new LatexSyntax.LineCursor(file.Content);
        // Consume whole entries so an @ sign inside a title cannot create a spurious key.
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] != '@') continue;
            var start = i++;
            var typeStart = i;
            while (i < code.Length && char.IsLetter(code[i])) i++;
            var type = code[typeStart..i];
            LatexSyntax.SkipSpace(code, ref i);
            if (i >= code.Length || code[i] is not ('{' or '(')) continue;
            var open = code[i];
            var bodyStart = ++i;
            var braceDepth = open == '{' ? 1 : 0;
            var outerBraceDepth = braceDepth;
            var quoted = false;
            var closed = false;
            for (; i < code.Length; i++)
            {
                if (LatexSyntax.IsEscaped(code, i)) continue;
                if (code[i] == '"' && braceDepth == outerBraceDepth) quoted = !quoted;
                else if (code[i] == '{') braceDepth++;
                else if (code[i] == '}')
                {
                    braceDepth--;
                    if (open == '{' && braceDepth == 0) { closed = true; i++; break; }
                }
                else if (code[i] == ')' && open == '(' && braceDepth == 0 && !quoted) { closed = true; i++; break; }
            }
            var bodyEnd = i - (closed ? 1 : 0);
            i--;
            if (type.Equals("comment", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("preamble", StringComparison.OrdinalIgnoreCase)) continue;
            var body = code[bodyStart..bodyEnd];
            var comma = body.IndexOf(',');
            if (comma < 0) continue;
            var key = body[..comma].Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace) || key.IndexOfAny(['{', '}', '=']) >= 0) continue;
            var title = Regex.Match(body[(comma + 1)..], "\\btitle\\s*=\\s*[\\{\\\"]([^\\}\"\\r\\n]+)", RegexOptions.IgnoreCase);
            citations.Add(new(key, file.Path, lines.LineAt(start), title.Success ? Readable(title.Groups[1].Value) : type));
        }
    }

    private static string Readable(string value) => Regex.Replace(
        Regex.Replace(value, @"\\(?:[A-Za-z@]+\*?\s*|([%&_#$]))", "$1").Replace("{", "").Replace("}", ""), @"\s+", " ").Trim();
}

internal sealed record LatexCommand(string Name, int Index, int End, IReadOnlyList<string> Arguments)
{
    internal IReadOnlyList<string> OptionalArguments { get; init; } = [];
}
internal sealed record LatexComment(int Index, string Text);
internal sealed record LatexRange(int Start, int End);
internal sealed record LatexScan(string Code, IReadOnlyList<LatexComment> Comments, IReadOnlyList<LatexRange> LiteralRanges);

// Positions are retained when comments and literal text are hidden: every index still maps to the editor.
internal static class LatexSyntax
{
    private static readonly HashSet<string> LiteralEnvironments = new(StringComparer.Ordinal)
        { "verbatim", "verbatim*", "Verbatim", "BVerbatim", "LVerbatim", "lstlisting", "minted", "comment", "filecontents", "filecontents*" };
    private static readonly Regex CommandPattern = new(@"\\([A-Za-z@]+)\*?", RegexOptions.Compiled);

    internal static LatexScan Analyze(string source)
    {
        var chars = source.ToCharArray();
        var comments = new List<LatexComment>();
        var literals = new List<LatexRange>();
        void Mask(int start, int end)
        {
            for (var k = start; k < end; k++) if (chars[k] is not ('\n' or '\r')) chars[k] = ' ';
        }
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] is not ('%' or '\\')) continue;
            if (IsEscaped(source, i)) continue;
            if (source[i] == '%')
            {
                var end = source.IndexOf('\n', i);
                if (end < 0) end = source.Length;
                comments.Add(new(i, source[(i + 1)..end].TrimEnd('\r')));
                Mask(i, end); i = end - 1;
            }
            else
            {
                var after = i + 1;
                while (after < source.Length && (char.IsAsciiLetter(source[after]) || source[after] == '@')) after++;
                if (after == i + 1) continue;
                var name = source.AsSpan(i + 1, after - i - 1);
                if (after < source.Length && source[after] == '*') after++;
                if (name.SequenceEqual("verb") && after < source.Length && !char.IsWhiteSpace(source[after]))
                {
                    var delimiter = source[after];
                    var end = after + 1;
                    while (end < source.Length && source[end] != delimiter && source[end] is not ('\r' or '\n')) end++;
                    if (end < source.Length && source[end] == delimiter) end++;
                    Mask(i, end); literals.Add(new(i, end)); i = end - 1;
                }
                else if (name.SequenceEqual("begin"))
                {
                    var arg = ReadGroup(source, ref after, '{', '}');
                    if (arg is null || !LiteralEnvironments.Contains(arg)) continue;
                    var endMatch = Regex.Match(source[after..], @"\\end\s*\{" + Regex.Escape(arg) + @"\}");
                    var end = endMatch.Success ? after + endMatch.Index + endMatch.Length : source.Length;
                    Mask(i, end); literals.Add(new(i, end)); i = end - 1;
                }
            }
        }
        return new(new string(chars), comments, literals);
    }

    internal static IEnumerable<LatexCommand> Commands(string code, HashSet<string>? names = null)
    {
        foreach (Match match in CommandPattern.Matches(code))
        {
            if (IsEscaped(code, match.Index)) continue;
            var pos = match.Index + match.Length;
            if (names is not null && !names.GetAlternateLookup<ReadOnlySpan<char>>().Contains(match.Groups[1].ValueSpan)) continue;
            var name = match.Groups[1].Value;
            var args = new List<string>();
            List<string>? optional = null;
            SkipSpace(code, ref pos);
            while (pos < code.Length && code[pos] == '[')
            {
                var option = ReadGroup(code, ref pos, '[', ']');
                if (option is null) break;
                (optional ??= []).Add(option);
                SkipSpace(code, ref pos);
            }
            var argumentCount = name is "import" or "subimport" or "inputfrom" or "subinputfrom" or "includefrom" or "subincludefrom"
                or "pdfbookmark" or "currentpdfbookmark" or "subpdfbookmark" or "belowpdfbookmark" ? 2 : 1;
            while (args.Count < argumentCount)
            {
                var arg = ReadGroup(code, ref pos, '{', '}');
                if (arg is null) break;
                args.Add(arg);
            }
            if (args.Count == 0 && name is "input" or "include")
            {
                SkipSpace(code, ref pos);
                var start = pos;
                while (pos < code.Length && !char.IsWhiteSpace(code[pos]) && code[pos] is not ('{' or '}' or '\\')) pos++;
                if (pos > start) args.Add(code[start..pos]);
            }
            yield return new LatexCommand(name, match.Index, pos, args) { OptionalArguments = optional ?? (IReadOnlyList<string>)Array.Empty<string>() };
        }
    }

    internal static string? ReadGroup(string code, ref int pos, char open, char close)
    {
        SkipSpace(code, ref pos);
        if (pos >= code.Length || code[pos] != open) return null;
        var start = ++pos;
        var depth = 1;
        var braceDepth = 0;
        for (; pos < code.Length; pos++)
        {
            if (IsEscaped(code, pos)) continue;
            if (open == '[')
            {
                if (code[pos] == '{') braceDepth++;
                else if (code[pos] == '}') braceDepth = Math.Max(0, braceDepth - 1);
                if (braceDepth > 0) continue;
            }
            if (code[pos] == open) depth++;
            else if (code[pos] == close && --depth == 0)
            {
                var result = code[start..pos]; pos++; return result;
            }
        }
        return null;
    }

    internal static bool IsEscaped(string source, int index)
    {
        var count = 0;
        while (--index >= 0 && source[index] == '\\') count++;
        return count % 2 != 0;
    }
    internal static void SkipSpace(string source, ref int index) { while (index < source.Length && char.IsWhiteSpace(source[index])) index++; }
    // Map commands and comments to editor lines in source order.
    internal sealed class LineCursor(string source)
    {
        private int _position, _line = 1;
        internal int LineAt(int index)
        {
            _line += source.AsSpan(_position, index - _position).Count('\n');
            _position = index;
            return _line;
        }
    }
}
