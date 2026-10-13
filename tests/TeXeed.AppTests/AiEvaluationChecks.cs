using System.IO;
using System.Text.Json;

// No WPF or network dependencies: the live harness and offline checks share
// this acceptance rule so an unrelated successful build cannot pass a case.
internal static class AiEvaluationChecks
{
    internal static bool MatchesExpectedEntry(string projectPath, string? arguments, IReadOnlyList<string> expectedEntries, out string actualEntry)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var fallback = Path.GetFileName(projectPath);
        actualEntry = "";
        try
        {
            using var json = JsonDocument.Parse(arguments ?? "{}");
            var entry = json.RootElement.TryGetProperty("entry_file", out var field) ? field.GetString() : null;
            var path = Path.GetFullPath(string.IsNullOrWhiteSpace(entry) ? fallback : entry, root);
            actualEntry = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (actualEntry == ".." || actualEntry.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(actualEntry)) return false;
            var expected = expectedEntries.Count > 0 ? expectedEntries : [fallback];
            return expected.Any(value => string.Equals(path, Path.GetFullPath(value, root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
