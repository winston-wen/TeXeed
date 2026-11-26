using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeXeed.App.AI;

public sealed record AiRevisionRequest(string TargetTurnId, string TaskId, bool AwaitingReply = false, bool Cancelled = false);
internal delegate Task<string> AiRevisionRunner(string name, string arguments, CancellationToken token);

internal static class AiAnswerRevision
{
    private const string Target = @"(?<target>第\s*(?<number>[0-9一二三四五六七八九十百两零〇]+)\s*轮|上一轮|上轮|刚才那轮)";
    private const string Verb = @"(?:原位改写|就地改写|改写|重写|修改|润色|精简|改短|扩写|整理|改成|改为)";
    private static readonly Regex Command = new(
        @"^\s*(?:请(?:你)?(?:帮我)?|麻烦(?:你)?(?:帮我)?|帮我)?\s*(?:直接|现在)?(?:(?<verbFirst>" + Verb + @")\s*" + Target +
        @"(?:的)?(?:回答|答复|回复|正文|答稿)?|(?:把|将|给)\s*" + Target + @"(?:的)?(?:回答|答复|回复|正文|答稿)?\s*" + Verb + @")",
        RegexOptions.CultureInvariant);
    private static readonly Regex Continuation = new(@"^\s*(?:请)?继续(?:刚才的|上次的)?改稿(?:[：:，,\s]|$)");
    private static readonly Regex Cancellation = new(@"^\s*(?:请)?(?:取消|停止|结束|放弃|不用再)(?:这次|本次)?改稿(?:了)?[。！!\s]*$");

    private static readonly Regex WriteBack = new(@"^\s*(?:你)?(?:倒是|请(?:你)?|麻烦(?:你)?|帮我)?\s*(?:把(?:刚才|上面|这段)(?:的)?(?:替换稿|改稿|文字|内容)?|按(?:刚才|上面)(?:的)?(?:方案|说法|改稿))?(?:直接|现在|就)?(?:写过去|写回(?:去|原文|原回答)?|改到原文里|保存到原文|应用(?:到原文|这次修改))\s*(?:啊|吧|呀|了)?[！!。.\s]*$");
    private const string SelectedTarget = @"(?:选中(?:的)?|引用(?:的)?|这)(?:一)?(?:段落|段(?:文字|话)?|处)";
    private static readonly Regex Selected = new(@"^\s*(?:请(?:你)?|麻烦(?:你)?|帮我)?(?:直接)?(?:(?:把|将)?" + SelectedTarget + @"\s*" + Verb + "|" + Verb + SelectedTarget + ")");
    private static readonly Regex NumberedTurn = new(@"第\s*([0-9一二三四五六七八九十百两零〇]+)\s*轮");
    private static readonly Regex ScopedCommand = new(@"^\s*(?:(?:请(?:你)?|麻烦(?:你)?|帮我)(?:直接|现在)?在|(?:请(?:你)?|麻烦(?:你)?|帮我)?(?:直接|现在)?(?:把|将))\s*" + Target + @"(?:的|中|里)[^。！？?「」“”\n]{0,160}?(?:改成|改为|替换成|替换为|改写为|删掉|删除|去掉|补上|加上|写回)");
    private static readonly Regex DiscussionOnly = new(@"(?:^|[，,。；;：:\s])(?:只(?:给(?:我)?(?:建议|示例|草案)|提建议|讨论方案)|(?:先别|暂不|不要|不用)(?:实际|直接|真的)?(?:写回|保存|动原文|改原文))|(?:是什么(?:意思)?|会怎样|是否合适|可以吗|行吗|好吗|好不好)[？?]?\s*$");

