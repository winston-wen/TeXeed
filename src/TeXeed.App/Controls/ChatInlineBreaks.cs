using System.Text;

namespace TeXeed.App.Controls;

/// <summary>Positions where inline TeX may continue on the next prose line.</summary>
internal static class ChatInlineBreaks
{
    internal const double MinimumWidthBp = 60 * 72d / 72.27;
    private static readonly HashSet<string> Operators = new(("""
        le leq leqslant ge geq geqslant ne neq equiv approx sim simeq cong propto asymp doteq ll gg prec succ
        preceq succeq subset supset subseteq supseteq subsetneq supsetneq in notin ni owns parallel nparallel
        mid nmid perp models vdash dashv Vdash vDash Vvdash pm mp times div cdot ast star circ bullet odot oplus
        ominus otimes oslash cap cup uplus sqcap sqcup vee wedge land lor setminus smallsetminus wr diamond
        triangleleft triangleright bigtriangleup bigtriangledown to gets mapsto longmapsto hookrightarrow
        hookleftarrow rightarrow leftarrow leftrightarrow Rightarrow Leftarrow Leftrightarrow longrightarrow
        longleftarrow longleftrightarrow Longrightarrow Longleftarrow Longleftrightarrow implies impliedby iff bmod
        """).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    internal static int[] Positions(string source)
    {
        // At most five ordinary characters and operators fit well inside the six-em minimum.
        if (source.Length < 6) return [];
        var positions = new List<int>();
        var groups = 0;
        var fences = 0;
        var environments = 0;
        var insideBars = false;
        var hasOperand = false;
        for (var i = 0; i < source.Length;)
        {
            var c = source[i++];
            if (char.IsWhiteSpace(c)) continue;
            if (c == '%') { while (i < source.Length && source[i] is not ('\r' or '\n')) i++; continue; }
            string? command = null;
            if (c == '\\')
            {
                var begin = i;
                i = CommandEnd(source, i);
                command = source[begin..i];
            }
            if (c == '{') { groups++; continue; }
            if (c == '}') { groups--; hasOperand = true; continue; }
            if (groups != 0) continue;
            if (command == "begin") { environments++; continue; }
            if (command == "end") { environments--; continue; }
            if (environments != 0) continue;
            if (command is "left" or "right" or "middle")
            {
                if (command == "left") fences++;
                else if (command == "right") { fences--; hasOperand = true; }
                i = SkipToken(source, i); // the delimiter may itself be a control sequence
                continue;
            }
            if (c is '(' or '[' || command is "{" or "lbrace" or "langle" or "lvert" or "lVert" or "lfloor" or "lceil")
            { fences++; continue; }
            if (c is ')' or ']' || command is "}" or "rbrace" or "rangle" or "rvert" or "rVert" or "rfloor" or "rceil")
            { fences--; hasOperand = true; continue; }
            if (command is "over" or "atop" or "choose" or "brack" or "brace" or "root") return [];
            if (fences != 0) continue;
            if (c == '|' || command is "vert" or "Vert" or "|")
            { insideBars = !insideBars; hasOperand = !insideBars; continue; }
            if (insideBars) continue;
            if (command == "nobreak") continue;
            if (command == "allowbreak")
            {
                if (hasOperand && Next(source, i) != '\0') positions.Add(i);
                continue;
            }
            if (c is '^' or '_')
            {
                i = SkipToken(source, i);
                continue;
            }
            if (command is "not" or "displaystyle" or "textstyle" or "scriptstyle" or "scriptscriptstyle"
                or "," or ";" or ":" or "!" or " " or "quad" or "qquad" or "enspace" or "thinspace") continue;
            if (command is "sqrt" or "cfrac" && Next(source, i) == '[')
            {
                while (char.IsWhiteSpace(source[i])) i++;
                for (i++; i < source.Length && source[i] != ']';)
                    i = char.IsWhiteSpace(source[i]) ? i + 1 : SkipToken(source, i);
                if (i == source.Length) return [];
                i++;
            }
            // Do not insert a whatsit between the arguments of an unbraced TeX macro.
            var arguments = command switch
            {
                "frac" or "dfrac" or "tfrac" or "binom" or "dbinom" or "tbinom" or "overset" or "underset" or "stackrel" => 2,
                "genfrac" => 6,
                "cfrac" => 2,
                "sqrt" or "mathbb" or "mathcal" or "mathfrak" or "mathrm" or "mathbf" or "mathsf" or "mathtt" or "mathit" or "mathnormal"
                    or "boldsymbol" or "pmb" or "hat" or "widehat" or "bar" or "overline" or "underline" or "vec" or "dot" or "ddot"
                    or "tilde" or "widetilde" or "breve" or "check" or "acute" or "grave" or "boxed" or "pmod" => 1,
                _ => 0
            };
            if (arguments != 0)
            {
                for (var n = 0; n < arguments; n++) i = SkipToken(source, i);
                hasOperand = true;
                continue;
            }
            if (c is '=' or '<' or '>' or '+' or '-' or '/' or '*' or ',' || command is not null && Operators.Contains(command))
            {
                if (hasOperand && Next(source, i) is not ('\0' or '^' or '_' or '\'' or '=' or '>' or '<')
                    && !source.AsSpan(i).TrimStart().StartsWith(@"\nobreak", StringComparison.Ordinal))
                    positions.Add(i);
                hasOperand = false;
            }
            else hasOperand = true;
        }
        return groups == 0 && fences == 0 && environments == 0 ? positions.ToArray() : [];
    }

    private static int CommandEnd(string source, int i)
    {
        if (i >= source.Length || !char.IsAsciiLetter(source[i])) return Math.Min(source.Length, i + 1);
        while (i < source.Length && char.IsAsciiLetter(source[i])) i++;
        return i;
    }

    private static char Next(string source, int i)
    {
        while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
        return i < source.Length ? source[i] : '\0';
    }

    private static int SkipToken(string source, int i)
    {
        while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
        if (i >= source.Length) return i;
        var c = source[i++];
        if (c == '\\') return CommandEnd(source, i);
        if (c != '{') return i;
        var depth = 1;
        while (depth != 0 && i < source.Length)
        {
            switch (source[i++])
            {
                case '{': depth++; break;
                case '}': depth--; break;
                case '\\': i = CommandEnd(source, i); break;
                case '%': while (i < source.Length && source[i] is not ('\r' or '\n')) i++; break;
            }
        }
        return i;
    }

    internal static void AppendMarked(StringBuilder document, string source, int[] positions, int page)
    {
        var copied = 0;
        for (var n = 0; n < positions.Length; n++)
        {
            document.Append(source, copied, positions[n] - copied).Append(@"\texeedbreak{").Append(page).Append('-').Append(n + 1).Append('}');
            copied = positions[n];
        }
        document.Append(source, copied, source.Length - copied);
    }
}
