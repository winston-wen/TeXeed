using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeXeed.App.AI;

// Older versions preserve unknown AiEntry fields but discard unknown file fields.
internal static class AiAttachmentMetadataCompatibility
{
    internal const string Key = "__texeed_attachment_metadata_v1";
    private sealed record Record(string Fingerprint, string? BlobId, string? ReferenceKind, string? ReferencePath);
    private sealed record Envelope(int Version, List<Record> Files);

    internal static Dictionary<string, JsonElement>? Prepare(IReadOnlyList<AiAttachedFile> files, Dictionary<string, JsonElement>? original)
    {
        var fresh = files.Where(HasMetadata).Select(ToRecord).ToArray();
        if (fresh.Length == 0) return original;
        var existing = new List<Record>();
        if (original?.TryGetValue(Key, out var raw) == true)
        {
            if (!TryRead(raw, out var envelope)) throw new JsonException("无法保存附件：已有兼容数据无法识别，已保留原数据。");
            existing.AddRange(envelope.Files);
        }
        foreach (var group in fresh.GroupBy(record => record))
        {
            var present = existing.Count(record => record == group.Key);
            for (var i = present; i < group.Count(); i++) existing.Add(group.Key);
        }
        var result = original is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(original);
        result[Key] = JsonSerializer.SerializeToElement(new Envelope(1, existing));
        return result;
    }

    internal static void Restore(AiEntry entry)
    {
        if (entry.AdditionalData?.TryGetValue(Key, out var raw) != true || !TryRead(raw, out var envelope)) return;
        var pending = new List<Record>(envelope.Files);
        foreach (var file in entry.Files.Where(HasMetadata)) pending.Remove(ToRecord(file));
        var candidates = entry.Files.Select((file, index) => (file, index))
            .Where(item => !HasMetadata(item.file)).GroupBy(item => Fingerprint(item.file));
        foreach (var group in candidates)
        {
            var matches = pending.Where(record => record.Fingerprint == group.Key).ToArray();
            var positions = group.ToArray();
            if (matches.Length == 0 || matches.Length != positions.Length || matches.Distinct().Count() != 1) continue;
            var record = matches[0];
            foreach (var item in positions)
                entry.Files[item.index] = item.file with { BlobId = record.BlobId, ReferenceKind = record.ReferenceKind, ReferencePath = record.ReferencePath };
            foreach (var match in matches) pending.Remove(match);
        }
        if (pending.Count == 0)
        {
            entry.AdditionalData.Remove(Key);
            if (entry.AdditionalData.Count == 0) entry.AdditionalData = null;
        }
        else entry.AdditionalData[Key] = JsonSerializer.SerializeToElement(new Envelope(1, pending));
    }

    private static bool TryRead(JsonElement element, out Envelope envelope)
    {
        envelope = null!;
        try
        {
            var parsed = element.Deserialize<Envelope>();
            if (parsed is null || parsed.Version != 1 || parsed.Files is null || parsed.Files.Any(record => record is null || string.IsNullOrEmpty(record.Fingerprint))) return false;
            envelope = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
    private static bool HasMetadata(AiAttachedFile file) => file.BlobId is not null || file.ReferenceKind is not null || file.ReferencePath is not null;
    private static Record ToRecord(AiAttachedFile file) => new(Fingerprint(file), file.BlobId, file.ReferenceKind, file.ReferencePath);

    private static string Fingerprint(AiAttachedFile file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16384];
        foreach (var part in new[] { file.Name, file.MimeType, file.Base64, file.FolderPath })
        {
            hash.AppendData(BitConverter.GetBytes(part?.Length ?? -1));
            if (part is null) continue;
            var remaining = part.AsSpan(); var encoder = Encoding.UTF8.GetEncoder();
            while (!remaining.IsEmpty)
            {
                encoder.Convert(remaining, buffer, true, out var chars, out var bytes, out _);
                hash.AppendData(buffer.AsSpan(0, bytes)); remaining = remaining[chars..];
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
