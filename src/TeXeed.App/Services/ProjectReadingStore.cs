using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App.Services;

internal sealed record ProjectReadingState
{
    public int Version { get; init; } = 1;
    public string[] LeftTabs { get; init; } = [];
    public string[] RightTabs { get; init; } = [];
    public string? LeftActive { get; init; }
    public string? RightActive { get; init; }
    public TabSide FocusedSide { get; init; }
    public Dictionary<string, EditorReadingPosition> Editors { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public PdfReadingPosition? Preview { get; init; }
    public double LeftWidth { get; init; } = 1;
    public double RightWidth { get; init; } = 1;
    public bool SidebarExpanded { get; init; } = true;
    public double SidebarWidth { get; init; } = 294;
    public string Sidebar { get; init; } = "outline";
    public double WindowLeft { get; init; }
    public double WindowTop { get; init; }
    public double WindowWidth { get; init; }
    public double WindowHeight { get; init; }
    public bool Maximized { get; init; }
    public bool Fullscreen { get; init; }
}

internal static class ProjectReadingStore
{
    private static string PathFor(string project)
    {
        var home = Environment.GetEnvironmentVariable("TEXEED_HOME") is { Length: > 0 } directory
            ? Path.GetFullPath(directory) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant())));
        return Path.Combine(home, "reading-history", key + ".json");
    }
    public static ProjectReadingState? Load(string project)
    {
        try
        {
            var state = JsonSerializer.Deserialize<ProjectReadingState>(File.ReadAllText(PathFor(project)));
            return state?.Version == 1 ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }
    public static void Save(string project, ProjectReadingState state)
    {
        var path = PathFor(project); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        AtomicFile.Replace(temporary, path);
    }
}
