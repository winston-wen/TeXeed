using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace TeXeed.App.Services;

public sealed record ThemeDefinition(string Id, string Name, bool IsDark, Dictionary<string, string> Colors);

public static class ThemeManager
{
    public const string DefaultId = "ristretto";
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Themes");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly ThemeDefinition Default = ReadEmbedded(DefaultId);
    public static ThemeDefinition Current { get; private set; } = Default;
    public static event EventHandler? Changed;

    private static ThemeDefinition ReadEmbedded(string id)
    {
        using var stream = typeof(ThemeManager).Assembly.GetManifestResourceStream($"TeXeed.App.Themes.{id}.json")!;
        return JsonSerializer.Deserialize<ThemeDefinition>(stream, JsonOptions)!;
    }

    public static ThemeDefinition Load(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-z0-9-]{1,80}$")) throw new InvalidDataException("主题 ID 无效。");
        var path = Path.Combine(DirectoryPath, id + ".json");
        var theme = File.Exists(path) ? JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(path), JsonOptions)
            : id is DefaultId or "github-light-default" ? ReadEmbedded(id) : throw new FileNotFoundException("找不到主题文件。", path);
        if (theme is null || theme.Id != id || string.IsNullOrWhiteSpace(theme.Name) || theme.Colors is null)
            throw new InvalidDataException("主题缺少有效的 id、name 或 colors。");
        var colors = new Dictionary<string, string>(Default.Colors);
        foreach (var pair in theme.Colors)
        {
            if (!colors.ContainsKey(pair.Key) || !Regex.IsMatch(pair.Value ?? "", "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
                throw new InvalidDataException($"主题颜色无效：{pair.Key}。请使用 #RRGGBB 或 #AARRGGBB。");
            colors[pair.Key] = pair.Value!;
        }
        return theme with { Colors = colors };
    }

    public static IReadOnlyList<ThemeDefinition> List()
    {
        var ids = new[] { DefaultId, "github-light-default" }.Concat(Directory.Exists(DirectoryPath)
            ? Directory.EnumerateFiles(DirectoryPath, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>() : []).Distinct();
        var themes = new List<ThemeDefinition>();
        foreach (var id in ids)
            try { themes.Add(Load(id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { if (id is DefaultId or "github-light-default") themes.Add(ReadEmbedded(id)); }
        return themes;
    }

    public static SolidColorBrush Brush(string key)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Current.Colors[key]));
        brush.Freeze(); return brush;
    }

    public static void Apply(string id)
    {
        var theme = Load(id); // Validate the complete file before touching the live UI.
        ApplyDefinition(theme);
    }

    public static void UseDefault() => ApplyDefinition(Default);

    private static void ApplyDefinition(ThemeDefinition theme)
    {
        Current = theme;
        var resources = Application.Current.Resources;
        foreach (var key in theme.Colors.Keys) resources[key] = Brush(key);
        foreach (var (key, token) in new (ResourceKey, string)[]
        {
            (SystemColors.WindowBrushKey, "EditorBackground"), (SystemColors.WindowTextBrushKey, "Ink"),
            (SystemColors.ControlBrushKey, "PanelBackground"), (SystemColors.ControlTextBrushKey, "Ink"),
            (SystemColors.GrayTextBrushKey, "Disabled"), (SystemColors.HighlightBrushKey, "Selection"),
            (SystemColors.HighlightTextBrushKey, "Ink"), (SystemColors.InactiveSelectionHighlightBrushKey, "PanelBackground"),
            (SystemColors.InactiveSelectionHighlightTextBrushKey, "Ink"), (SystemColors.MenuBrushKey, "EditorBackground"),
            (SystemColors.MenuTextBrushKey, "Ink")
        }) resources[key] = Brush(token);
        resources[SystemParameters.VerticalScrollBarWidthKey] = 14d;
        resources[SystemParameters.HorizontalScrollBarHeightKey] = 12d;
        // WPF Track uses half these metrics as its minimum thumb length. Setting
        // Thumb.MinHeight instead disagrees with Track's drag geometry and clips it.
        resources[SystemParameters.VerticalScrollBarButtonHeightKey] = 48d;
        resources[SystemParameters.HorizontalScrollBarButtonWidthKey] = 48d;
        foreach (Window window in Application.Current.Windows) WindowTheme.Apply(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