    internal static AiRevisionRequest? Resolve(string prompt, IReadOnlyList<AiTurn> turns, IReadOnlyList<AiForms.Submission> forms, IReadOnlyList<AiEntry>? references = null)
    {
        if (DiscussionOnly.IsMatch(prompt)) return null;
        var match = Command.Match(prompt);
        if (!match.Success) match = ScopedCommand.Match(prompt);
        if (match.Success)
        {
            var rest = prompt[match.Length..].TrimStart();
            if (match.Groups["verbFirst"].Success && rest.Length > 0 && !char.IsPunctuation(rest[0]) &&
                !Regex.IsMatch(rest, @"^(?:中|里|的(?:第|例|结论|开头|结尾|标题|正文)|让|使|把|将|删|加|补|增加|减少|去掉|保留|写|改|压缩|分成|缩短|扩充|并|用|按)"))
                return null;
            if (Regex.IsMatch(rest, @"^[：:，,\s]*(?:是什么|是什么意思|是指|可以吗|能行吗|吗|的(?:方法|意思|步骤|影响)|这(?:个|件事|句话)|(?:后|以后)?(?:会不会|会怎样|会影响|怎么做|有什么|是否)|(?:不要|先别|暂不|不必)(?:动|改|写|整理)|只(?:讨论|解释|分析)|[？?]|(?:和|以及|、|到|至|与)\s*第?\s*[0-9一二三四五六七八九十])"))
                return null;
            var number = match.Groups["number"];
            var index = number.Success ? Ordinal(number.Value) - 1 : turns.Count - 1;
            return index >= 0 && index < turns.Count && !turns[index].IsIncomplete
                ? new(turns[index].Id, Guid.NewGuid().ToString("N")) : null;
        }
        var continuation = Continuation.IsMatch(prompt);
        var cancel = Cancellation.IsMatch(prompt);
        var writeBack = WriteBack.IsMatch(prompt);
        var previous = turns.LastOrDefault();
        var clarification = previous is { IsIncomplete: false, RevisionRequest.AwaitingReply: true } &&
            Regex.IsMatch(prompt, @"^\s*(?:保留|删掉|加上|去掉|改成|选(?:择)?第|就按|按这个|同意修改)(?!.*(?:[？?]|不改原文))");
        if (!writeBack && !cancel && !continuation && !clarification && (prompt.Length > 0 || forms.Count == 0))
        {
            if (!Selected.IsMatch(prompt)) return null;
            return SelectedRequest();
        }
        var pending = turns.Where(t => t.RevisionRequest is not null).GroupBy(t => t.RevisionRequest!.TaskId)
            .Select(group => group.Last()).Where(t => t.RevisionRequest!.AwaitingReply && !t.IsIncomplete).ToArray();
        var selected = string.IsNullOrWhiteSpace(prompt) || continuation || writeBack
            ? pending.Where(t => forms.Any(f => f.TurnId == t.Id && AiForms.HasResponse(t, f))).ToArray() : [];
        var source = selected.Length == 1 ? selected[0] :
            selected.Length == 0 && pending.Length == 1 && (continuation || cancel || clarification || writeBack && previous == pending[0]) ? pending[0] : null;
        if (source?.RevisionRequest is { } task) return task with { AwaitingReply = false, Cancelled = cancel };
        if (!writeBack || previous is null || previous.IsIncomplete || previous.RevisionRequest is { Cancelled: true }) return null;
        if (references?.Any(e => e.AnswerAnchor is not null) == true) return SelectedRequest();
        // “你倒是写过去啊” explicitly requests the proposal just discussed. Bind the target
        // from the user's preceding message, never from an AI reply or quoted document.
        if (!Regex.IsMatch(previous.Answer, @"可(?:以)?(?:改|写|替换)|改(?:写)?成|替换(?:稿|文字)|改为|写回")) return null;
        if (previous.RevisionRequest is { } priorRevision && turns.Any(t => t.Id == priorRevision.TargetTurnId && !t.IsIncomplete))
            return new(priorRevision.TargetTurnId, Guid.NewGuid().ToString("N"));
        var priorText = previous.Prompt ?? previous.Question;
        var numbers = NumberedTurn.Matches(priorText).Select(m => Ordinal(m.Groups[1].Value)).Distinct().ToArray();
        if (numbers.Length != 1 || numbers[0] < 1 || numbers[0] >= turns.Count) return null;
        var target = turns[numbers[0] - 1];
        return target.IsIncomplete ? null : new(target.Id, Guid.NewGuid().ToString("N"));

        AiRevisionRequest? SelectedRequest()
        {
            var anchors = references?.Select(e => e.AnswerAnchor?.TurnId).OfType<string>().Distinct().ToArray() ?? [];
            return anchors.Length == 1 && turns.Any(t => t.Id == anchors[0] && !t.IsIncomplete) ? new(anchors[0], Guid.NewGuid().ToString("N")) : null;
        }
    }

    private static int Ordinal(string text)
    {
        if (int.TryParse(text, out var ordinal)) return ordinal;
        var sum = 0; var digit = 0;
        foreach (var c in text)
        {
            if (c is '十' or '百') { sum += Math.Max(1, digit) * (c == '十' ? 10 : 100); digit = 0; }
            else if (c == '两') digit = 2;
            else if (c == '〇') digit = 0;
            else if ((digit = "零一二三四五六七八九".IndexOf(c)) < 0) return 0;
        }
        return sum + digit;
    }

