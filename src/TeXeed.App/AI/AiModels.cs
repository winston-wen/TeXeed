using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeXeed.App.Services;

namespace TeXeed.App.AI;

public sealed record AiSourceBlock(string File, int StartLine, int EndLine, int Offset, string Text, string Revision);
public sealed record AiPdfBlock(int Page, double X, double Y, double Width, double Height, string ImageBase64)
{
    public double? PointX { get; init; }
    public double? PointY { get; init; }
}
public sealed record AiAnswerAnchor(string TurnId, string SegmentId, int Offset, int Length, string Quote, string Revision)
{
    // Semantic text offset survives responsive columns changing the FlowDocument structure.
    public int TextOffset { get; init; } = -1;
}

public sealed class AiEntry : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "问题";
    private string _question = "";
    public string Question { get => _question; set { if (_question == value) return; _question = value; PropertyChanged?.Invoke(this, new(nameof(Question))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public int Number { get; set; }
    public AiAnswerAnchor? AnswerAnchor { get; set; }
    [JsonIgnore] public string DisplayTitle => Number > 0 ? $"#{Number} · {Title}" : Title;
    public bool Selected { get; set; } = true;
    public string State { get; set; } = "待提交";
    public List<AiSourceBlock> Sources { get; set; } = [];
    public List<AiPdfBlock> PdfBlocks { get; set; } = [];
    public string PdfRevision { get; set; } = "";
    public AiEntry Snapshot(int? number = null) => new() { Id = Id, Number = number ?? Number, Title = Title, Question = Question, Selected = Selected, State = State, AnswerAnchor = AnswerAnchor, Sources = [.. Sources], PdfBlocks = [.. PdfBlocks], PdfRevision = PdfRevision, IsPoint = IsPoint };
    public bool IsPoint { get; set; }
}
public sealed record AiTurn(string Question, string Answer)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? AnsweredAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool? Incomplete { get; init; }
    public string? Prompt { get; init; }
    public List<AiEntry> SentAnnotations { get; init; } = [];
    public bool Collapsed { get; set; }
    public Dictionary<string, bool> CollapsedSections { get; init; } = [];
    public string Model { get; init; } = "";
    public string ReasoningEffort { get; init; } = "";
    public List<AiSegment> Segments { get; init; } = [];
    [JsonIgnore] public IReadOnlyList<AiSegment> DisplaySegments
    {
        get
        {
            var segments = Segments.Count > 0 ? Segments.Where(s => s.Id != "request").Select(s =>
            {
                if (SentAt is not null) return s;
                var legacy = s.Snapshot(); legacy.CreatedAt = legacy.CreatedAt.ToLocalTime(); return legacy;
            }).ToList() : [new AiSegment { Id = Id + "-answer", Kind = "final", Text = DisplayAnswer, CreatedAt = CreatedAt.ToLocalTime() }];
            foreach (var segment in segments) segment.ReferenceTime = SentAt ?? CreatedAt.ToLocalTime();
            return segments;
        }
    }
    [JsonIgnore] public string DisplayQuestion => AiConversation.CleanQuestion(Question);
    [JsonIgnore] public string DisplayAnswer => string.IsNullOrWhiteSpace(Answer) ? "此旧记录未保存回答，可删除或重新提问。" : Answer;
    [JsonIgnore] public string DisplayTime => AiTimestamp.Full(SentAt ?? CreatedAt.ToLocalTime());
    [JsonIgnore] public string SentTime => "发送时间：" + DisplayTime + (Collapsed ? "（已折叠）" : "");
    [JsonIgnore] public bool IsIncomplete => Incomplete ?? (AnsweredAt is null && (string.IsNullOrWhiteSpace(Answer) || Answer.StartsWith("[本轮未完成]") || FailureSegment is not null));
    private AiSegment? FailureSegment => Segments.LastOrDefault(s => s.Kind == "activity" && (s.Id == "failure" || s.Text.StartsWith("已停止。") || s.Text.StartsWith("本轮未完成：")));
    [JsonIgnore] public string AnswerTime
    {
        get
        {
            var end = IsIncomplete ? EndedAt ?? FailureSegment?.CreatedAt : AnsweredAt;
            return "答复时间：" + (end is { } time ? AiTimestamp.Full(time) : "未记录") + (IsIncomplete ? "（未完成）" : "");
        }
    }
}

public sealed record AiContextMemory(string Summary, string[] TurnIds, string Fingerprint);

public sealed class AiConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int NextAnnotationNumber { get; set; } = 1;
    public List<AiEntry> Entries { get; set; } = [];
    public List<AiTurn> Turns { get; set; } = [];
    public string Draft { get; set; } = "";
    public AiPermission Permission { get; set; } = AiPermission.FullAccess;
    public AiContextUsage? ContextUsage { get; set; }
    public AiContextMemory? ContextMemory { get; set; }
    public bool HeaderPinned { get; set; }
    public double ReadingOffset { get; set; }
    public string? ReadingTurnId { get; set; }
    public double ReadingTurnOffset { get; set; }
    [JsonIgnore] internal HashSet<string> ShellApprovals { get; } = [];
    [JsonIgnore] public string Summary => $"{Turns.Count} 轮 · {UpdatedAt.LocalDateTime:MM-dd HH:mm}";
    [JsonIgnore] public string RelativeTime
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - UpdatedAt;
            return elapsed.TotalMinutes < 1 ? "刚刚" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分钟" : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} 小时" : $"{(int)elapsed.TotalDays} 天";
        }
    }

    internal static string CleanQuestion(string text) => Regex.Replace(text, @"(?m)^\[[a-fA-F0-9]{8}\]\s*", "");
    internal static string MakeTitle(string question)
    {
        var text = CleanQuestion(question).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length == 0 ? "新对话" : string.Concat(text.EnumerateRunes().Take(20));
    }
    internal bool ReconcileSentAnnotations()
    {
        var changed = false;
        for (var i = 0; i < Turns.Count; i++)
        {
            var turn = Turns[i];
            if (turn.Prompt is not null || turn.SentAnnotations.Count > 0) continue;
            // Legacy turns embedded the complete submitted question. A matching
            // title alone must not consume a draft edited since that submission.
            var prompt = "\n" + turn.Question + "\n";
            var entries = Entries.Where(e => prompt.Contains("\n" + e.DisplayTitle + "：" + e.Question + "\n", StringComparison.Ordinal)).ToArray();
            if (entries.Length == 0) continue;
            foreach (var entry in entries) prompt = prompt.Replace("\n" + entry.DisplayTitle + "：" + entry.Question + "\n", "\n", StringComparison.Ordinal);
            Turns[i] = turn with { Prompt = prompt.Trim('\n'), SentAnnotations = entries.Select((e, n) => e.Snapshot(n + 1)).ToList() };
            changed = true;
        }
        var sent = Turns.SelectMany(t => t.SentAnnotations).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
        // Acceptance, rather than successful completion, consumes a pending entry.
        // Compare the payload too so an edited/replaced draft remains untouched.
        return Entries.RemoveAll(e => sent.TryGetValue(e.Id, out var snapshot)
            && e.Title == snapshot.Title && e.Question == snapshot.Question
            && e.PdfRevision == snapshot.PdfRevision && e.IsPoint == snapshot.IsPoint
            && e.AnswerAnchor == snapshot.AnswerAnchor
            && e.Sources.SequenceEqual(snapshot.Sources) && e.PdfBlocks.SequenceEqual(snapshot.PdfBlocks)) > 0 || changed;
    }
    internal AiConversation Snapshot() => new()
    {
        NextAnnotationNumber = NextAnnotationNumber, Id = Id, Title = Title, UpdatedAt = UpdatedAt, Draft = Draft, HeaderPinned = HeaderPinned, ReadingOffset = ReadingOffset, ReadingTurnId = ReadingTurnId, ReadingTurnOffset = ReadingTurnOffset, Turns = Turns.Select(t => t with { SentAnnotations = t.SentAnnotations.Select(e => e.Snapshot()).ToList(), Segments = t.Segments.Select(s => s.Snapshot()).ToList(), CollapsedSections = new(t.CollapsedSections) }).ToList(), Permission = Permission, ContextUsage = ContextUsage, ContextMemory = ContextMemory,
        Entries = Entries.Select(e => new AiEntry
        {
            Number = e.Number, AnswerAnchor = e.AnswerAnchor, Id = e.Id, Title = e.Title, Question = e.Question, Selected = e.Selected, State = e.State,
            Sources = [.. e.Sources], PdfBlocks = [.. e.PdfBlocks], PdfRevision = e.PdfRevision, IsPoint = e.IsPoint
        }).ToList()
    };
}

