using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TeXeed.App.AI;

public sealed class AiSegment : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _kind = "text", _text = "";
    public string Kind { get => _kind; set { if (_kind == value) return; _kind = value; Changed(nameof(Kind)); } }
    public string Text { get => _text; set { if (_text == value) return; _text = value; Changed(nameof(Text)); } }
    private string _details = "";
    public string Details { get => _details; set { if (_details == value) return; _details = value; Changed(nameof(Details)); } }
    private DateTimeOffset _createdAt = DateTimeOffset.Now;
    public DateTimeOffset CreatedAt { get => _createdAt; set { if (_createdAt == value) return; _createdAt = value; Changed(nameof(DisplayTime)); } }
    [JsonIgnore] public DateTimeOffset ReferenceTime { get; set; }
    [JsonIgnore] public string DisplayTime => AiTimestamp.Activity(CreatedAt, ReferenceTime == default ? CreatedAt : ReferenceTime);
    [JsonIgnore] internal bool IsTransientStatus => Kind == "status" || Id.StartsWith("request-state-", StringComparison.Ordinal) ||
        Kind == "activity" && (Text is "已连接 AI" or "正在连接 AI" or "正在准备请求");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public AiSegment Snapshot() => new() { Id = Id, Kind = Kind, Text = Text, Details = Details, CreatedAt = CreatedAt, ReferenceTime = ReferenceTime };
}

internal sealed record AiStreamEvent(string Id, string Kind, string Text, bool Append = false, string Details = "", bool Reset = false);
internal delegate Task<string> AiToolRunner(string name, string arguments, CancellationToken token);

internal sealed class AiStreamBuffer
{
    private sealed class Node(AiStreamEvent value)
    {
        internal string Id = value.Id, Kind = value.Kind;
        internal string Details = value.Details;
        internal DateTimeOffset Time = DateTimeOffset.Now;
        internal System.Text.StringBuilder Text = new();
    }
    private readonly object _gate = new();
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<string, Node> _byId = [];
    private long _version;
    private long _statusVersion;
    private string _status = "等待 AI 响应";
    internal long Version { get { lock (_gate) return _version; } }
    internal (long Version, string Text) PendingStatus { get { lock (_gate) return (_statusVersion, _status); } }
    internal void Add(AiStreamEvent value)
    {
        lock (_gate)
        {
            if (value.Kind == "status")
            {
                if (_status != value.Text) { _status = value.Text; _statusVersion++; }
                return;
            }
            _version++;
            if (value.Reset)
            {
                if (_byId.Remove(value.Id, out var removed)) _nodes.Remove(removed);
                return;
            }
            if (!_byId.TryGetValue(value.Id, out var node)) { node = new(value); _nodes.Add(node); _byId.Add(value.Id, node); }
            if (!value.Append) node.Time = DateTimeOffset.Now;
            node.Kind = value.Kind;
            if (value.Details.Length > 0) node.Details = value.Details;
            if (value.Append) node.Text.Append(value.Text);
            else if (value.Text.Length > 0) { node.Text.Clear(); node.Text.Append(value.Text); }
        }
    }
    internal List<AiSegment> Snapshot()
    { lock (_gate) return _nodes.Select(n => new AiSegment { Id = n.Id, Kind = n.Kind, Text = n.Text.ToString(), Details = n.Details, CreatedAt = n.Time }).ToList(); }
}
