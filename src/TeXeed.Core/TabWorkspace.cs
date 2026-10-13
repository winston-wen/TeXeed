namespace TeXeed.Core;

public enum TabSide { Left, Right }
public enum TabCloseRange { This, All, Others, Before, After }

/// <summary>Two optional tab groups. The singleton preview can move but never close.</summary>
public sealed class TabWorkspace
{
    public const string AiSettingsKey = "@texeed:ai-settings";
    public const string AiKey = "@texeed:ai";
    public static bool IsSource(string key) => key != PreviewKey && key != AiKey && key != AiSettingsKey;
    public const string PreviewKey = "@texeed:preview";
    private List<string> _left = [], _right = [PreviewKey];
    private string? _leftActive, _rightActive = PreviewKey;
    public TabSide FocusedSide { get; private set; } = TabSide.Left;
    public IReadOnlyList<string> Tabs(TabSide side) => side == TabSide.Left ? _left : _right;
    public string? Active(TabSide side) => side == TabSide.Left ? _leftActive : _rightActive;
    public string? FocusedTab => Active(FocusedSide);
    public bool Has(TabSide side) => Tabs(side).Count != 0;
    public IEnumerable<string> Sources => _left.Concat(_right).Where(IsSource);
    private List<string> List(TabSide side) => side == TabSide.Left ? _left : _right;
    public TabSide? Owner(string key) => _left.Contains(key, StringComparer.OrdinalIgnoreCase) ? TabSide.Left
        : _right.Contains(key, StringComparer.OrdinalIgnoreCase) ? TabSide.Right : null;
    private void SetActive(TabSide side, string? key) { if (side == TabSide.Left) _leftActive = key; else _rightActive = key; }
    public void Reset(string main)
    { _left = [main]; _right = [PreviewKey]; _leftActive = main; _rightActive = PreviewKey; FocusedSide = TabSide.Left; }
    public void Open(string path)
    {
        var side = Owner(path) ?? ((path == AiKey || path == AiSettingsKey) ? TabSide.Right : TabSide.Left);
        if (Owner(path) is null) List(side).Add(path);
        Select(side, path);
    }
    public void Focus(TabSide side) { if (Has(side)) FocusedSide = side; }
    public void Select(TabSide side, string key)
    { if (List(side).Contains(key, StringComparer.OrdinalIgnoreCase)) { SetActive(side, key); FocusedSide = side; } }
    public void Move(string key, TabSide side, int index = int.MaxValue)
    {
        if (Owner(key) is not { } from) return;
        var source = List(from); var oldIndex = source.FindIndex(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
        source.RemoveAt(oldIndex);
        if (string.Equals(Active(from), key, StringComparison.OrdinalIgnoreCase)) SetActive(from, source.Count == 0 ? null : source[Math.Min(oldIndex, source.Count - 1)]);
        var target = List(side); target.Insert(Math.Clamp(index, 0, target.Count), key);
        SetActive(side, key); FocusedSide = side;
    }
    public void Split(string key, TabSide side)
    {
        if (Owner(key) is null) return;
        if (!Has(TabSide.Left) || !Has(TabSide.Right))
        {
            if (Owner(key) == side)
            { (_left, _right) = (_right, _left); (_leftActive, _rightActive) = (_rightActive, _leftActive); }
        }
        Move(key, side);
    }
    public IReadOnlyList<string> Close(string key, TabCloseRange range)
    {
        if (Owner(key) is not { } side) return [];
        var tabs = List(side); var index = tabs.FindIndex(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
        var selected = range switch
        {
            TabCloseRange.All => tabs.ToArray(), TabCloseRange.Others => tabs.Where(item => !string.Equals(item, key, StringComparison.OrdinalIgnoreCase)).ToArray(),
            TabCloseRange.Before => tabs.Take(index).ToArray(), TabCloseRange.After => tabs.Skip(index + 1).ToArray(), _ => new[] { key }
        };
        var close = selected.Where(item => item != PreviewKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeIndex = tabs.FindIndex(item => string.Equals(item, Active(side), StringComparison.OrdinalIgnoreCase));
        tabs.RemoveAll(item => close.Contains(item));
        if (tabs.Count == 0) SetActive(side, null);
        else if (Active(side) is null || close.Contains(Active(side)!)) SetActive(side, tabs[Math.Clamp(activeIndex, 0, tabs.Count - 1)]);
        if (!Has(FocusedSide)) FocusedSide = side == TabSide.Left ? TabSide.Right : TabSide.Left;
        return close.ToArray();
    }
}
