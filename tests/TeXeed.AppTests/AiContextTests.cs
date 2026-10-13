using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiContextBudget(Action<bool, string> check)
    {
        static JsonNode[] HistoryMessages(JsonArray input) => input.Take(input.Count - 1).Select(n => n!).ToArray();
        static string Content(JsonNode message) => message["content"]!.GetValue<string>();

        // Seven older long answers can consume nearly all the budget. The latest
        // instruction must win that competition, while retained pairs keep their order.
        var history = Enumerable.Range(0, 7).Select(i => new AiTurn($"old-{i}", new string((char)('a' + i), 6800))).ToList();
        var latestQuestion = "保留书籍文档类、正文和引用；只修复模板接入。" + new string('约', 340);
        var latestAnswer = "收到，保留这些约束。" + new string('定', 340);
        history.Add(new(latestQuestion, latestAnswer));
        var input = AiResponses.MakeInput([new() { Question = "继续修复编译" }], history);
        var messages = HistoryMessages(input);
        check(messages.Length >= 2 && Content(messages[^2]) == latestQuestion && Content(messages[^1]) == latestAnswer,
            "Older long answers displaced the latest user constraints");
        check(messages.Sum(m => Content(m).Length) <= 48000, "History exceeded the request context budget");
        check(messages.Select((message, index) => message["role"]!.GetValue<string>() == (index % 2 == 0 ? "user" : "assistant")).All(matches => matches),
            "History budgeting split or reordered user/assistant pairs");
        var expected = history.Where(t => messages.Any(m => Content(m) == t.Question))
            .SelectMany(t => new[] { t.Question, t.Answer });
        check(messages.Select(Content).SequenceEqual(expected), "Retained history changed chronological order or content");
        check(!messages.Any(m => Content(m) == "old-0") && input[^1]!["content"]![0]!["text"]!.GetValue<string>().Contains("继续修复编译"),
            "History budgeting did not discard oldest context or lost the current task");

        var many = Enumerable.Range(0, 12).Select(i => new AiTurn($"问题 {i}", $"回答 {i}")).ToList();
        many.Insert(10, new("没有保存回复的旧问题", ""));
        messages = HistoryMessages(AiResponses.MakeInput([], many));
        check(messages.Length == 16 && Content(messages[0]) == "问题 4" && Content(messages[^2]) == "问题 11",
            "History did not retain the latest eight answered turns");
        check(!messages.Any(m => Content(m).Contains("没有保存回复")), "Empty legacy turn consumed a history slot");

        var boundary = new[] { new AiTurn("旧", new string('文', 47899)), new AiTurn("近", new string('字', 99)) };
        messages = HistoryMessages(AiResponses.MakeInput([], boundary));
        check(messages.Length == 4 && messages.Sum(m => Content(m).Length) == 48000,
            "Exactly fitting history was unnecessarily discarded");
        boundary[0] = boundary[0] with { Answer = boundary[0].Answer + "超" };
        messages = HistoryMessages(AiResponses.MakeInput([], boundary));
        check(messages.Length == 2 && Content(messages[0]) == "近", "Over-budget context displaced a recent turn");

        var longAnswer = "回答开头：已找到模板。" + new string('文', 70000) + "回答结尾：仍需验证编译。";
        var constraints = "继续时必须保留 book 文档类、数学公式及全部引用，不可删除正文。";
        var oversizedLatest = new AiTurn(constraints, longAnswer);
        input = AiResponses.MakeInput([new() { Question = "继续" }], [new("较早问题", "较早回复"), oversizedLatest]);
        messages = HistoryMessages(input);
        check(messages.Length == 2 && Content(messages[0]) == constraints, "Oversized latest answer erased or truncated the latest user constraints");
        var shortened = Content(messages[1]);
        check(shortened.StartsWith("回答开头：已找到模板。") && shortened.EndsWith("回答结尾：仍需验证编译。") && shortened.Contains("已截断上轮回答") && shortened.Contains("中间内容已省略"),
            "Truncated answer lost its useful boundaries or pretended to be complete");
        check(messages.Sum(m => Content(m).Length) <= 48000 && shortened.Length < longAnswer.Length && oversizedLatest.Answer == longAnswer,
            "Long-turn compaction exceeded its budget or mutated saved history");

        var enormousQuestion = "问题开头：保留已有模板。" + new string('问', 60000) + "问题结尾：禁止删除任何正文。";
        messages = HistoryMessages(AiResponses.MakeInput([], [new(enormousQuestion, longAnswer)]));
        check(Content(messages[0]).StartsWith("问题开头：保留已有模板。") && Content(messages[0]).EndsWith("问题结尾：禁止删除任何正文。") && Content(messages[0]).Contains("已截断上轮问题"),
            "An individually oversized question was omitted or silently cut");
        check(Content(messages[1]).StartsWith("回答开头：已找到模板。") && Content(messages[1]).EndsWith("回答结尾：仍需验证编译。") && messages.Sum(m => Content(m).Length) <= 48000,
            "Oversized question left no bounded answer context");

        var unicodeAnswer = "开头" + string.Concat(Enumerable.Repeat("😀", 40000)) + "结尾";
        messages = HistoryMessages(AiResponses.MakeInput([], [new("保留 Unicode 字符", unicodeAnswer)]));
        var unicodeIsValid = true;
        try { _ = new System.Text.UTF8Encoding(false, true).GetBytes(Content(messages[1])); }
        catch (System.Text.EncoderFallbackException) { unicodeIsValid = false; }
        check(unicodeIsValid && Content(messages[1]).EndsWith("结尾") && messages.Sum(m => Content(m).Length) <= 48000,
            "History truncation split a UTF-16 character or exceeded the budget");

        var fullBudgetQuestion = new string('问', 48000);
        messages = HistoryMessages(AiResponses.MakeInput([], [new(fullBudgetQuestion, "旧回复") ]));
        check(messages.Length == 1 && Content(messages[0]) == fullBudgetQuestion,
            "A question that alone fits the budget was shortened to make room for the answer");
    }
}
