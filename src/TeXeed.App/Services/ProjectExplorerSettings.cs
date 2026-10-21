using System.IO;
using System.Text.Json;

namespace TeXeed.App.Services;

internal static class ProjectExplorerSettings
{
    private sealed record State(string[] PriorityFiles);
    internal static string[] Load(string root)
    {
        try
        {
            var path = SettingsPath(root);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return [];
            return (JsonSerializer.Deserialize<State>(File.ReadAllText(path))?.PriorityFiles ?? [])
                .Select(relative => Resolve(root, relative)).Where(path => path is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return []; }
    }
    internal static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    internal static void Save(string root, IEnumerable<string> paths)
    {
        var path = SettingsPath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State(paths.Select(file => Path.GetRelativePath(root, file)).ToArray()), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string SettingsPath(string root)
    {
        var directory = Path.Combine(root, ".texeed"); var path = Path.Combine(directory, "explorer.json");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("项目文件列表设置不能位于目录联接或符号链接中。");
        return path;
    }
}
