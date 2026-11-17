namespace TeXeed.Core;

public enum TabSide { Left, Right }
public enum TabCloseRange { This, All, Others, Before, After }

/// <summary>Workspace tab groups with a protected primary preview.</summary>
public sealed class TabWorkspace
{
    public const string AiSettingsKey = "@texeed:ai-settings";
    public const string AiKey = "@texeed:ai";
    public const string AiHistoryKey = "@texeed:ai-history";
    private const string AiRelationsPrefix = "@texeed:ai-relations:";
    public static string AiRelationsKey(string id) => AiRelationsPrefix + id;
    public static bool IsAiRelations(string key) => key.StartsWith(AiRelationsPrefix, StringComparison.Ordinal);
    public static string AiRelationsId(string key) => key[AiRelationsPrefix.Length..];
    public static string AiConversationKey(string id) => AiKey + ":" + id;
    public static bool IsAiConversation(string key) => key == AiKey || key.StartsWith(AiKey + ":", StringComparison.Ordinal);
    public const string PreviewKey = "@texeed:preview";
    private const string EntryPreviewPrefix = "@texeed:preview:";
    public static string PreviewKeyFor(string entryPath) => EntryPreviewPrefix + Path.GetFullPath(entryPath);
    public static bool IsPreview(string key) => key == PreviewKey || key.StartsWith(EntryPreviewPrefix, StringComparison.OrdinalIgnoreCase);
    public static string? PreviewEntry(string key) => key.StartsWith(EntryPreviewPrefix, StringComparison.OrdinalIgnoreCase) ? key[EntryPreviewPrefix.Length..] : null;
    public static bool IsSource(string key) => !IsPreview(key) && !IsAiConversation(key) && !IsAiRelations(key) && key != AiSettingsKey && key != AiHistoryKey;
    private List<string> _left = [], _right = [PreviewKey];
    private string? _leftActive, _rightActive = PreviewKey;
    private readonly Dictionary<TabSide, List<string>> _extra = [];
    private readonly Dictionary<TabSide, string?> _extraActive = [];
    private int _nextGroup = 2;
    public IEnumerable<TabSide> ExtraGroups => _extra.Keys;
    public TabSide AddGroup()
    {
        var side = (TabSide)_nextGroup++;
        _extra.Add(side, []); _extraActive.Add(side, null); return side;
    }
    public void RemoveGroup(TabSide side)
    {
        if (!_extra.TryGetValue(side, out var tabs) || tabs.Count != 0) return;
        _extra.Remove(side); _extraActive.Remove(side);
        if (FocusedSide == side) FocusedSide = Has(TabSide.Left) ? TabSide.Left : TabSide.Right;
    }
    public TabSide FocusedSide { get; private set; } = TabSide.Left;
    public IReadOnlyList<string> Tabs(TabSide side) => List(side);
    public string? Active(TabSide side) => side == TabSide.Left ? _leftActive : side == TabSide.Right ? _rightActive : _extraActive.GetValueOrDefault(side);
    public string? FocusedTab => Active(FocusedSide);
    public bool Has(TabSide side) => Tabs(side).Count != 0;
    public IEnumerable<string> Sources => _left.Concat(_right).Concat(_extra.Values.SelectMany(items => items)).Where(IsSource);
    private List<string> List(TabSide side) => side == TabSide.Left ? _left : side == TabSide.Right ? _right : _extra[side];
    public TabSide? Owner(string key) => _left.Contains(key, StringComparer.OrdinalIgnoreCase) ? TabSide.Left
        : _right.Contains(key, StringComparer.OrdinalIgnoreCase) ? TabSide.Right : _extra.Where(pair => pair.Value.Contains(key, StringComparer.OrdinalIgnoreCase)).Select(pair => (TabSide?)pair.Key).FirstOrDefault();
    private void SetActive(TabSide side, string? key) { if (side == TabSide.Left) _leftActive = key; else if (side == TabSide.Right) _rightActive = key; else _extraActive[side] = key; }
    public void Reset(string main)
    { _left = [main]; _right = [PreviewKey]; _extra.Clear(); _extraActive.Clear(); _nextGroup = 2; _leftActive = main; _rightActive = PreviewKey; FocusedSide = TabSide.Left; }
    public void Restore(IEnumerable<string> left, IEnumerable<string> right, string? leftActive, string? rightActive, TabSide focused)
    {
        _extra.Clear(); _extraActive.Clear(); _nextGroup = 2;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _left = left.Where(seen.Add).ToList();
        _right = right.Where(seen.Add).ToList();
        if (!seen.Contains(PreviewKey)) _right.Add(PreviewKey);
        _leftActive = _left.FirstOrDefault(item => string.Equals(item, leftActive, StringComparison.OrdinalIgnoreCase)) ?? _left.FirstOrDefault();
        _rightActive = _right.FirstOrDefault(item => string.Equals(item, rightActive, StringComparison.OrdinalIgnoreCase)) ?? _right.FirstOrDefault();
        FocusedSide = focused is TabSide.Left or TabSide.Right && Has(focused) ? focused : Has(TabSide.Left) ? TabSide.Left : TabSide.Right;
    }
    public void Open(string path)
    {
        var side = Owner(path) ?? (IsSource(path) ? TabSide.Left : TabSide.Right);
        if (Owner(path) is null) List(side).Add(path);
        Select(side, path);
    }
    public void Focus(TabSide side) { if (Has(side)) FocusedSide = side; }
    public void RenameSource(string previous, string next)
    {
        if (!IsSource(previous) || !IsSource(next)) throw new ArgumentException("Only source tabs can be renamed.");
        foreach (var side in new[] { TabSide.Left, TabSide.Right }.Concat(ExtraGroups))
        {
            var tabs = List(side);
            for (var i = 0; i < tabs.Count; i++) if (tabs[i].Equals(previous, StringComparison.OrdinalIgnoreCase)) tabs[i] = next;
            if (string.Equals(Active(side), previous, StringComparison.OrdinalIgnoreCase)) SetActive(side, next);
        }
    }
    public void RenamePreviewEntry(string previous, string next)
    {
        var oldKey = PreviewKeyFor(previous); var newKey = PreviewKeyFor(next);
        if (Owner(oldKey) is null || Owner(newKey) is not null && !oldKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var side in new[] { TabSide.Left, TabSide.Right }.Concat(ExtraGroups))
        {
            var tabs = List(side);
            for (var i = 0; i < tabs.Count; i++) if (tabs[i].Equals(oldKey, StringComparison.OrdinalIgnoreCase)) tabs[i] = newKey;
            if (string.Equals(Active(side), oldKey, StringComparison.OrdinalIgnoreCase)) SetActive(side, newKey);
        }
    }
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
        if (!Has(FocusedSide)) FocusedSide = Has(TabSide.Left) ? TabSide.Left : Has(TabSide.Right) ? TabSide.Right : _extra.Keys.FirstOrDefault(Has);
        return close.ToArray();
    }
}
