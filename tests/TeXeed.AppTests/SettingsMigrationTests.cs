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
            File.WriteAllText(path, "{\"EditorFontFamily\":\"Sarasa Mono Slab SC\"}");
            var legacy = (AppSettings)load.Invoke(null, null)!;
            if (!legacy.InvertPdfColors || !legacy.AutoCropPdf || legacy.EditorFontFamily != "Sarasa Mono Slab SC")
                throw new InvalidOperationException("PDF inversion default discarded the existing font");
            save.Invoke(null, [legacy with { InvertPdfColors = false, AutoCropPdf = false }]);
            var restoredColors = (AppSettings)load.Invoke(null, null)!;
            if (restoredColors.InvertPdfColors || restoredColors.AutoCropPdf)
                throw new InvalidOperationException("Original PDF color preference did not persist");
            File.WriteAllText(path, "{\"FontSize\":19}");
            if (!((AppSettings)load.Invoke(null, null)!).WordWrap)
                throw new InvalidOperationException("Existing settings without a wrap preference must enable soft wrapping");
            foreach (var wrap in new[] { true, false })
            {
                save.Invoke(null, [new AppSettings { WordWrap = wrap }]);
                if (((AppSettings)load.Invoke(null, null)!).WordWrap != wrap)
                    throw new InvalidOperationException("Word-wrap preference did not round-trip");
            }
            File.WriteAllText(path, "{\"FontSize\":19}");
            var previewDefaults = (AppSettings)load.Invoke(null, null)!;
            if (!previewDefaults.CompressPdfWhitespace || previewDefaults.AutoWidthMaximumCm != 30)
                throw new InvalidOperationException("Legacy preview settings must default to compression and a 30 cm width cap");
            save.Invoke(null, [previewDefaults with { CompressPdfWhitespace = false, AutoWidthMaximumCm = 12.5 }]);
            var physicalSettings = (AppSettings)load.Invoke(null, null)!;
            if (physicalSettings.CompressPdfWhitespace || physicalSettings.AutoWidthMaximumCm != 12.5)
                throw new InvalidOperationException("Independent compression and physical width preferences did not round-trip");
            foreach (var invalid in new[] { "0", "-1", "201", "1e100" })
            {
                File.WriteAllText(path, "{\"AutoWidthMaximumCm\":" + invalid + ",\"FontSize\":19}");
                var normalized = (AppSettings)load.Invoke(null, null)!;
                if (normalized.AutoWidthMaximumCm != 30 || normalized.FontSize != 19)
                    throw new InvalidOperationException("Invalid physical width reset unrelated preferences");
            }
            Console.WriteLine("PASS: all preview follow modes round-trip, with legacy migration, key priority and invalid-value defaults");
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", previous); }
    }
}
