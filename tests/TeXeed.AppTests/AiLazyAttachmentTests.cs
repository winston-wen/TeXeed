using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    // Uses saved attachment bytes rather than depending on the user's files or a live model.
    private static async Task CheckAiLazyAttachmentsAsync()
    {
        static void CheckLazy(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }
        new AiCredentials { ApiKey = "fixture-key-never-send", UseApiKey = true }.Save();

        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aQAAAABJRU5ErkJggg==";
        var files = Enumerable.Range(0, 25).Select(i => new AiAttachedFile(
            $"source-{i:00}.md", "text/plain",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"CONTENT_SENTINEL_{i:00}\n" + new string('文', 60000) + $"\nEND_SENTINEL_{i:00}")))
            { FolderPath = @"C:\fixture\directory\" }).ToList();
        files.AddRange(Enumerable.Range(0, 6).Select(i => new AiAttachedFile($"image-{i:00}.png", "image/png", png)
            { FolderPath = @"C:\fixture\directory\" }));
        var entry = new AiEntry { Id = "fixture-entry", EntryKind = "attachment", Files = files };
        var snapshot = entry.Snapshot();
        var saved = JsonSerializer.Serialize(snapshot);
        var roundTrip = JsonSerializer.Deserialize<AiEntry>(saved)!;
        CheckLazy(roundTrip.Files.Count == 31 && roundTrip.Files.Select(f => f.Base64).SequenceEqual(files.Select(f => f.Base64)),
            "Saving the draft changed attachment snapshots");
        CheckAiAttachmentCatalog(roundTrip);

        static string Complete(JsonNode output) => "data: " + new JsonObject
        {
            ["type"] = "response.completed", ["response"] = new JsonObject { ["output"] = new JsonArray(output) }
        }.ToJsonString() + "\n\n";
        var answer = new JsonObject
        {
            ["id"] = "final", ["type"] = "message", ["role"] = "assistant", ["phase"] = "final_answer",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "完成。" })
        };
        using var handler = new AiSequenceHandler((_, _) => Complete(answer.DeepClone()));
        using var connection = new AiConnection(handler);
        await new AiResponses(connection).RunAsync("fixture", [roundTrip], [], [], _ => { }, _ => { }, CancellationToken.None);
        var request = handler.Inputs.Single();
        var wire = request.ToJsonString();
        CheckLazy(!wire.Contains("CONTENT_SENTINEL_") && !wire.Contains("END_SENTINEL_") && wire.Length < 150000,
            "First request eagerly inserted attachment contents into model context");
        CheckLazy(wire.Contains("list_attachments") && wire.Contains("read_attachment") && wire.Contains("search_attachments"),
            "Attachment discovery, reading or search was not exposed to the model");
        CheckLazy(CountJsonValues(request, "type", "input_image") == 6,
            "Moving text to lazy access lost attached images");
        CheckLazy(JsonSerializer.Serialize(roundTrip) == saved, "Sending altered the saved attachment snapshot");

        static JsonObject Call(string name, object args) => new()
        {
            ["id"] = "call-" + name, ["type"] = "function_call", ["name"] = "attachments." + name,
            ["call_id"] = "id-" + name, ["arguments"] = JsonSerializer.Serialize(args)
        };
        using var routedHandler = new AiSequenceHandler((step, body) =>
        {
            if (step == 0) return Complete(Call("list_attachments", new { offset = 0, limit = 1 }));
            var result = body["input"]!.AsArray().Last(item => item?["type"]?.ToString() == "function_call_output")!;
            var output = JsonNode.Parse(result["output"]!.ToString())!;
            if (step == 1)
            {
                CheckLazy((int)output["total"]! == 31, "Model tool call did not reach the conversation attachment catalog");
                return Complete(Call("read_attachment", new { handle = (string)output["items"]![0]!["handle"]!, offset = 0, max_chars = 50 }));
            }
            CheckLazy(output["content"]!.ToString().StartsWith("CONTENT_SENTINEL_00"), "Model tool call did not read the requested snapshot");
            return Complete(answer.DeepClone());
        });
        using var routedConnection = new AiConnection(routedHandler);
        var activities = new List<AiStreamEvent>();
        await new AiResponses(routedConnection).RunAsync("fixture", [roundTrip], [], [], _ => { }, _ => { }, CancellationToken.None, activity: activities.Add);
        CheckLazy(routedHandler.Inputs.Count == 3, "Attachment tool calls failed to continue the response");
        CheckLazy(activities.Any(eventItem => eventItem.Text == "已读取附件片段" && eventItem.Details.Contains("returned_chars")) &&
            activities.All(eventItem => !eventItem.Details.Contains("CONTENT_SENTINEL_00")),
            "Attachment activity must retain read coverage without storing the source text");
    }

    private static void CheckAiAttachmentCatalog(AiEntry entry)
    {
        static void CheckCatalog(bool ok, string message) { if (!ok) throw new Exception(message); }
        static JsonNode Tool(AiAttachmentCatalog catalog, string name, object args) =>
            JsonNode.Parse(catalog.Execute(name, JsonSerializer.Serialize(args)))!;
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var catalog = new AiAttachmentCatalog([], [entry]);
        var handles = new List<string>(); var offset = 0;
        do
        {
            var page = Tool(catalog, "list_attachments", new { offset, limit = 7 });
            CheckCatalog((int)page["total"]! == 31 && (int)page["returned"]! <= 7,
                "Attachment list did not retain all 25 documents and six images");
            handles.AddRange(page["items"]!.AsArray().Select(item => (string)item!["handle"]!));
            offset = page["next_offset"] is null ? -1 : (int)page["next_offset"]!;
        } while (offset >= 0);
        CheckCatalog(handles.Count == 31 && handles.Distinct().Count() == 31, "Listing pages lost or duplicated attachments");
        for (var fileIndex = 0; fileIndex < 25; fileIndex++)
        {
            var content = new StringBuilder(); offset = 0;
            do
            {
                var page = Tool(catalog, "read_attachment", new { handle = handles[fileIndex], offset, max_chars = 4096 });
                CheckCatalog((int)page["offset"]! == offset && (int)page["returned_chars"]! <= 4096,
                    "Read page did not honor its bounded offset and size");
                content.Append((string)page["content"]!);
                offset = page["next_offset"] is null ? -1 : (int)page["next_offset"]!;
            } while (offset >= 0);
            CheckCatalog(content.ToString() == Encoding.UTF8.GetString(Convert.FromBase64String(entry.Files[fileIndex].Base64)),
                "Paged reads failed to reproduce the saved snapshot");
        }
        var found = new List<string>(); offset = 0;
        do
        {
            var page = Tool(catalog, "search_attachments", new { query = "CONTENT_SENTINEL_", offset, limit = 7 });
            CheckCatalog((int)page["total_files"]! == 31 && (int)page["scanned_text_files"]! == 25 &&
                (int)page["unsupported_files"]! == 6 && (int)page["failed_files"]! == 0 && (int)page["total_matches"]! == 25 &&
                !(bool)page["search_complete"]! && (bool)page["text_search_complete"]!,
                "Search coverage omitted documents, images or match totals");
            found.AddRange(page["matches"]!.AsArray().Select(item => (string)item!["handle"]!));
            offset = page["next_offset"] is null ? -1 : (int)page["next_offset"]!;
        } while (offset >= 0);
        CheckCatalog(found.Count == 25 && found.Distinct().Count() == 25, "Search pagination missed or repeated results");
        CheckCatalog((string)Tool(catalog, "read_attachment", new { handle = handles[25], offset = 0, max_chars = 20 })["error"]! == "unsupported_media",
            "An image was incorrectly presented as readable text");
        CheckCatalog((string)Tool(catalog, "read_attachment", new { handle = @"C:\outside.txt", offset = 0, max_chars = 20 })["error"]! == "unknown_handle",
            "A path outside the attachment snapshot was accepted as a handle");

        var old = new AiEntry { Id = "same-entry", Files = [new("same.txt", "text/plain", Encode("old😀end"))] };
        var later = new AiEntry { Id = "same-entry", Files = [new("same.txt", "text/plain", Encode("new branch snapshot"))] };
        var inherited = new AiTurn("old", "answer") { Id = "old-turn", SentAnnotations = [old.Snapshot()] };
        var parentFuture = new AiTurn("future", "answer") { Id = "future-turn", SentAnnotations = [later.Snapshot()] };
        var branch = new AiAttachmentCatalog([inherited], []);
        var branchItems = Tool(branch, "list_attachments", new { offset = 0, limit = 30 })["items"]!.AsArray();
        var oldHandle = (string)branchItems.Single()!["handle"]!;
        var full = new AiAttachmentCatalog([inherited, parentFuture], []);
        var fullItems = Tool(full, "list_attachments", new { offset = 0, limit = 30 })["items"]!.AsArray();
        CheckCatalog(fullItems.Count == 2 && (string)fullItems[0]!["handle"]! == oldHandle &&
            (string)fullItems[1]!["handle"]! != oldHandle, "Duplicate names and entry IDs confused snapshots");
        var laterHandle = (string)fullItems[1]!["handle"]!;
        CheckCatalog((string)Tool(branch, "read_attachment", new { handle = laterHandle, offset = 0, max_chars = 20 })["error"]! == "unknown_handle",
            "A branch read a snapshot added to the parent after the fork");
        var first = Tool(branch, "read_attachment", new { handle = oldHandle, offset = 0, max_chars = 4 });
        CheckCatalog((string)first["content"]! == "old" && (int)first["next_offset"]! == 3,
            "Read split a surrogate pair at a page boundary");
        var second = Tool(branch, "read_attachment", new { handle = oldHandle, offset = 3, max_chars = 20 });
        CheckCatalog((string)second["content"]! == "😀end" && second["next_offset"] is null,
            "Read did not resume after a surrogate boundary");
        CheckCatalog((string)Tool(branch, "read_attachment", new { handle = oldHandle, offset = 4, max_chars = 20 })["error"]! == "invalid_offset",
            "Read accepted an offset inside a surrogate pair");
        var afterTurn = new AiAttachmentCatalog([new AiTurn("q", "a") { SentAnnotations = [old] }], []);
        CheckCatalog((string)Tool(afterTurn, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]! == oldHandle,
            "A handle changed when a current attachment became conversation history");

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "texeed-attachment-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            System.IO.File.WriteAllText(path, "snapshot before change");
            var captured = new AiEntry { Files = [new(path, "text/plain", Convert.ToBase64String(System.IO.File.ReadAllBytes(path)))] };
            var stable = new AiAttachmentCatalog([], [captured]);
            var stableHandle = (string)Tool(stable, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            System.IO.File.WriteAllText(path, "changed on disk");
            CheckCatalog((string)Tool(stable, "read_attachment", new { handle = stableHandle, offset = 0, max_chars = 100 })["content"]! == "snapshot before change",
                "Read used the current disk file instead of the stored attachment snapshot");
        }
        finally { System.IO.File.Delete(path); }

        CheckCatalog((string)JsonNode.Parse(branch.Execute("read_attachment", "{}"))!["error"]! == "invalid_arguments",
            "A missing tool argument escaped as an exception");
        var broken = new AiAttachmentCatalog([], [new AiEntry { Files = [new("bad.txt", "text/plain", "not-base64"),
            new("encoding.txt", "text/plain", Convert.ToBase64String(new byte[] { 0xff, 0xfe })), new("image.png", "image/png", "") ] }]);
        var brokenItems = Tool(broken, "list_attachments", new { offset = 0, limit = 30 })["items"]!.AsArray();
        var badEncoding = (string)brokenItems[1]!["handle"]!;
        CheckCatalog((string)Tool(broken, "read_attachment", new { handle = badEncoding, offset = 0, max_chars = 20 })["error"]! == "unsupported_encoding",
            "Invalid UTF-8 was silently changed during read");
        var coverage = Tool(broken, "search_attachments", new { query = "anything", offset = 0, limit = 10 });
        CheckCatalog((int)coverage["failed_files"]! == 2 && (int)coverage["unsupported_files"]! == 1 && !(bool)coverage["search_complete"]! && !(bool)coverage["text_search_complete"]!,
            "Search claimed complete coverage despite an unreadable snapshot");
    }

    private static int CountJsonValues(JsonNode? node, string key, string value)
    {
        if (node is JsonObject obj)
            return (obj[key]?.ToString() == value ? 1 : 0) + obj.Sum(property => CountJsonValues(property.Value, key, value));
        if (node is JsonArray array) return array.Sum(item => CountJsonValues(item, key, value));
        return 0;
    }
}
