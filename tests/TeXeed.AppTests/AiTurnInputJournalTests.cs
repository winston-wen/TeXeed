using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckInputJournalAsync(string output)
    {
        var source = Path.Combine(output, "input-journal.tex");
        var book = new AiNotebook();
        var question = new AiSegment { Id = "choose", Kind = "final", Text = "选哪条？{{text:path}}" };
        var reply = new AiInputReply([question.Id], new() { ["text:path"] = "甲" }, null, DateTimeOffset.UtcNow);
        var turn = AiForms.WithSubmissions(new AiTurn("请分步讲解", question.Text)
        {
            Segments = [question], InputReplies = [reply], EndReason = "input-resuming", InputAttemptId = "attempt-a", Incomplete = true,
            RevisionHistory = [new AiTurnRevision("saved-question", "user", DateTimeOffset.UtcNow, AiRevisionText.Parse(question.Id, question.Text).ToList())
                { Sources = new() { [question.Id] = question.Text } }]
        }, []);
        book.Turns.Add(turn);
        AiStorage.Save(source, book);
        var buffer = new AiStreamBuffer();
        var journal = await AiTurnCheckpoint.StartAsync(source, book.Active.Id, book.Active.Title, turn, buffer);
        buffer.Add(new("tool", "activity", "已保存更改", Details: "first call receipt"));
        buffer.Add(new("answer", "final", "甲的解释"));
        await journal.StopAsync();
        var path = AiTurnCheckpoint.PathFor(source);
        AiStorage.Save(source, book);
        if (!File.Exists(path)) throw new InvalidOperationException("An unrelated save retired the in-flight continuation journal.");
        var stale = File.ReadAllBytes(path);
        var recovered = AiStorage.Load(source);
        var partial = recovered.Turns.Single();
        if (partial.Id != turn.Id || !partial.CanRetryInputReply || partial.AnsweredAt is not null || partial.InputReplies.Length != 1 ||
            !partial.Answer.Contains("甲的解释") || partial.RevisionHistory?.Last().Rows.Any(r => r.Text == "甲的解释") != true ||
            !partial.Segments.Any(s => s.Details == "first call receipt"))
            throw new InvalidOperationException("Crash recovery lost the confirmed input, partial answer or completed tool receipt.");
        recovered.Turns[0] = partial with { Answer = "已经完成", Incomplete = false, EndReason = null, AnsweredAt = DateTimeOffset.UtcNow };
        AiStorage.Save(source, recovered);
        if (File.Exists(path)) throw new InvalidOperationException("Completed continuation journal was not retired.");
        File.WriteAllBytes(path, stale);
        if (AiStorage.Load(source).Turns.Single().Answer != "已经完成")
            throw new InvalidOperationException("A stale input journal replaced a completed answer.");
        AiStorage.Save(source, recovered);
    }
}
