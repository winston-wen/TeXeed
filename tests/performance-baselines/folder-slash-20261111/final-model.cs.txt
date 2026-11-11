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
public sealed record AiPastedImage(string ImageBase64);
public sealed record AiAttachedFile(string Name, string MimeType, string Base64)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FolderPath { get; init; }
}
public sealed record AiAttachmentItem(string DisplayPath, bool IsFolder, IReadOnlyList<AiAttachedFile> Files)
{
    public string DisplayStem => IsFolder ? DisplayPath[..^1] : DisplayPath;
    public string DisplayTerminator => IsFolder ? DisplayPath[^1..] : "";
    [JsonIgnore] public string DisplayPrefix
    {
        get
        {
            if (!IsFolder) return DisplayPath;
            var previous = DisplayPath.Length > 1 ? DisplayPath.LastIndexOfAny(['/', '\\'], DisplayPath.Length - 2) : -1;
            return previous >= 0 ? DisplayPath[..previous] : "";
        }
    }
    [JsonIgnore] public string DisplayMiddleSeparator
    {
        get
        {
            if (!IsFolder) return "";
            var previous = DisplayPath.Length > 1 ? DisplayPath.LastIndexOfAny(['/', '\\'], DisplayPath.Length - 2) : -1;
            return previous >= 0 ? DisplayPath[previous..(previous + 1)] : "";
        }
    }
    [JsonIgnore] public string DisplayTail => IsFolder ? DisplayPath[(DisplayPrefix.Length + DisplayMiddleSeparator.Length)..] : "";
    [JsonIgnore] public string DisplayLeaf => IsFolder ? DisplayTail[..^1] : "";
}
public sealed record AiAnswerAnchor(string TurnId, string SegmentId, int Offset, int Length, string Quote, string Revision)
{
    // Semantic text offset survives responsive columns changing the FlowDocument structure.
    public int TextOffset { get; init; } = -1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PreviewKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PreviewSource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PreviewNumber { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PreviewBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PreviewAfter { get; init; }
}

public sealed record AiUserBookmark
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = "";
    public AiAnswerAnchor? Anchor { get; init; }
    public List<string> FoldKeys { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextAfter { get; init; }
}

public sealed class AiEntry : System.ComponentModel.INotifyPropertyChanged
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "问题";
    private string _question = "";
    public string Question { get => _question; set { if (_question == value) return; _question = value; PropertyChanged?.Invoke(this, new(nameof(Question))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public int Number { get; set; }
    public AiAnswerAnchor? AnswerAnchor { get; set; }
    [JsonIgnore] public string DisplayTitle => Number > 0 ? $"#{Number} · {Title}" : Title;
    private bool _selected = true;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ReferenceSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AttachmentSelection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SentReferencePart { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SentAttachmentPart { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? EntryKind { get; set; }
    [JsonIgnore] public bool ReferenceSelected { get => ReferenceSelection ?? Selected; set { ReferenceSelection = value; PropertyChanged?.Invoke(this, new(nameof(ReferenceSelected))); } }
    [JsonIgnore] public bool AttachmentSelected { get => AttachmentSelection ?? Selected; set { AttachmentSelection = value; PropertyChanged?.Invoke(this, new(nameof(AttachmentSelected))); } }
    public string State { get; set; } = "待提交";
    public List<AiSourceBlock> Sources { get; set; } = [];
    public List<AiPdfBlock> PdfBlocks { get; set; } = [];
    public System.Collections.ObjectModel.ObservableCollection<AiPastedImage> Images { get; set; } = [];
    public List<AiAttachedFile> Files { get; set; } = [];
    [JsonIgnore] public bool HasAttachmentImages => Images.Count > 0 && (EntryKind == "attachment" ||
        EntryKind is null && Title == "粘贴的图片" && AnswerAnchor is null && Sources.Count == 0 && PdfBlocks.Count == 0);
    [JsonIgnore] public bool HasAttachments => Files.Count > 0 || HasAttachmentImages;
    [JsonIgnore] public bool HasReference => AnswerAnchor is not null || Sources.Count > 0 || PdfBlocks.Count > 0 ||
        EntryKind != "attachment" && (!HasAttachments || !string.IsNullOrWhiteSpace(Question));
    [JsonIgnore] public IReadOnlyList<AiAttachmentItem> AttachmentItems
    {
        get
        {
            var items = new List<AiAttachmentItem>();
            var groups = new Dictionary<string, List<AiAttachedFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                var folder = file.FolderPath;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    // Earlier versions saved directory attachments as relative paths.
                    var slash = file.Name.IndexOfAny(['/', '\\']);
                    if (slash > 0 && !Path.IsPathRooted(file.Name)) folder = file.Name[..(slash + 1)];
                }
                if (string.IsNullOrWhiteSpace(folder)) { items.Add(new(file.Name, false, [file])); continue; }
                folder = folder.TrimEnd('/', '\\') + (folder.Contains('\\') ? "\\" : "/");
                if (!groups.TryGetValue(folder, out var files))
                {
                    groups.Add(folder, files = []);
                    items.Add(new(folder, true, files));
                }
                files.Add(file);
            }
            return items;
        }
    }
    public string PdfRevision { get; set; } = "";
    public AiEntry Snapshot(int? number = null) => new() { Id = Id, Number = number ?? Number, Title = Title, Question = Question, Selected = Selected, ReferenceSelection = ReferenceSelection, AttachmentSelection = AttachmentSelection, SentReferencePart = SentReferencePart, SentAttachmentPart = SentAttachmentPart, EntryKind = EntryKind, State = State, AnswerAnchor = AnswerAnchor, Sources = [.. Sources], PdfBlocks = [.. PdfBlocks], Images = new(Images), Files = [.. Files], PdfRevision = PdfRevision, IsPoint = IsPoint, AdditionalData = AdditionalData is null ? null : new(AdditionalData) };
    public bool IsPoint { get; set; }
}
public sealed record AiTurn(string Question, string Answer)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? AnsweredAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool? Incomplete { get; init; }
    public string? EndReason { get; init; }
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
            var segments = Segments.Count > 0 ? Segments.Where(s => s.Id != "request" && !s.IsTransientStatus).Select(s =>
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
    [JsonIgnore] public string SentTime => DisplayTime + " 发送" + (Collapsed ? "（已折叠）" : "");
    [JsonIgnore] public bool IsIncomplete => Incomplete ?? (AnsweredAt is null && (string.IsNullOrWhiteSpace(Answer) || Answer.StartsWith("[本轮未完成]") || FailureSegment is not null));
    private AiSegment? FailureSegment => Segments.LastOrDefault(s => s.Kind == "activity" && (s.Id == "failure" || s.Text.StartsWith("已停止。") || s.Text.StartsWith("本轮未完成：")));
    [JsonIgnore] public string AnswerTime
    {
        get
        {
            var end = IsIncomplete ? EndedAt ?? FailureSegment?.CreatedAt : AnsweredAt;
            var stoppedByUser = EndReason == "user" || EndReason is null && FailureSegment?.Text.StartsWith("已停止。") == true;
            var status = IsIncomplete ? stoppedByUser ? "用户中止" : "程序中止" : "答复";
            return (end is { } time ? AiTimestamp.Full(time) : "未记录") + " " + status;
        }
    }
}

public sealed record AiContextMemory(string Summary, string[] TurnIds, string Fingerprint);

public sealed class AiConversation
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; set; }
    [JsonIgnore] public DateTimeOffset? LastSend => Turns.Select(t => (DateTimeOffset?)(t.SentAt ?? t.CreatedAt)).Append(LastSentAt).Max();
    [JsonIgnore] public string TabTitle
    {
        get
        {
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(Title);
            var text = new StringBuilder(); var count = 0;
            while (elements.MoveNext()) { if (count++ == 15) return text + "…"; text.Append(elements.GetTextElement()); }
            return text.ToString();
        }
    }
    public List<AiEntry> Entries { get; set; } = [];
    public List<AiTurn> Turns { get; set; } = [];
    public string Draft { get; set; } = "";
    public string? ParentConversationId { get; set; }
    public AiAnswerAnchor? BranchAnchor { get; set; }
    public string? BranchContextBefore { get; set; }
    public string? BranchContextAfter { get; set; }
    public List<AiTurn> InheritedTurns { get; set; } = [];
    public AiPermission Permission { get; set; } = AiPermission.FullAccess;
    public AiContextUsage? ContextUsage { get; set; }
    public AiContextMemory? ContextMemory { get; set; }
    public bool HeaderPinned { get; set; }
    public double ReadingOffset { get; set; }
    public string? ReadingTurnId { get; set; }
    public double ReadingTurnOffset { get; set; }
    public List<AiUserBookmark> Bookmarks { get; set; } = [];
    public string BookmarkSort { get; set; } = "document";
    public double? BookmarkBallX { get; set; }
    public double? BookmarkBallY { get; set; }
    [JsonIgnore] internal HashSet<string> ShellApprovals { get; } = [];

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
            var entries = Entries.Where(e => e.State is "已完成" or "提交中" or "未完成，可重试" or "上次提交未完成，可重试")
                .Where(e => new[] { e.DisplayTitle, e.Title }.Any(label => prompt.Contains("\n" + label + "：" + e.Question + "\n", StringComparison.Ordinal))).ToArray();
            if (entries.Length == 0) continue;
            foreach (var entry in entries)
                foreach (var label in new[] { entry.DisplayTitle, entry.Title })
                    prompt = prompt.Replace("\n" + label + "：" + entry.Question + "\n", "\n", StringComparison.Ordinal);
            Turns[i] = turn with { Prompt = prompt.Trim('\n'), SentAnnotations = entries.Select((e, n) => e.Snapshot(n + 1)).ToList() };
            changed = true;
        }
        var sent = Turns.SelectMany(t => t.SentAnnotations).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.ToArray());
        // Acceptance, rather than successful completion, consumes a pending entry.
        // Compare the payload too so an edited/replaced draft remains untouched.
        foreach (var entry in Entries.ToArray())
        {
            if (!sent.TryGetValue(entry.Id, out var snapshots)) continue;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.SentReferencePart is null && snapshot.SentAttachmentPart is null)
                {
                    if (entry.Title == snapshot.Title && entry.Question == snapshot.Question && entry.PdfRevision == snapshot.PdfRevision && entry.IsPoint == snapshot.IsPoint &&
                        entry.AnswerAnchor == snapshot.AnswerAnchor && entry.Sources.SequenceEqual(snapshot.Sources) && entry.PdfBlocks.SequenceEqual(snapshot.PdfBlocks) &&
                        entry.Images.SequenceEqual(snapshot.Images) && entry.Files.SequenceEqual(snapshot.Files))
                    { Entries.Remove(entry); changed = true; break; }
                    continue;
                }
                // Only consume an accepted part while its content still matches the checkpoint.
                if (entry.AdditionalData is { Count: > 0 }) continue;
                var imageIsAttachment = entry.HasAttachmentImages;
                var referenceMatches = snapshot.SentReferencePart == true && entry.HasReference && entry.Title == snapshot.Title &&
                    entry.Question == snapshot.Question && entry.AnswerAnchor == snapshot.AnswerAnchor && entry.PdfRevision == snapshot.PdfRevision &&
                    entry.IsPoint == snapshot.IsPoint && entry.Sources.SequenceEqual(snapshot.Sources) && entry.PdfBlocks.SequenceEqual(snapshot.PdfBlocks) &&
                    (imageIsAttachment || entry.Images.SequenceEqual(snapshot.Images));
                var attachmentMatches = snapshot.SentAttachmentPart == true && entry.HasAttachments && entry.Title == snapshot.Title &&
                    entry.Files.SequenceEqual(snapshot.Files) && (!imageIsAttachment || entry.Images.SequenceEqual(snapshot.Images));
                if (!referenceMatches && !attachmentMatches) continue;
                var keepReference = entry.HasReference && !referenceMatches;
                var keepAttachment = entry.HasAttachments && !attachmentMatches;
                if (!keepReference && !keepAttachment) { Entries.Remove(entry); changed = true; break; }
                if (referenceMatches)
                {
                    entry.AnswerAnchor = null; entry.Sources.Clear(); entry.PdfBlocks.Clear(); entry.PdfRevision = "";
                    if (!imageIsAttachment) entry.Images.Clear();
                    entry.Question = ""; entry.EntryKind = "attachment"; entry.ReferenceSelection = false;
                }
                if (attachmentMatches)
                { entry.Files.Clear(); if (imageIsAttachment) entry.Images.Clear(); entry.AttachmentSelection = false; }
                changed = true;
            }
        }
        return changed;
    }
    internal AiConversation Snapshot() => new()
    {
        Id = Id, Title = Title, UpdatedAt = UpdatedAt, LastSentAt = LastSentAt, Draft = Draft, HeaderPinned = HeaderPinned, ReadingOffset = ReadingOffset, ReadingTurnId = ReadingTurnId, ReadingTurnOffset = ReadingTurnOffset, BookmarkBallX = BookmarkBallX, BookmarkBallY = BookmarkBallY, BookmarkSort = BookmarkSort, Bookmarks = Bookmarks.Select(b => b with { FoldKeys = [.. b.FoldKeys] }).ToList(), Turns = Turns.Select(t => t with { SentAnnotations = t.SentAnnotations.Select(e => e.Snapshot()).ToList(), Segments = t.Segments.Select(s => s.Snapshot()).ToList(), CollapsedSections = new(t.CollapsedSections) }).ToList(), Permission = Permission, ContextUsage = ContextUsage, ContextMemory = ContextMemory,
        Entries = Entries.Select(e => e.Snapshot()).ToList(), ParentConversationId = ParentConversationId, BranchAnchor = BranchAnchor, BranchContextBefore = BranchContextBefore, BranchContextAfter = BranchContextAfter,
        InheritedTurns = InheritedTurns.Select(t => t with { SentAnnotations = t.SentAnnotations.Select(e => e.Snapshot()).ToList(), Segments = t.Segments.Select(s => s.Snapshot()).ToList(), CollapsedSections = new(t.CollapsedSections) }).ToList(), AdditionalData = AdditionalData is null ? null : new(AdditionalData)
    };
}

