using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeXeed.Core;

namespace TeXeed.App.Services;
internal static class SettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("TEXEED_HOME") is { Length: > 0 } directory
            ? Path.GetFullPath(directory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed"), "settings.json");
    public static AppSettings Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject json) return new();
            if (json[nameof(AppSettings.ThemeId)] is not JsonValue theme || !theme.TryGetValue<string>(out var themeId) || string.IsNullOrWhiteSpace(themeId))
                json[nameof(AppSettings.ThemeId)] = ThemeManager.DefaultId;
            // Normalize before deserializing so an invalid mode cannot discard unrelated settings.
            json[nameof(AppSettings.FollowMode)] = (int)ReadFollowMode(json);
            var settings = json.Deserialize<AppSettings>() ?? new();
            return settings with
            {
                DebounceMs = Math.Clamp(settings.DebounceMs, 400, 10000), FontSize = Math.Clamp(settings.FontSize, 10, 30),
                EditorFontFamily = string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? new AppSettings().EditorFontFamily : settings.EditorFontFamily.Trim(),
                AutoWidthMaximumCm = double.IsFinite(settings.AutoWidthMaximumCm) && settings.AutoWidthMaximumCm is >= 1 and <= 200 ? settings.AutoWidthMaximumCm : 30,
                RecentProjects = settings.RecentProjects ?? []
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    private static PreviewFollowMode ReadFollowMode(JsonObject json)
    {
        if (json.TryGetPropertyValue(nameof(AppSettings.FollowMode), out var mode))
            return mode is JsonValue value && value.TryGetValue<int>(out var number)
                && Enum.IsDefined((PreviewFollowMode)number) ? (PreviewFollowMode)number : PreviewFollowMode.Viewport;
        if (json["FollowViewport"] is JsonValue viewport && viewport.TryGetValue<bool>(out var followViewport))
            return followViewport ? PreviewFollowMode.Viewport : PreviewFollowMode.None;
        if (json["FollowCursor"] is JsonValue cursor && cursor.TryGetValue<bool>(out var followCursor))
            return followCursor ? PreviewFollowMode.Cursor : PreviewFollowMode.None;
        return PreviewFollowMode.Viewport;
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        AtomicFile.Replace(temporary, SettingsPath);
    }
}
