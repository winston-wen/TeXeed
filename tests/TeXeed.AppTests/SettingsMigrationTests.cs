using System.IO;
using System.Reflection;
using System.Text.Json;
using TeXeed.App;
using TeXeed.Core;

internal static class SettingsMigrationTests
{
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("TEXEED_HOME");
        var store = typeof(MainWindow).Assembly.GetType("TeXeed.App.Services.SettingsStore")!;
        var load = store.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!;
        var save = store.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)!;
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Environment.SetEnvironmentVariable("TEXEED_HOME", directory);
            if (new AppSettings().FollowMode != PreviewFollowMode.Viewport)
                throw new InvalidOperationException("New settings must follow the viewport by default.");
            foreach (var (json, expected) in new[]
            {
                ("{}", PreviewFollowMode.Viewport),
                ("{\"FollowCursor\":false}", PreviewFollowMode.None),
                ("{\"FollowCursor\":true}", PreviewFollowMode.Cursor),
                ("{\"FollowViewport\":false}", PreviewFollowMode.None),
                ("{\"FollowViewport\":true}", PreviewFollowMode.Viewport),
                ("{\"FollowCursor\":true,\"FollowViewport\":false}", PreviewFollowMode.None),
                ("{\"FollowViewport\":true,\"FollowCursor\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":0,\"FollowViewport\":true,\"FollowCursor\":true}", PreviewFollowMode.None),
                ("{\"FollowMode\":1,\"FollowViewport\":false,\"FollowCursor\":false}", PreviewFollowMode.Cursor),
                ("{\"FollowMode\":2,\"FollowViewport\":false,\"FollowCursor\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":-1,\"FollowViewport\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":999,\"FollowCursor\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":2147483648,\"FollowViewport\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":null,\"FollowCursor\":false}", PreviewFollowMode.Viewport),
                ("{\"FollowMode\":\"unknown\",\"FollowCursor\":false}", PreviewFollowMode.Viewport)
            })
            {
                // A bad follow mode must not reset independent, valid preferences.
                File.WriteAllText(path, json == "{}" ? "{\"FontSize\":19}" : json.Insert(1, "\"FontSize\":19,"));
                var settings = (AppSettings)load.Invoke(null, null)!;
                if (settings.FollowMode != expected || settings.FontSize != 19)
                    throw new InvalidOperationException("Preview follow mode migration failed: " + json);
                save.Invoke(null, [settings]);
                var restored = (AppSettings)load.Invoke(null, null)!;
                using var saved = JsonDocument.Parse(File.ReadAllText(path));
                if (restored.FollowMode != expected || restored.FontSize != 19
                    || saved.RootElement.GetProperty(nameof(AppSettings.FollowMode)).GetInt32() != (int)expected
                    || saved.RootElement.TryGetProperty("FollowCursor", out _) || saved.RootElement.TryGetProperty("FollowViewport", out _))
                    throw new InvalidOperationException("Migrated preview follow mode did not round-trip cleanly.");
            }
            foreach (var mode in Enum.GetValues<PreviewFollowMode>())
            {
                save.Invoke(null, [new AppSettings { FollowMode = mode }]);
                if (((AppSettings)load.Invoke(null, null)!).FollowMode != mode)
                    throw new InvalidOperationException($"Preview follow mode {mode} did not round-trip.");
            }
            save.Invoke(null, [new AppSettings { EditorFontFamily = "Times New Roman" }]);
            if (((AppSettings)load.Invoke(null, null)!).EditorFontFamily != "Times New Roman")
                throw new InvalidOperationException("A proportional editor font did not round-trip.");
            Console.WriteLine("PASS: all preview follow modes round-trip, with legacy migration, key priority and invalid-value defaults");
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", previous); }
    }
}
