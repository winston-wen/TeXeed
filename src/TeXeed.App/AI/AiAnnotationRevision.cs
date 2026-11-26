using System.Text.RegularExpressions;
using TeXeed.App.Controls;

namespace TeXeed.App.AI;

internal static class AiAnnotationRevision
{
    internal const string StaleNotice = "所选内容已经变化或无法在最新版中唯一定位。请用户在最新回答中重新选中目标并确认改稿。";
    private const string Lead = @"^\s*(?:请(?:你)?(?:帮我)?|麻烦(?:你)?(?:帮我)?|帮我)?\s*(?:直接|现在|原位|就地)?\s*";
    private const string Object = @"(?:这里|这儿|(?:(?:选中|引用|批注)(?:的)?|这(?:一|个|条)?|该|此|原)?(?:处|段(?:落|文字|话)?|句(?:话|子)?|公式|式子|等式|标题|正文|原文|符号|表达式))";
    private const string Action = @"(?:改(?:写|为|成|一下)?|修改|修正|重写|替换(?:为|成)?|删(?:除|掉|去)?|去掉|补(?:上|入|充)?|加(?:上|入)?|插入|润色|精简|扩写)";
    private const string Number = @"(?:\s*[（(]?\d+[）)]?)?";
    private const string Direction = @"(?:改成|改为|改写为|替换为|替换成|删掉|去掉|补上|加上|加入|插入|写回|改一下)";
    // An object followed by a verb can also describe an earlier edit. Require a
    // requested operation here, e.g. “这段请修改” or “公式6改为…”.
    private static readonly Regex Edit = new(Lead + @"(?:" + Action + @"\s*" + Object + @"|" + Object + Number +
        @"\s*(?:(?:请(?:你)?|麻烦(?:你)?|帮我)\s*(?:直接)?\s*" + Action + "|" + Direction + @")|(?:把|将)[^。！？?\n]{1,160}?" +
        Direction + @"|(?:加入|加上|补上|插入|替换为|改成|改为)(?=\S))");
    private const string Clause = @"(?:^|[。；;，,：:\s])(?:(?:请(?:你)?|这次|这回|本次|目前|现在|暂时|先)\s*)*";
    private const string Negative = @"(?:不要|不用|无需|无须|不必|不需要|先别|别|暂不|禁止|不)(?:再|直接|实际|真的|继续)?";
    private static readonly Regex Veto = new(Clause + @"(?:(?:只(?:要|需)?(?:讨论|解释|分析|给建议|看看))|(?:" +
        Object + Number + @"\s*)?" + Negative + @"(?:修改|改|调整|写回|保存|动原文|执行))");
    private static readonly Regex Discussion = new(@"(?:^|[。；;，,：:\s])(?:能否|要不要|该不该|是否|怎么改|如何改|可不可以|会不会|好不好)|[？?]\s*$|(?:的(?:意思|方法|步骤|影响)|是什么意思)");
    private static readonly Regex FormulaObject = new(Lead + @"(?:(?:把|将|给)\s*|" + Action + @"\s*)?(?:这(?:个|条)?|该)?(?:公式|式子|等式)\s*[（(]?\s*(?:(?<turn>\d+)\s*[:：]\s*)?(?<number>\d+)(?![\d:：])");

    internal static bool ProhibitsWriteBack(string text) => text.Length > 0 && Veto.IsMatch(text);
    internal static bool IsCommand(string text) => Edit.IsMatch(text) && !Discussion.IsMatch(text) && !ProhibitsWriteBack(text);

    internal static bool MatchesRequestedObject(string instruction, AiAnswerAnchor anchor, int sourceTurnNumber)
    {
        var match = FormulaObject.Match(instruction);
        if (!match.Success) return true;
        if (match.Groups["turn"].Success && (!int.TryParse(match.Groups["turn"].Value, out var requestedTurn) ||
            requestedTurn != sourceTurnNumber)) return false;
        return anchor.PreviewNumber is not { Length: > 0 } number || match.Groups["number"].Value == number.Trim('(', '（', ')', '）', ' ');
    }

    internal static bool MatchesSelection(AiEntry entry, AiTurn turn, out string? currentNumber)
    {
        currentNumber = null;
        if (entry.AnswerAnchor is not { } anchor || anchor.TurnId != turn.Id) return false;
        var segment = AiRevisionText.Segments(turn).FirstOrDefault(s => s.Id == anchor.SegmentId);
        if (segment == null || string.IsNullOrWhiteSpace(anchor.Quote)) return false;
        var current = segment.Text;
        var unchanged = AiStorage.Hash(current) == anchor.Revision;
        var original = unchanged ? current : turn.RevisionHistory?.Where(r => anchor.CapturedRevisionId == null || r.Id == anchor.CapturedRevisionId)
            .Select(r => r.Sources.GetValueOrDefault(segment.Id))
            .FirstOrDefault(s => s != null && AiStorage.Hash(s) == anchor.Revision);
        if (original == null) return false;
        if (anchor.PreviewKind == "formula" && !string.IsNullOrWhiteSpace(anchor.PreviewSource))
        {
            var originalTargets = Formulas(original).Where(t => t.Source == anchor.PreviewSource &&
                (anchor.PreviewNumber == null || t.Number == anchor.PreviewNumber)).Take(2).ToArray();
            if (originalTargets.Length != 1) return unchanged && anchor.PreviewNumber == null;
            var selected = originalTargets[0];
            if (unchanged) { currentNumber = selected.Number; return true; }
            // Without a label, a renumbered duplicate can silently turn into its neighbor.
            bool SameFormula(ChatMath.Token t) => t.Label == selected.Label && t.Source == selected.Source && t.Display == selected.Display;
            if (selected.Label == null && Formulas(original).Count(SameFormula) != 1) return false;
            var latest = Formulas(current).Where(SameFormula).Take(2).ToArray();
            if (latest.Length != 1) return false;
            currentNumber = latest[0].Number;
            return true;
        }
        if (unchanged) return true;
        if (anchor.PreviewKind != null || anchor.PreviewTableMarkdown != null) return false;
        var quote = anchor.Quote.Trim();
        bool Unique(string source)
        {
            var visible = AiRevisionText.SelectionText(source).Text;
            var start = visible.IndexOf(quote, StringComparison.Ordinal);
            return start >= 0 && visible.IndexOf(quote, start + 1, StringComparison.Ordinal) < 0;
        }
        return Unique(original) && Unique(current);
    }

    private static IEnumerable<ChatMath.Token> Formulas(string source) =>
        MarkdownView.RevisionMath(source, 0).Tokens.Values.Where(t => t.Reference == null);
}
