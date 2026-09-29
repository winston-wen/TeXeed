using System.Text;
using System.Text.RegularExpressions;

namespace TeXeed.Core;

public sealed record TitlePart(string Kind, string Text = "", IReadOnlyList<TitlePart>? Children = null,
    IReadOnlyList<TitlePart>? Second = null);

/// <summary>Bounded, non-executing LaTeX title parser. Unknown commands remain visible.</summary>
public static class LatexTitle
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["log"]="log ", ["ln"]="ln ", ["exp"]="exp ", ["sin"]="sin ", ["cos"]="cos ", ["tan"]="tan ", ["cot"]="cot ", ["lim"]="lim ", ["max"]="max ", ["min"]="min ", ["Pr"]="Pr ", ["det"]="det ", ["gcd"]="gcd ", ["bmod"]=" mod ",
        ["alpha"]="α", ["beta"]="β", ["gamma"]="γ", ["delta"]="δ", ["epsilon"]="ε", ["varepsilon"]="ϵ",
        ["theta"]="θ", ["lambda"]="λ", ["mu"]="μ", ["nu"]="ν", ["xi"]="ξ", ["pi"]="π", ["rho"]="ρ",
        ["sigma"]="σ", ["tau"]="τ", ["phi"]="φ", ["varphi"]="ϕ", ["chi"]="χ", ["psi"]="ψ", ["omega"]="ω",
        ["Gamma"]="Γ", ["Delta"]="Δ", ["Theta"]="Θ", ["Lambda"]="Λ", ["Xi"]="Ξ", ["Pi"]="Π", ["Sigma"]="Σ", ["Phi"]="Φ", ["Psi"]="Ψ", ["Omega"]="Ω",
        ["sum"]="∑", ["prod"]="∏", ["int"]="∫", ["oint"]="∮", ["infty"]="∞", ["partial"]="∂", ["nabla"]="∇",
        ["times"]="×", ["cdot"]="·", ["oplus"]="⊕", ["otimes"]="⊗", ["pm"]="±", ["mp"]="∓",
        ["le"]="≤", ["leq"]="≤", ["ge"]="≥", ["geq"]="≥", ["ne"]="≠", ["neq"]="≠", ["approx"]="≈", ["equiv"]="≡",
        ["in"]="∈", ["notin"]="∉", ["subset"]="⊂", ["subseteq"]="⊆", ["supset"]="⊃", ["cup"]="∪", ["cap"]="∩", ["emptyset"]="∅",
        ["forall"]="∀", ["exists"]="∃", ["neg"]="¬", ["land"]="∧", ["lor"]="∨", ["to"]="→", ["rightarrow"]="→", ["leftarrow"]="←", ["leftrightarrow"]="↔", ["Rightarrow"]="⇒", ["Leftarrow"]="⇐", ["iff"]="⇔", ["mapsto"]="↦",
        ["langle"]="⟨", ["rangle"]="⟩", ["lceil"]="⌈", ["rceil"]="⌉", ["lfloor"]="⌊", ["rfloor"]="⌋", ["vert"]="|", ["Vert"]="‖",
        ["ldots"]="…", ["dots"]="…", ["cdots"]="⋯", ["vdots"]="⋮", ["ddots"]="⋱", ["ell"]="ℓ", ["hbar"]="ℏ",
        ["LaTeX"]="LaTeX", ["TeX"]="TeX", ["textendash"]="–", ["textemdash"]="—", ["textbackslash"]="\\",
        ["quad"]="  ", ["qquad"]="    ", ["enspace"]=" ", ["space"]=" ", ["ldotp"]=".", ["%"]="%", ["&"]="&", ["_"]="_", ["#"]="#", ["$"]="$", ["{"]="{", ["}"]="}", ["\\"]=" ", [","]=" ", [";"]=" ", [":"]=" ", ["!"]="", [" "]=" "
    };
    public static IReadOnlyList<TitlePart> Parse(string source) => new Reader(source.Length > 8192 ? source[..8192] + "…" : source).Read(0);
    public static string PlainText(string source) => Regex.Replace(Plain(Parse(source)), @"\s+", " ").Trim();
    private static string Plain(IEnumerable<TitlePart> parts) => string.Concat(parts.Select(p => p.Kind switch
    {
        "text" => p.Text, "fraction" => $"({Plain(p.Children!)})/({Plain(p.Second!)})", "binomial" => $"({Plain(p.Children!)} choose {Plain(p.Second!)})", "sqrt" => $"√({Plain(p.Children!)})",
        "sup" => "^" + Plain(p.Children!), "sub" => "_" + Plain(p.Children!), _ => Plain(p.Children ?? [])
    }));

    private sealed class Reader(string source)
    {
        private int _position;
        internal List<TitlePart> Read(int depth, char end = '\0')
        {
            var result = new List<TitlePart>();
            if (depth > 32) { result.Add(new("text", source[_position..])); _position = source.Length; return result; }
            while (_position < source.Length)
            {
                var c = source[_position++];
                if (c == end) break;
                if (c == '{') { result.Add(new("group", Children: Read(depth + 1, '}'))); continue; }
                if (c == '}') { result.Add(new("text", "}")); continue; }
                if (c is '^' or '_') { result.Add(new(c == '^' ? "sup" : "sub", Children: Argument(depth + 1))); continue; }
                if (c == '$')
                {
                    if (_position < source.Length && source[_position] == '$') _position++;
                    var mathStart = _position;
                    var children = Read(depth + 1, '$');
                    var mathEnd = _position > mathStart && source[_position - 1] == '$' ? _position - 1 : _position;
                    result.Add(new("math", source[mathStart..mathEnd], Children: children));
                    if (_position < source.Length && source[_position] == '$') _position++;
                    continue;
                }
                if (c == '%') { while (_position < source.Length && source[_position] != '\n') _position++; continue; }
                if (c != '\\')
                {
                    var text = new StringBuilder(c == '~' || char.IsWhiteSpace(c) ? " " : c.ToString());
                    while (_position < source.Length && source[_position] != end && !"{}^_$%\\~".Contains(source[_position]))
                    { var next = source[_position++]; text.Append(char.IsWhiteSpace(next) ? ' ' : next); }
                    result.Add(new("text", text.ToString())); continue;
                }
                if (_position == source.Length) { result.Add(new("text", "\\")); break; }
                var start = _position;
                if (char.IsLetter(source[_position])) { while (_position < source.Length && char.IsLetter(source[_position])) _position++; }
                else _position++;
                var command = source[start.._position];
                if (command is "(" or "[")
                {
                    var close = command == "(" ? @"\)" : @"\]";
                    var endMath = source.IndexOf(close, _position, StringComparison.Ordinal);
                    if (endMath < 0) endMath = source.Length;
                    var math = source[_position..endMath];
                    result.Add(new("math", math, Children: new Reader(math).Read(depth + 1)));
                    _position = Math.Min(source.Length, endMath + close.Length);
                    continue;
                }
                if (command.Length > 1 || char.IsLetter(command[0]))
                    while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
                if (Symbols.TryGetValue(command, out var symbol)) { result.Add(new("text", symbol)); continue; }
                if (command is "(" or ")" or "[" or "]" or "left" or "right" or "protect" or "limits" or "nolimits") continue;
                if (command is "label" or "index" or "footnote" or "thanks") { Argument(depth + 1); continue; }
                if (command is "textcolor" or "colorbox" or "href") { Argument(depth + 1); result.Add(new("group", Children: Argument(depth + 1))); continue; }
                if (command == "texorpdfstring") { result.Add(new("group", Children: Argument(depth + 1))); Argument(depth + 1); continue; }
                if (command is "frac" or "dfrac" or "tfrac" or "binom")
                { result.Add(new(command == "binom" ? "binomial" : "fraction", Children: Argument(depth + 1), Second: Argument(depth + 1))); continue; }
                if (command == "sqrt") { if (_position < source.Length && source[_position] == '[') { _position++; Read(depth + 1, ']'); } result.Add(new("sqrt", Children: Argument(depth + 1))); continue; }
                var kind = command switch
                {
                    "textbf" or "mathbf" or "boldsymbol" => "bold", "textit" or "mathit" or "emph" => "italic",
                    "texttt" or "mathtt" => "mono", "textsf" or "mathsf" => "sans", "textrm" or "mathrm" or "text" or "operatorname" or "mbox" => "roman",
                    "mathbb" => "blackboard", "mathcal" or "mathscr" => "italic", "underline" => "underline", "overline" or "bar" => "overline", _ => null
                };
                if (kind is not null) { result.Add(new(kind, Children: Argument(depth + 1))); continue; }
                var accent = command switch { "hat" or "^" => "\u0302", "tilde" or "~" => "\u0303", "vec" => "\u20d7", "dot" or "." => "\u0307", "ddot" or "\"" => "\u0308", "'" => "\u0301", "`" => "\u0300", _ => null };
                if (accent is not null) { result.Add(new("text", Plain(Argument(depth + 1)) + accent)); continue; }
                result.Add(new("text", "\\" + command));
            }
            return result;
        }
        private IReadOnlyList<TitlePart> Argument(int depth)
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
            if (_position >= source.Length) return [];
            if (source[_position] == '{') { _position++; return Read(depth, '}'); }
            if (source[_position] == '\\')
            {
                var start = _position++;
                while (_position < source.Length && char.IsLetter(source[_position])) _position++;
                if (_position == start + 1 && _position < source.Length) _position++;
                return new Reader(source[start.._position]).Read(depth);
            }
            return [new("text", source[_position++].ToString())];
        }
    }
}
