using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.Core;

namespace TeXeed.App.Controls;

public partial class DocumentOutline : UserControl
{
    private readonly ObservableCollection<OutlineNode> _roots = [];
    private Dictionary<NodeKey, OutlineNode> _nodes = [];
    private string? _mainPath;
    public event EventHandler<SourceLocation>? NavigateRequested;

    public DocumentOutline()
    {
        InitializeComponent();
        Tree.ItemsSource = _roots;
    }

    public void SetAllExpanded(bool expanded)
    {
        foreach (var node in _nodes.Values) node.IsExpanded = expanded;
    }

    public void Update(IReadOnlyList<IndexEntry> entries, string mainPath)
    {
        if (!string.Equals(mainPath, _mainPath, StringComparison.OrdinalIgnoreCase))
        {
            _roots.Clear(); _nodes.Clear(); _mainPath = mainPath;
        }
        var next = new Dictionary<NodeKey, OutlineNode>();
        var occurrences = new Dictionary<(string Path, int Level, string Title), int>();
        var planned = entries.Select(entry =>
        {
            var identity = (entry.Path.ToUpperInvariant(), entry.Level, entry.Title);
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            return (Entry: entry, Key: new NodeKey(identity.Item1, entry.Level, entry.Title, occurrence));
        }).ToArray();
        var exactKeys = planned.Select(item => item.Key).ToHashSet();
        var renamedCandidates = _nodes.Where(pair => !exactKeys.Contains(pair.Key)).Select(pair => pair.Value).ToList();
        var roots = new List<OutlineNode>();
        var children = new Dictionary<OutlineNode, List<OutlineNode>>();
        var parents = new Stack<OutlineNode>();
        string? previousFile = null;
        foreach (var (entry, key) in planned)
        {
            // The index lists files separately, not expanded TeX input order. Do not
            // attach the next file's headings to an unrelated previous file's section.
            if (!string.Equals(previousFile, entry.Path, StringComparison.OrdinalIgnoreCase)) parents.Clear();
            previousFile = entry.Path;
            // Line numbers deliberately do not identify nodes: editing above a heading
            // must update navigation without resetting its collapsed state.
            if (!_nodes.TryGetValue(key, out var node))
            {
                // Preserve a heading being renamed in place, without taking nodes
                // that still have an exact match elsewhere after an insertion.
                var renamed = renamedCandidates.Where(candidate => candidate.Entry.Line == entry.Line
                    && candidate.Entry.Level == entry.Level && string.Equals(candidate.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                node = renamed.Length == 1 ? renamed[0] : new OutlineNode();
                renamedCandidates.Remove(node);
            }
            while (parents.TryPeek(out var parent) && parent.Entry.Level >= entry.Level) parents.Pop();
            node.Update(entry, Path.GetDirectoryName(mainPath)!, parents.Count + 1);
            next.Add(key, node); children[node] = [];
            if (parents.TryPeek(out var container)) children[container].Add(node); else roots.Add(node);
            parents.Push(node);
        }
        foreach (var pair in children) Reconcile(pair.Key.Children, pair.Value);
        Reconcile(_roots, roots);
        _nodes = next;
    }

    private static void Reconcile(ObservableCollection<OutlineNode> current, List<OutlineNode> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < current.Count && ReferenceEquals(current[index], desired[index])) continue;
            var previous = current.IndexOf(desired[index]);
            if (previous >= 0) current.Move(previous, index); else current.Insert(index, desired[index]);
        }
        while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
    }

    private void Heading_Click(object sender, RoutedEventArgs e)
    {
        // A native button owns the entire row up to the separate expander. Let it
        // capture and complete the click, including padding, before moving focus.
        if (sender is Button { DataContext: OutlineNode node })
        {
            e.Handled = true;
            node.IsSelected = true;
            Navigate(node);
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is OutlineNode node)
        { Navigate(node); e.Handled = true; }
    }

    private void Navigate(OutlineNode node) => NavigateRequested?.Invoke(this,
        new SourceLocation(node.Entry.Path, node.Entry.Line));
    private readonly record struct NodeKey(string Path, int Level, string Title, int Occurrence);
}

public sealed class OutlineNode : INotifyPropertyChanged
{
    public IndexEntry Entry { get; private set; } = new("", "", 1);
    public string Title => Entry.Title;
    public string Tooltip { get; private set; } = "";
    public bool IsRoot { get; private set; }
    public string LevelLabel { get; private set; } = "h1";
    public ObservableCollection<OutlineNode> Children { get; } = [];
    private bool _isExpanded = true, _isSelected;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) { _isExpanded = value; Changed(); } } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; Changed(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(IndexEntry entry, string root, int depth)
    {
        if (Entry != entry)
        {
            Entry = entry;
            Tooltip = $"{entry.Title}\n{Path.GetRelativePath(root, entry.Path)}:{entry.Line}";
            Changed(nameof(Title)); Changed(nameof(Tooltip));
        }
        var isRoot = depth == 1;
        if (IsRoot != isRoot) { IsRoot = isRoot; Changed(nameof(IsRoot)); }
        var label = $"h{depth}";
        if (LevelLabel != label) { LevelLabel = label; Changed(nameof(LevelLabel)); }
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