public sealed class AiNotebook
{
    internal bool RequiresSave { get; set; }
    public List<AiConversation> Conversations { get; set; } = [];
    public string ActiveConversationId { get; set; } = "";
    [JsonIgnore] public AiConversation Active
    {
        get
        {
            var active = Conversations.FirstOrDefault(c => c.Id == ActiveConversationId) ?? Conversations.FirstOrDefault();
            if (active is null) active = NewConversation();
            ActiveConversationId = active.Id;
            return active;
        }
    }
    [JsonIgnore] public List<AiEntry> Entries { get => Active.Entries; set => Active.Entries = value; }
    [JsonIgnore] public List<AiTurn> Turns { get => Active.Turns; set => Active.Turns = value; }
    [JsonIgnore] public string Draft { get => Active.Draft; set => Active.Draft = value; }

    internal AiConversation NewConversation()
    {
        var conversation = new AiConversation();
        Conversations.Add(conversation);
        ActiveConversationId = conversation.Id;
        return conversation;
    }
    internal bool RemoveConversation(string id)
    {
        if (Conversations.RemoveAll(c => c.Id == id) == 0) return false;
        _ = Active;
        return true;
    }
    internal AiNotebook Snapshot() => new()
    { ActiveConversationId = Active.Id, Conversations = Conversations.Select(c => c.Snapshot()).ToList() };
}
internal static class AiStorage
{
    internal static string Home => Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeXeed"), "ai");
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string NotebookPath(string project) => Path.Combine(Home, "projects", Hash(Path.GetFullPath(project).ToUpperInvariant()) + ".json");
    internal static AiNotebook Load(string project)
    {
        var path = NotebookPath(project);
        if (!File.Exists(path)) return Restore(new AiNotebook());
        var text = File.ReadAllText(path);
        var json = JsonNode.Parse(text)?.AsObject();
        if (json?.ContainsKey(nameof(AiNotebook.Conversations)) == true)
        {
            var book = json.Deserialize<AiNotebook>() ?? new();
            _ = book.Active;
            return Restore(book);
        }
        // Version 0.3.0/0.3.1 stored a single flat conversation. Preserve it in the new history.
        var previous = JsonSerializer.Deserialize<AiConversation>(text) ?? new();
        if (previous.Turns.Count > 0) previous.Title = AiConversation.MakeTitle(previous.Turns[0].Question);
        var migrated = new AiNotebook { Conversations = [previous], ActiveConversationId = previous.Id };
        return Restore(migrated);

        AiNotebook Restore(AiNotebook book)
        {
            AiTurnCheckpoint.Recover(project, book);
            foreach (var conversation in book.Conversations)
                if (conversation.ReconcileSentAnnotations()) book.RequiresSave = true;
            return book;
        }
    }
    internal static void Save(string project, AiNotebook book)
    {
        var path = NotebookPath(project); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, book); file.Flush(flushToDisk: true); }
            AtomicFile.Replace(temp, path);
            AiTurnCheckpoint.RetireSaved(project, book);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
