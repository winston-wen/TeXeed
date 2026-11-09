using System.Text.Json.Nodes;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void CheckAiDocumentContext()
    {
        var source = "BEGIN_DOCUMENT_" + new string('x', 200_000) + "_END_DOCUMENT";
        var entries = new[] { new AiEntry { Title = "共同文稿及评论", Question = source } };
        var input = AiResponses.MakeDocumentInput(entries, []);
        if (!input.ToJsonString().Contains(source, StringComparison.Ordinal))
            throw new InvalidOperationException("The whole long document did not reach the model input.");
        var turn = new AiTurn("重试原问题", "已中止") { Segments = [new AiSegment { Kind = "activity", Text = "修改文件", Details = "已完成写入" }] };
        var retryInput = AiResponses.MakeDocumentInput(entries, [turn]).ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (!retryInput.Contains(source, StringComparison.Ordinal) || !retryInput.Contains("已完成写入", StringComparison.Ordinal))
            throw new InvalidOperationException("Retry dropped the document or completed tool observations.");
        var overflow = AiFailure.FromEvent(JsonNode.Parse("""{"error":{"code":"context_length_exceeded","message":"context window exceeded"}}"""), 400);
        if (overflow is not AiContextLimitException || AiResponses.IsNetworkFailure(overflow, CancellationToken.None) || !overflow.Message.Contains("未截断", StringComparison.Ordinal))
            throw new InvalidOperationException("Context overflow was treated as a transient failure or hid the intact document.");
        var invalidRequest = AiFailure.FromEvent(JsonNode.Parse("""{"error":{"code":"invalid_request","message":"unsupported parameter"}}"""), 400);
        if (invalidRequest is AiContextLimitException || AiResponses.IsNetworkFailure(invalidRequest, CancellationToken.None))
            throw new InvalidOperationException("An unrelated HTTP 400 was misclassified or retried.");
        var supplementary = new string('x', 599) + "😀" + new string('y', 100);
        var preview = AiPanel.SubmittedPreview(supplementary);
        if (!preview.EndsWith("…（其余略）", StringComparison.Ordinal) || preview.Contains('\ud83d') || AiPanel.SubmittedPreview("短附言") != "短附言")
            throw new InvalidOperationException("Pending note preview cut a Unicode character or failed to mark truncation.");
        Console.WriteLine("Document input preserves full text and retry observations; context overflow is not retried.");
    }
}
