using System.Text.Json;
using System.Text.Json.Serialization;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed class OldAttachmentEntry
    {
        public List<OldAttachmentFile> Files { get; set; } = [];
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }
    private sealed class OldAttachmentFile
    {
        public string Name { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string Base64 { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FolderPath { get; set; }
    }

    private static void CheckAiAttachmentCompatibility()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static string ThroughOld(AiEntry entry, Action<OldAttachmentEntry>? change = null)
        {
            var old = JsonSerializer.Deserialize<OldAttachmentEntry>(JsonSerializer.Serialize(entry))!;
            change?.Invoke(old);
            return JsonSerializer.Serialize(old);
        }
        var blob = new AiAttachedFile("upload.txt", "text/plain", "") { BlobId = new string('A', 64) };
        var reference = new AiAttachedFile(@"C:\work\notes.tex", "application/vnd.texeed.file-reference", "")
            { ReferenceKind = "file", ReferencePath = @"C:\work\notes.tex" };
        var directory = new AiAttachedFile(@"C:\work\", "application/vnd.texeed.directory-reference", "")
            { FolderPath = @"C:\work\", ReferenceKind = "directory", ReferencePath = @"C:\work\" };
        var legacy = new AiAttachedFile("old.txt", "text/plain", "b2xk");
        var source = new AiEntry { Files = [blob, reference, directory, legacy] };
        var modern = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(source))!;
        Check(modern.Files.SequenceEqual(source.Files) && !modern.HasUnresolvedAttachmentMetadata && source.AdditionalData is null,
            "Modern round-trip changed file metadata or mutated the original extensions");
        using var future = JsonDocument.Parse("{\"keep\":true}");
        var extended = new AiEntry { Files = [blob], AdditionalData = new() { ["future"] = future.RootElement.Clone() } };
        var concurrent = Enumerable.Range(0, 24).Select(_ => Task.Run(() => JsonSerializer.Serialize(extended))).ToArray();
        Task.WaitAll(concurrent);
        Check(concurrent.All(task => JsonSerializer.Deserialize<AiEntry>(task.Result) is { } item && item.Files.Single() == blob && item.AdditionalData?.ContainsKey("future") == true) &&
            extended.AdditionalData is { Count: 1 } && extended.AdditionalData.ContainsKey("future"),
            "Concurrent serialization lost file metadata or changed the original extensions");
        var invalidMetadata = new AiEntry { Files = [blob], AdditionalData = new() { [AiAttachmentMetadataCompatibility.Key] = JsonSerializer.SerializeToElement("unknown") } };
        var rejected = false;
        try { _ = JsonSerializer.Serialize(invalidMetadata); } catch (JsonException) { rejected = true; }
        Check(rejected && invalidMetadata.AdditionalData?.Count == 1 && invalidMetadata.AdditionalData[AiAttachmentMetadataCompatibility.Key].GetString() == "unknown",
            "Failed serialization modified unknown extension data");
        var restored = JsonSerializer.Deserialize<AiEntry>(ThroughOld(source, old => old.Files.Reverse()))!;
        Check(restored.Files.SequenceEqual(source.Files.AsEnumerable().Reverse()) && !restored.HasUnresolvedAttachmentMetadata,
            "Old-version load/save lost newer file metadata after reordering");
        var changed = JsonSerializer.Deserialize<AiEntry>(ThroughOld(source, old => old.Files[0].Name = "replaced.txt"))!;
        Check(changed.HasUnresolvedAttachmentMetadata && changed.Files[0].BlobId is null && changed.Files[1] == reference && changed.Files[2] == directory,
            "Edited legacy file was guessed or other metadata was lost");
        var changedAgain = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(changed))!;
        Check(changedAgain.HasUnresolvedAttachmentMetadata && changedAgain.Files[0].BlobId is null,
            "Unmatched metadata was lost or assigned on a later save");
        changed.AnswerAnchor = new("turn", "segment", 0, 1, "x", "revision");
        var justReference = AiPanel.SnapshotSelectedParts(changed, true, false, 1);
        Check(!justReference.HasUnresolvedAttachmentMetadata && justReference.Files.Count == 0 && changed.HasUnresolvedAttachmentMetadata,
            "Unselected attachment metadata was sent or removed from the draft");

        var duplicate = new AiEntry { Files = [blob, blob with { BlobId = new string('B', 64) }] };
        var duplicateModern = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(duplicate))!;
        Check(duplicateModern.Files.SequenceEqual(duplicate.Files) && !duplicateModern.HasUnresolvedAttachmentMetadata,
            "Modern files sharing old-visible fields became ambiguous");
        var ambiguous = JsonSerializer.Deserialize<AiEntry>(ThroughOld(duplicate))!;
        Check(ambiguous.HasUnresolvedAttachmentMetadata && ambiguous.Files.All(file => file.BlobId is null),
            "Ambiguous old-version files were assigned the wrong snapshot");
        var ambiguousAgain = JsonSerializer.Deserialize<AiEntry>(JsonSerializer.Serialize(ambiguous))!;
        Check(ambiguousAgain.HasUnresolvedAttachmentMetadata && ambiguousAgain.Files.All(file => file.BlobId is null),
            "Ambiguous compatibility metadata was lost after another save");
    }
}