public sealed class AiNotebook
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
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

    private static bool HasEmptyNewConversationContent(AiConversation c) =>
        c.Title == "新对话" && c.Draft is { Length: 0 } && c.Turns is { Count: 0 } && c.Entries is { Count: 0 } &&
        c.Bookmarks is { Count: 0 } && c.LastSentAt is null && c.ParentConversationId is null && c.BranchAnchor is null &&
        c.BranchContextBefore is null && c.BranchContextAfter is null && c.InheritedTurns is { Count: 0 } &&
        c.ContextUsage is null && c.ContextMemory is null && !c.HeaderPinned && c.ReadingOffset == 0 &&
        c.ReadingTurnId is null && c.ReadingTurnOffset == 0 && c.BookmarkBallX is null && c.BookmarkBallY is null &&
        c.BookmarkSort == "document" && c.Permission == AiPermission.FullAccess && c.ShellApprovals.Count == 0 &&
        (c.AdditionalData is null || c.AdditionalData.Count == 0);

    internal bool IsEmptyNewConversation(AiConversation c) => HasEmptyNewConversationContent(c) &&
        !Conversations.Any(other => other.ParentConversationId == c.Id);

    internal IReadOnlyList<AiConversation> EmptyNewConversations()
    {
        var parents = Conversations.Select(c => c.ParentConversationId).Where(id => id is not null).ToHashSet();
        return Conversations.Where(c => HasEmptyNewConversationContent(c) && !parents.Contains(c.Id)).ToArray();
    }

    internal bool PruneEmptyNewConversations()
    {
        var empty = EmptyNewConversations();
        if (empty.Count <= 1) return false;
        var keep = empty.FirstOrDefault(c => c.Id == ActiveConversationId) ?? empty[0];
        var remove = empty.Where(c => c != keep).ToHashSet();
        return Conversations.RemoveAll(remove.Contains) > 0;
    }

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
    { ActiveConversationId = Active.Id, Conversations = Conversations.Select(c => c.Snapshot()).ToList(), AdditionalData = AdditionalData is null ? null : new(AdditionalData) };
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
