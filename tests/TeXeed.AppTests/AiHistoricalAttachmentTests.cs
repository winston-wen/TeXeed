using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiHistoricalAttachmentsAsync()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        static string Complete(string text) => "data: " + new JsonObject
        {
            ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(new JsonObject
            { ["type"] = "message", ["role"] = "assistant", ["phase"] = "final_answer", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }) }
        }.ToJsonString() + "\n\n";
        static bool IsSummary(JsonNode request) => request["instructions"]!.ToString().Contains("会话状态整理器");
        new AiCredentials { ApiKey = "fixture-key-never-send", UseApiKey = true }.Save();

        var temp = Path.Combine(Path.GetTempPath(), "texeed-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var live = Path.Combine(temp, "live.txt"); File.WriteAllText(live, "LIVE_DIRECTORY_BODY");
            var blobPath = Path.Combine(temp, "uploaded.txt"); File.WriteAllText(blobPath, "SAVED_BLOB_BODY", new UTF8Encoding(false));
            var blob = AiAttachmentBlobStore.Store(blobPath).Id;
            var sent = new AiEntry { EntryKind = "attachment", Files = [
                new("one.txt", "text/plain", Encode("LEGACY_UPLOAD_BODY")),
                new("blob.txt", "text/plain", "") { BlobId = blob },
                new("one.png", "image/png", "HISTORY_IMAGE_MARKER"),
                new("one.pdf", "application/pdf", "HISTORY_PDF_MARKER"),
                new("old/folder.txt", "text/plain", Encode("OLD_FOLDER_BODY")) { FolderPath = temp },
                new("old/folder.png", "image/png", "OLD_FOLDER_IMAGE") { FolderPath = temp },
                new(temp + Path.DirectorySeparatorChar, "application/vnd.texeed.directory-reference", "") { ReferenceKind = "directory", ReferencePath = temp } ],
                Images = new([new("PASTED_IMAGE_MARKER")]), PdfBlocks = [new(1, 0, 0, 10, 10, "ANNOTATION_IMAGE_MARKER")] };
            var turn = new AiTurn("请阅读上传的材料", "已阅读。"){ SentAnnotations = [sent] };
            using var handler = new AiSequenceHandler((_, _) => Complete("继续回答。"));
            using var connection = new AiConnection(handler);
            await new AiResponses(connection).RunAsync("fixture", [new AiEntry { Question = "根据之前的材料继续" }], [turn], [], _ => { }, _ => { }, CancellationToken.None);
            var request = handler.Inputs.Single();
            var first = request["input"]!.AsArray().First(item => item?["role"]?.ToString() == "user")!;
            var firstJson = first.ToJsonString();
            Check(firstJson.Contains("LEGACY_UPLOAD_BODY") && firstJson.Contains("SAVED_BLOB_BODY") && firstJson.Contains("HISTORY_IMAGE_MARKER") &&
                firstJson.Contains("HISTORY_PDF_MARKER") && firstJson.Contains("PASTED_IMAGE_MARKER") && firstJson.Contains("ANNOTATION_IMAGE_MARKER"),
                "Uncompacted history did not replay sent text, image, PDF or pasted media snapshots");
            Check(!firstJson.Contains("OLD_FOLDER_BODY") && !firstJson.Contains("OLD_FOLDER_IMAGE") && !firstJson.Contains("LIVE_DIRECTORY_BODY"),
                "History replay expanded an old folder snapshot or read a live directory");
            Check(CountJsonValues(first, "type", "input_image") == 3 && CountJsonValues(first, "type", "input_file") == 1,
                "History media were not restored with the original multimodal types");

            var longText = "HISTORICAL_TEXT_BEGIN" + new string('x', 7000) + "HISTORICAL_TEXT_END";
            var largeTurn = new AiTurn("处理材料", "已阅读") { SentAnnotations = [new AiEntry { Files = [
                new("long.txt", "text/plain", Encode(longText)), new("diagram.png", "image/png", "SECRET_IMAGE_BYTES") ] }] };
            using var compactHandler = new AiSequenceHandler((_, body) => Complete(IsSummary(body) ? "已总结附件及来源。" : "继续。"));
            using var compactConnection = new AiConnection(compactHandler);
            AiContextMemory? memory = null;
            await new AiResponses(compactConnection).RunAsync("fixture", [new AiEntry { Question = "继续" }], [largeTurn], [], _ => { }, _ => { }, CancellationToken.None,
                compactThreshold: 4000, compacted: result => memory = result);
            var summaries = compactHandler.Inputs.Where(IsSummary).ToArray();
            var summarySource = string.Concat(summaries.Select(body => body["input"]!.ToJsonString()));
            var normal = compactHandler.Inputs.Last(body => !IsSummary(body))["input"]!.ToJsonString();
            Check(memory is not null && summaries.Length >= 1 && summarySource.Contains("HISTORICAL_TEXT_BEGIN") && summarySource.Contains("HISTORICAL_TEXT_END") && summarySource.Contains("diagram.png") &&
                !summarySource.Contains("SECRET_IMAGE_BYTES"), "Compaction omitted historical text or embedded media bytes in summary input");
            Check(!normal.Contains("HISTORICAL_TEXT_BEGIN") && !normal.Contains("SECRET_IMAGE_BYTES"), "Compacted attachment was replayed in full");

            var changed = largeTurn with { SentAnnotations = [new AiEntry { Files = [new("long.txt", "text/plain", Encode("CHANGED_TEXT_BEGIN" + new string('z', 7000))) ] }] };
            using var changedHandler = new AiSequenceHandler((_, body) => Complete(IsSummary(body) ? "新摘要。" : "继续。"));
            using var changedConnection = new AiConnection(changedHandler);
            await new AiResponses(changedConnection).RunAsync("fixture", [new AiEntry { Question = "继续" }], [changed], [], _ => { }, _ => { }, CancellationToken.None,
                compactThreshold: 4000, memory: memory);
            Check(changedHandler.Inputs.Any(IsSummary) && changedHandler.Inputs.Where(IsSummary).Any(body => body["input"]!.ToJsonString().Contains("CHANGED_TEXT_BEGIN")),
                "Changed historical attachment reused an incompatible summary fingerprint");

            var missing = new AiTurn("上传", "回答") { SentAnnotations = [new AiEntry { Files = [new("missing.txt", "text/plain", "")
                { BlobId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) }] }] };
            using var missingHandler = new AiSequenceHandler((_, _) => Complete("不应发送"));
            using var missingConnection = new AiConnection(missingHandler);
            var rejected = false;
            try { await new AiResponses(missingConnection).RunAsync("fixture", [new AiEntry { Question = "继续" }], [missing], [], _ => { }, _ => { }, CancellationToken.None); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("missing.txt"); }
            Check(rejected && missingHandler.Inputs.Count == 0 && missing.SentAnnotations.Single().Files.Single().BlobId is not null,
                "Missing historical blob was silently lost or the saved turn was altered");
            Console.WriteLine("PASS: historical uploaded attachments, compaction, fingerprint and missing snapshot handling.");
        }
        finally { Directory.Delete(temp, true); }
    }
}
