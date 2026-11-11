using System.Text;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiDirectoryReferences()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static JsonNode Tool(AiAttachmentCatalog catalog, string name, object args) => JsonNode.Parse(catalog.Execute(name, JsonSerializer.Serialize(args)))!;
        var fixture = Path.Combine(Path.GetTempPath(), "texeed-directory-" + Guid.NewGuid().ToString("N"));
        var selected = Path.Combine(fixture, "selected");
        var sub = Path.Combine(selected, "child");
        var outside = Path.Combine(fixture, "outside");
        var volumeRoot = Path.GetPathRoot(fixture)!;
        Check(AiAttachmentCatalog.IsWithinDirectory(volumeRoot, Path.Combine(volumeRoot, "texeed-root-test.txt")), "A drive or share root failed to contain its own child");
        Directory.CreateDirectory(sub); Directory.CreateDirectory(outside);
        var link = Path.Combine(selected, "external-link");
        var linkCreated = false;
        try
        {
            var target = Path.Combine(selected, "document.txt");
            File.WriteAllText(target, "before😀end", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sub, "nested.txt"), "nested", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET", new UTF8Encoding(false));
            for (var i = 0; i < 31; i++) File.WriteAllText(Path.Combine(selected, $"file-{i:00}.txt"), i.ToString());
            try { Directory.CreateSymbolicLink(link, outside); linkCreated = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }

            var reference = new AiAttachedFile("selected/", "application/x-directory", "") { ReferenceKind = "directory", ReferencePath = selected };
            var entry = new AiEntry { Id = "test-directory", Files = [reference] };
            var catalog = new AiAttachmentCatalog([], [entry]);
            var initial = Tool(catalog, "list_attachments", new { offset = 0, limit = 1 });
            var root = (string)initial["items"]![0]!["handle"]!;
            Check(catalog.Manifest.Contains("已选目录首层") && !catalog.Manifest.Contains("before😀end"), "Directory manifest should list names, not file contents");
            var children = new List<JsonNode>(); var offset = 0;
            do
            {
                var page = Tool(catalog, "list_attachment_directory", new { handle = root, offset, limit = 7 });
                Check(page["entries"] is JsonArray, "Listing failed: " + page);
                children.AddRange(page["entries"]!.AsArray().Select(child => child!.DeepClone()));
                offset = page["next_offset"] is null ? -1 : (int)page["next_offset"]!;
            } while (offset >= 0);
            Check(children.Count == 33 + (linkCreated ? 1 : 0), "Directory pagination missed entries");
            var fileHandle = (string)children.Single(child => (string?)child["name"] == "document.txt")["handle"]!;
            var dirHandle = (string)children.Single(child => (string?)child["name"] == "child")["handle"]!;
            var nested = Tool(catalog, "list_attachment_directory", new { handle = dirHandle, offset = 0, limit = 10 });
            Check((string?)nested["entries"]![0]!["name"] == "nested.txt", "Nested directory could not be listed");
            if (linkCreated) Check((string?)children.Single(child => (string?)child["name"] == "external-link")["error"] == "reparse_point", "Reparse point was presented as readable");
            var first = Tool(catalog, "read_attachment", new { handle = fileHandle, offset = 0, max_chars = 7 });
            Check((string?)first["content"] == "before" && (int?)first["next_offset"] == 6, "Read split a surrogate pair");
            var second = Tool(catalog, "read_attachment", new { handle = fileHandle, offset = 6, max_chars = 20 });
            Check((string?)second["content"] == "😀end" && second["next_offset"] is null, "Read did not resume correctly");

            File.WriteAllText(target, "changed after selection", new UTF8Encoding(false));
            var reopened = new AiAttachmentCatalog([new AiTurn("q", "a") { SentAnnotations = [entry.Snapshot()] }], []);
            var changed = Tool(reopened, "read_attachment", new { handle = fileHandle, offset = 0, max_chars = 100 });
            Check((string?)changed["content"] == "changed after selection", "A saved reference did not read the current file or its handle changed");
            var forged = root + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes("../outside/secret.txt")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Check((string?)Tool(catalog, "read_attachment", new { handle = forged, offset = 0, max_chars = 30 })["error"] == "unknown_handle", "Traversal escaped the selected directory");
            File.WriteAllBytes(Path.Combine(selected, "binary.dat"), [0xff, 0xfe]);
            var search = Tool(catalog, "search_attachments", new { query = "nested", offset = 0, limit = 10 });
            Check(!(bool)search["search_complete"]! && (int)search["unsupported_files"]! >= 1 && (int)search["total_matches"]! == 1 &&
                (string?)search["matches"]![0]!["name"] == "nested.txt", "Search did not recurse or failed to report uncovered non-text files");

            const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aQAAAABJRU5ErkJggg==";
            File.WriteAllBytes(Path.Combine(selected, "image.png"), Convert.FromBase64String(image));
            File.WriteAllBytes(Path.Combine(selected, "source.pdf"), Encoding.ASCII.GetBytes("%PDF-1.4\nfixture"));
            using (var large = File.Create(Path.Combine(selected, "large.png"))) { large.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); large.SetLength(8 * 1024 * 1024 + 1); }
            var mediaChildren = new List<JsonNode>(); offset = 0;
            do
            {
                var page = Tool(catalog, "list_attachment_directory", new { handle = root, offset, limit = 30 });
                mediaChildren.AddRange(page["entries"]!.AsArray().Select(child => child!.DeepClone()));
                offset = page["next_offset"] is null ? -1 : (int)page["next_offset"]!;
            } while (offset >= 0);
            string MediaHandle(string name) => (string)mediaChildren.Single(child => (string?)child["name"] == name)["handle"]!;
            var png = Tool(catalog, "read_attachment_media", new { handle = MediaHandle("image.png") });
            Check((string?)png["mime_type"] == "image/png" && (string?)png["base64"] == image && (bool)png["live_reference"]!, "Referenced image was not returned to the multimodal caller");
            var pdf = Tool(catalog, "read_attachment_media", new { handle = MediaHandle("source.pdf") });
            Check((string?)pdf["mime_type"] == "application/pdf", "Referenced PDF was not recognized");
            Check((int)Tool(catalog, "read_attachment_media", new { handle = MediaHandle("large.png") })["bytes"]! == 8 * 1024 * 1024 + 1, "Media read retained the former fixed size limit");
            Check((string?)Tool(catalog, "read_attachment_media", new { handle = forged })["error"] == "unknown_handle", "Media read escaped the selected directory");
            var oldImage = new AiEntry { Id = "legacy-image", Files = [new AiAttachedFile("old.png", "image/png", image)] };
            var oldCatalog = new AiAttachmentCatalog([], [oldImage]);
            var oldHandle = (string)Tool(oldCatalog, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            Check((string?)Tool(oldCatalog, "read_attachment_media", new { handle = oldHandle })["base64"] == image, "Old media snapshot became unreadable");
            var imageBlob = AiAttachmentBlobStore.Store(Path.Combine(selected, "image.png"));
            var blobImage = new AiEntry { Id = "blob-image", Files = [new AiAttachedFile("image.png", "image/png", "") { BlobId = imageBlob.Id }] };
            var blobImageCatalog = new AiAttachmentCatalog([], [blobImage]);
            var blobImageHandle = (string)Tool(blobImageCatalog, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            Check((string?)Tool(blobImageCatalog, "read_attachment_media", new { handle = blobImageHandle })["base64"] == image, "Stored media snapshot was not read");

            var fileEntry = new AiEntry { Id = "test-file", Files = [new AiAttachedFile("document.txt", "text/plain", "") { ReferenceKind = "file", ReferencePath = target }] };
            var fileCatalog = new AiAttachmentCatalog([], [fileEntry]);
            var singleHandle = (string)Tool(fileCatalog, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            Check((string?)Tool(fileCatalog, "read_attachment", new { handle = singleHandle, offset = 0, max_chars = 100 })["content"] == "changed after selection", "Standalone file reference could not be read");
            Check(fileCatalog.CurrentFileMentions(new AiContextBudget(null), CancellationToken.None).Contains("changed after selection"), "Small current file was not included");
            Check(fileCatalog.CurrentFileMentions(new AiContextBudget(null, 100), CancellationToken.None, 45).Contains("estimated_total_budget"), "Current question did not reserve inline budget");
            var oldFileCatalog = new AiAttachmentCatalog([new AiTurn("q", "a") { SentAnnotations = [fileEntry.Snapshot()] }], []);
            Check(oldFileCatalog.CurrentFileMentions(new AiContextBudget(null), CancellationToken.None) == "", "Historical file reference was re-inlined");
            var blob = AiAttachmentBlobStore.Store(target);
            var blobEntry = new AiEntry { Id = "blob-text", Files = [new AiAttachedFile("document.txt", "text/plain", "") { BlobId = blob.Id }] };
            var blobCatalog = new AiAttachmentCatalog([], [blobEntry]);
            var blobHandle = (string)Tool(blobCatalog, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            Check((string?)Tool(blobCatalog, "read_attachment", new { handle = blobHandle, offset = 0, max_chars = 100 })["content"] == "changed after selection", "Stored text snapshot could not be paged");
            Check((int)Tool(blobCatalog, "search_attachments", new { query = "selection", offset = 0, limit = 10 })["total_matches"]! == 1, "Stored text snapshot was not searched");
            var reloadedBlob = new AiAttachmentCatalog([new AiTurn("q", "a") { SentAnnotations = [blobEntry.Snapshot()] }], []);
            Check((string?)Tool(reloadedBlob, "read_attachment", new { handle = blobHandle, offset = 0, max_chars = 100 })["content"] == "changed after selection", "Stored snapshot handle changed after the turn");
            var absentEntry = new AiEntry { Id = "absent", Files = [new AiAttachedFile("missing.txt", "text/plain", "") { BlobId = new string('0', 64) }] };
            var absentCatalog = new AiAttachmentCatalog([], [absentEntry]);
            var absentHandle = (string)Tool(absentCatalog, "list_attachments", new { offset = 0, limit = 1 })["items"]![0]!["handle"]!;
            Check((string?)Tool(absentCatalog, "read_attachment", new { handle = absentHandle, offset = 0, max_chars = 100 })["error"] == "snapshot_unavailable", "Missing snapshot was silently treated as empty");
            File.WriteAllText(target, new string('x', 256 * 1024 + 1), new UTF8Encoding(false));
            Check(fileCatalog.CurrentFileMentions(new AiContextBudget(null), CancellationToken.None).Contains("too_large_for_inline"), "Large file was inlined instead of reported for on-demand reading");
        }
        finally
        {
            if (linkCreated) Directory.Delete(link);
            Directory.Delete(fixture, true);
        }
    }
}
