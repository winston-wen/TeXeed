using System.Text.RegularExpressions;

namespace TeXeed.App.Controls;

/// <summary>Only math notation enters the standalone TeX document, never document/programming commands.</summary>
internal static class ChatTexInput
{
    private static readonly HashSet<string> Commands = new(("""
        alpha beta gamma delta epsilon varepsilon zeta eta theta vartheta iota kappa varkappa lambda mu nu xi pi varpi rho varrho sigma varsigma tau upsilon phi varphi chi psi omega
        Gamma Delta Theta Lambda Xi Pi Sigma Upsilon Phi Psi Omega
        frac dfrac tfrac cfrac binom dbinom tbinom genfrac sqrt root over atop choose braces brack
        sum prod coprod int iint iiint iiiint idotsint oint bigcap bigcup bigsqcup bigvee bigwedge bigodot bigotimes bigoplus biguplus
        limits nolimits displaylimits substack sideset overset underset stackrel
        sin cos tan cot sec csc sinh cosh tanh coth arcsin arccos arctan log ln lg exp lim limsup liminf sup inf max min det dim ker gcd hom Pr arg deg mod bmod pmod pod
        mathbb mathcal mathfrak mathrm mathbf mathsf mathtt mathit mathnormal boldsymbol pmb operatorname
        text textrm textsf texttt textnormal textbf textit emph mbox
        displaystyle textstyle scriptstyle scriptscriptstyle
        left right middle big Big bigg Bigg bigl Bigl biggl Biggl bigr Bigr biggr Biggr bigm Bigm biggm Biggm
        lbrace rbrace langle rangle lvert rvert lVert rVert vert Vert lfloor rfloor lceil rceil backslash
        hat widehat bar overline underline vec dot ddot dddot ddddot tilde widetilde breve check acute grave mathring
        overbrace underbrace overrightarrow overleftarrow overleftrightarrow underrightarrow underleftarrow underleftrightarrow
        phantom hphantom vphantom smash strut mathstrut boxed fbox hspace vspace kern mkern mspace quad qquad enspace thinspace medspace thickspace negthinspace negmedspace negthickspace
        le leq leqslant ge geq geqslant ne neq equiv approx sim simeq cong propto asymp doteq ll gg prec succ preceq succeq subset supset subseteq supseteq subsetneq supsetneq
        in notin ni owns not parallel nparallel mid nmid perp models vdash dashv Vdash vDash Vvdash
        pm mp times div cdot ast star circ bullet odot oplus ominus otimes oslash cap cup uplus sqcap sqcup vee wedge land lor setminus smallsetminus wr diamond triangleleft triangleright bigtriangleup bigtriangledown
        to gets mapsto longmapsto hookrightarrow hookleftarrow rightarrow leftarrow leftrightarrow Rightarrow Leftarrow Leftrightarrow longrightarrow longleftarrow longleftrightarrow Longrightarrow Longleftarrow Longleftrightarrow implies impliedby iff
        uparrow downarrow updownarrow Uparrow Downarrow Updownarrow nearrow searrow swarrow nwarrow rightleftharpoons leftrightharpoons rightharpoonup rightharpoondown leftharpoonup leftharpoondown xrightarrow xleftarrow
        dots ldots cdots vdots ddots dotsc dotsb dotsm dotsi dotso
        infinity infty partial nabla forall exists nexists neg lnot emptyset varnothing aleph beth gimel daleth hbar hslash ell imath jmath Re Im wp top bot angle measuredangle sphericalangle triangle square Box Diamond surd prime backprime degree
        checkmark blacksquare blacktriangle blacktriangledown blacklozenge lozenge complement natural flat sharp S P dag ddag dagger ddagger copyright pounds
        begin end cr hline hdotsfor multicolumn
        mathop mathrel mathbin mathord mathopen mathclose mathpunct
        tiny scriptsize footnotesize small normalsize large Large LARGE huge Huge
        """).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    private static readonly HashSet<string> Environments = new("aligned alignedat gathered split cases matrix pmatrix bmatrix Bmatrix vmatrix Vmatrix smallmatrix array".Split(' '), StringComparer.Ordinal);

    internal static void Validate(string source)
    {
        if (source.Length > 16384 || source.Contains("^^")) throw new ArgumentException("公式过长或含有不支持的 TeX 控制序列。");
        var depth = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '%') { while (i < source.Length && source[i] is not '\r' and not '\n') i++; continue; }
            if (c == '$' || (char.IsControl(c) && !char.IsWhiteSpace(c))) throw new ArgumentException("公式中包含不支持的定界符或控制字符。");
            if (c == '{') { if (++depth > 64) throw new ArgumentException("公式嵌套过深。"); }
            else if (c == '}' && --depth < 0) throw new ArgumentException("公式花括号不匹配。");
            if (c != '\\') continue;
            if (++i >= source.Length) throw new ArgumentException("公式尚未完整。");
            if (!char.IsAsciiLetter(source[i]))
            {
                if (!"\\{}%_#&$ ,;:!|/".Contains(source[i])) throw new ArgumentException("不支持的数学控制符。");
                continue;
            }
            var start = i;
            while (i < source.Length && char.IsAsciiLetter(source[i])) i++;
            var command = source[start..i]; i--;
            if (!Commands.Contains(command)) throw new ArgumentException("不支持的数学命令：\\" + command);
            if (command is "begin" or "end")
            {
                var match = Regex.Match(source[(i + 1)..], @"^\s*\{([A-Za-z]+)\}");
                if (!match.Success || !Environments.Contains(match.Groups[1].Value)) throw new ArgumentException("仅支持数学环境。");
            }
        }
        if (depth != 0) throw new ArgumentException("公式花括号不匹配。");
    }
}
