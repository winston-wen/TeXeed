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
public sealed class AiEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "问题";
    public string Question { get; set; } = "";
    public bool Selected { get; set; } = true;
    public string State { get; set; } = "待提交";
    public List<AiSourceBlock> Sources { get; set; } = [];
    public List<AiPdfBlock> PdfBlocks { get; set; } = [];
    public string PdfRevision { get; set; } = "";
    public bool IsPoint { get; set; }
}
public sealed record AiTurn(string Question, string Answer)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Model { get; init; } = "";
    public string ReasoningEffort { get; init; } = "";
    [JsonIgnore] public string DisplayQuestion => AiConversation.CleanQuestion(Question);
    [JsonIgnore] public string DisplayAnswer => string.IsNullOrWhiteSpace(Answer) ? "此旧记录未保存回答，可删除或重新提问。" : Answer;
    [JsonIgnore] public string DisplayTime => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
}

public sealed class AiConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiEntry> Entries { get; set; } = [];
    public List<AiTurn> Turns { get; set; } = [];
    public string Draft { get; set; } = "";
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
        return text.Length == 0 ? "新对话" : text[..Math.Min(text.Length, 45)];
    }
    internal AiConversation Snapshot() => new()
    {
        Id = Id, Title = Title, UpdatedAt = UpdatedAt, Draft = Draft, Turns = [.. Turns],
        Entries = Entries.Select(e => new AiEntry
        {
            Id = e.Id, Title = e.Title, Question = e.Question, Selected = e.Selected, State = e.State,
            Sources = [.. e.Sources], PdfBlocks = [.. e.PdfBlocks], PdfRevision = e.PdfRevision, IsPoint = e.IsPoint
        }).ToList()
    };
}

public sealed class AiNotebook
{
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
        if (!File.Exists(path)) return new();
        var text = File.ReadAllText(path);
        var json = JsonNode.Parse(text)?.AsObject();
        if (json?.ContainsKey(nameof(AiNotebook.Conversations)) == true)
        {
            var book = json.Deserialize<AiNotebook>() ?? new();
            _ = book.Active;
            return book;
        }
        // Version 0.3.0/0.3.1 stored a single flat conversation. Preserve it in the new history.
        var previous = JsonSerializer.Deserialize<AiConversation>(text) ?? new();
        if (previous.Turns.Count > 0) previous.Title = AiConversation.MakeTitle(previous.Turns[0].Question);
        return new() { Conversations = [previous], ActiveConversationId = previous.Id };
    }
    internal static void Save(string project, AiNotebook book)
    {
        var path = NotebookPath(project); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(book)); AtomicFile.Replace(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