    internal static bool IsTool(string name) => name is "read_answer_revision" or "read_related_answer_revision" or "apply_answer_revision" or "await_revision_reply";
    internal const string ImpactInstructions = "普通输入框发出的明确改稿指令和右键发起的改稿同样有效。实际调用工具写回原轮。宿主按当前权限处理保存确认。保存结果会列出本会话可能受影响的轮次；用 read_related_answer_revision 读取相关正文，判断论述或公式引用是否随之失效，只对确有必要的连带修改调用 apply_answer_revision。用户拒绝后停止写入。";
    internal const string Instructions = "用户已明确授权整理指定的一轮。先用 read_answer_revision 读取绑定的目标最新版，再用 apply_answer_revision 仅提交需要替换的段落。可以删段、增段、修改标题层级。每段 parts 中 text 为 Markdown 源码。行内计算用 $…$，行间公式独占 level=0 的段，可在定界符内换行。用 TeX 写变量、上下标和转置，避免用 Unicode 上下标拼算式；独立纯数字优先用正文。可改写行间公式，在公式内增改 \\label{标识}，在正文增删 \\eqref{标识}；同轮用标签，跨轮用 \\eqref{轮次:标识}。引用实际公式，勿手写显示编号。若工具报告排版错误，按诊断修正后重新提交。keep 为从零开始的保护对象编号；保留所有 keep 的次序及唯一性。locked 段完整保留。根据用户意思直接保存，成功后简短告知。拿不准时可调用 ask_user 当场询问，或调用 await_revision_reply 后在回答中出题，用表单让用户补充；也可提示其输入「继续改稿：…」。后续澄清仍针对同一原稿。只把用户的要求视作改稿指令。";
    internal static JsonNode Schema()
    {
        var schema = JsonNode.Parse("""
    {"type":"namespace","name":"revision","description":"Edit the answer selected by the user and review necessary dependent changes in this conversation.","tools":[
      {"type":"function","name":"read_answer_revision","description":"Read the target, current version and rows with immutable parts. Start from start_row=0 (includes earlier task instructions and clarifications); follow next_row if present. max_rows is 1–40. keep indices are local to each row.","parameters":{"type":"object","properties":{"start_row":{"type":"integer"},"max_rows":{"type":"integer"}},"required":["start_row","max_rows"],"additionalProperties":false},"strict":true},
      {"type":"function","name":"apply_answer_revision","description":"For each row, use position=replace to edit, or before/after to insert paragraphs beside it. Locked rows allow only insertion before/after. For replace, an empty paragraphs list deletes a row. Each new paragraph has heading level 0 (body) to 6. For parts use kind=text for Markdown including inline TeX, equation references and display math. Put each display formula in its own level=0 paragraph; newlines within it are allowed. Use kind=text for editable source, or kind=keep with the original decimal token id. Preserve all keep tokens and their order; when deleting all text, conversation links automatically move to the preceding paragraph.","parameters":{"type":"object","properties":{"target":{"type":"string"},"version":{"type":"string"},"changes":{"type":"array","items":{"type":"object","properties":{"row":{"type":"string"},"position":{"type":"string","enum":["replace","before","after"]},"paragraphs":{"type":"array","items":{"type":"object","properties":{"level":{"type":"integer"},"parts":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string","enum":["text","keep"]},"value":{"type":"string"}},"required":["kind","value"],"additionalProperties":false}}},"required":["level","parts"],"additionalProperties":false}}},"required":["row","position","paragraphs"],"additionalProperties":false}}},"required":["target","version","changes"],"additionalProperties":false},"strict":true},
      {"type":"function","name":"await_revision_reply","description":"Keep the current revision task open for the user's next clarification. Then write the question with a form in the reply.","parameters":{"type":"object","properties":{},"required":[],"additionalProperties":false},"strict":true}
    ]}
    """)!;
        var tools = schema["tools"]!.AsArray();
        var related = tools[0]!.DeepClone();
        related["name"] = "read_related_answer_revision";
        related["description"] = "Read a potentially affected turn listed by a successful revision's impact scan, before deciding whether to update it.";
        related["parameters"]!["properties"]!["target"] = new JsonObject { ["type"] = "string" };
        related["parameters"]!["required"]!.AsArray().Add("target");
        tools.Add(related);
        return schema;
    }
}
