using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed class AiSegment : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _kind = "text", _text = "";
    public string Kind { get => _kind; set { _kind = value; Changed(nameof(Kind)); } }
    public string Text { get => _text; set { _text = value; Changed(nameof(Text)); } }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string DisplayTime => CreatedAt.LocalDateTime.ToString("HH:mm:ss");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public AiSegment Snapshot() => new() { Id = Id, Kind = Kind, Text = Text, CreatedAt = CreatedAt };
}

internal sealed record AiStreamEvent(string Id, string Kind, string Text, bool Append = false);
internal delegate Task<string> AiToolRunner(string name, string arguments, CancellationToken token);

internal sealed class AiStreamBuffer
{
    private sealed class Node(AiStreamEvent value)
    {
        internal string Id = value.Id, Kind = value.Kind;
        internal DateTimeOffset Time = DateTimeOffset.UtcNow;
        internal System.Text.StringBuilder Text = new();
    }
    private readonly object _gate = new();
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<string, Node> _byId = [];
    internal void Add(AiStreamEvent value)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(value.Id, out var node)) { node = new(value); _nodes.Add(node); _byId.Add(value.Id, node); }
            node.Kind = value.Kind;
            if (value.Append) node.Text.Append(value.Text);
            else if (value.Text.Length > 0) { node.Text.Clear(); node.Text.Append(value.Text); }
        }
    }
    internal List<AiSegment> Snapshot()
    { lock (_gate) return _nodes.Select(n => new AiSegment { Id = n.Id, Kind = n.Kind, Text = n.Text.ToString(), CreatedAt = n.Time }).ToList(); }
}
