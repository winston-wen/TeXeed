using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>Local HTML reader for an authoritative conversation document.</summary>
public sealed class AiDocumentView : UserControl
{
    private const string Host = "texeed-document.invalid";
    private const string Address = "https://texeed-document.invalid/document.html";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WebView2 _browser = new();
    private AiSharedDocument? _document;
    private string _question = "", _userName = "用户", _modelName = "AI";
    private string? _preview;
    private string? _unapplied, _unappliedReason;
    private long _binding;
    private bool _ready, _initializing;

    public event EventHandler? DocumentEdited;
    public event EventHandler? VersionRequested;
    public event EventHandler? CarryRequested;
    public event EventHandler? SearchRequested;
    public Func<string?>? RoundIdProvider { get; set; }

    public AiDocumentView()
    {
        Content = _browser;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _browser.CoreWebView2 != null) return;
        _initializing = true;
        try
        {
            var userData = Path.Combine(AiStorage.Home, "webview2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 未初始化。");
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Assets", "AiDocument"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!IsDocument(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
                    ((uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host) || uri.Scheme is "data" or "blob")) return;
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate(Address);
        }
        catch (Exception ex)
        {
            Content = new TextBlock { Text = "无法加载文稿视图：" + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
        }
    }

    private static bool IsDocument(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host && uri.AbsolutePath == "/document.html";

    public void Bind(AiSharedDocument document, string question, string userName, string modelName)
    {
        if (!ReferenceEquals(_document, document)) { _binding++; _unapplied = null; _unappliedReason = null; if (_ready) Send(new { type = "clear-unapplied" }); }
        _document = document;
        _question = question ?? "";
        _userName = string.IsNullOrWhiteSpace(userName) ? "用户" : userName;
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "AI" : modelName;
        _preview = null;
        RefreshDocument();
    }

    public void RefreshDocument()
    {
        _preview = null;
        if (!_ready || _document == null) return;
        var snapshot = _document.Snapshot();
        Send(new { type = "state", binding = _binding.ToString(), source = snapshot.Source, initialSource = snapshot.InitialSource, revision = snapshot.Revision,
            authorSpans = snapshot.AuthorSpans, comments = snapshot.Comments, question = _question, userName = _userName, modelName = _modelName,
            theme = Theme() });
    }

    public void PreviewStreaming(string partial)
    {
        _preview = partial ?? "";
        if (_ready) Send(new { type = "preview", source = _preview });
    }

    public void SetQuestion(string question, string modelName)
    {
        _question = question ?? "";
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "AI" : modelName;
        if (_ready) Send(new { type = "metadata", question = _question, modelName = _modelName });
    }

    public void Locate(int start, int length) { if (_ready) Send(new { type = "locate", start, length }); }
    public void ShowUnapplied(string raw, string reason) { _unapplied = raw; _unappliedReason = reason; if (_ready) Send(new { type = "unapplied", raw, reason }); }

    private void Send(object value) => _browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(value, JsonOptions));
    private static Dictionary<string, string> Theme()
    {
        var output = new Dictionary<string, string>();
        foreach (var (css, resource) in new[] { ("bg", "EditorBackground"), ("sidebar", "PanelBackground"), ("panel", "PanelBackground"), ("ink", "Ink"), ("muted", "Muted"), ("line", "Line"), ("guide", "Guide"), ("focus", "ChatFocus") })
            if (Application.Current?.TryFindResource(resource) is SolidColorBrush brush) { var c = brush.Color; output[css] = $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}"; }
        return output;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsDocument(e.Source) || e.WebMessageAsJson.Length > 8_000_000) return;
        try
        {
            using var parsed = JsonDocument.Parse(e.WebMessageAsJson);
            var item = parsed.RootElement;
            var type = GetString(item, "type");
            if (type == "ready") { _ready = true; var pendingPreview = _preview; RefreshDocument(); if (pendingPreview != null) PreviewStreaming(pendingPreview); if (_unapplied != null) ShowUnapplied(_unapplied, _unappliedReason ?? ""); return; }
            if (type == "version") { VersionRequested?.Invoke(this, EventArgs.Empty); return; }
            if (type == "carry") { CarryRequested?.Invoke(this, EventArgs.Empty); return; }
            if (type == "search") { SearchRequested?.Invoke(this, EventArgs.Empty); return; }
            if (type == "refresh") { RefreshDocument(); return; }
            if (_document == null || type is not ("edit" or "comment" or "comment-delete")) return;
            if (GetString(item, "binding") != _binding.ToString()) { Reject("已切换会话；请复制保留的未保存文字，再重新打开文稿。"); return; }
            if (GetString(item, "revision") != _document.Revision) { Reject("文稿已发生变化；本地未保存文字保留在下方。"); return; }
            var success = type == "edit" ? ApplyEdit(item) : ApplyComment(item, type == "comment-delete");
            if (!success) { Reject("修改与当前文稿不一致；本地文字保留在下方。"); return; }
            Send(new { type = "ack", ok = true, revision = _document.Revision });
            DocumentEdited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        { Reject("无法保存修改：" + ex.Message); }
    }

    private void Reject(string error) => Send(new { type = "ack", ok = false, error });
    private static string GetString(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int GetInt(JsonElement item, string key) => item.GetProperty(key).GetInt32();
    private bool ApplyEdit(JsonElement item)
    {
        var start = GetInt(item, "start"); var removed = GetString(item, "removed"); var added = GetString(item, "added");
        var source = _document!.Source;
        if (start < 0 || start > source.Length || removed.Length > source.Length - start || !source.AsSpan(start, removed.Length).SequenceEqual(removed.AsSpan())) return false;
        return _document.TryApplyEdits(_document.Revision, [new AiDocumentEdit(start, removed.Length, added)], _userName, "user");
    }

    private static bool SameComment(JsonElement prior, AiDocumentComment c)
    {
        if (prior.ValueKind != JsonValueKind.Object || GetString(prior, "id") != c.Id || GetString(prior, "text") != c.Text ||
            GetString(prior, "quote") != c.Quote || GetInt(prior, "start") != c.Start || GetInt(prior, "end") != c.Start + c.Length ||
            prior.GetProperty("orphan").GetBoolean() != c.Orphaned) return false;
        var replies = prior.GetProperty("replies");
        return replies.GetArrayLength() == c.Replies.Count && replies.EnumerateArray().Select(x => x.GetString() ?? "").SequenceEqual(c.Replies.Select(x => x.Text));
    }

    private bool ApplyComment(JsonElement item, bool delete)
    {
        var previous = item.GetProperty("previous");
        var comment = delete ? previous : item.GetProperty("comment");
        var id = GetString(comment, "id");
        if (id.Length is 0 or > 128) return false;
        var existing = _document!.Comments.FirstOrDefault(c => c.Id == id);
        if (existing != null && !SameComment(previous, existing)) return false;
        if (existing == null && (delete || previous.ValueKind != JsonValueKind.Null)) return false;
        if (delete) return _document.Comments.Remove(existing!);
        var text = GetString(comment, "text");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return false;
        var replies = comment.GetProperty("replies").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        if (replies.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100_000)) return false;
        if (existing == null)
        {
            var start = GetInt(comment, "start"); var end = GetInt(comment, "end"); var quote = GetString(comment, "quote");
            if (start < 0 || end <= start || end > _document.Source.Length || _document.Source[start..end] != quote || replies.Length != 0) return false;
            _document.Comments.Add(new AiDocumentComment { Id = id, Start = start, Length = end - start, Quote = quote, Text = text, Author = _userName, Role = "user", RoundId = RoundIdProvider?.Invoke() ?? "" });
        }
        else
        {
            if (replies.Length < existing.Replies.Count) return false;
            existing.Text = text;
            for (var i = 0; i < replies.Length; i++)
                if (i < existing.Replies.Count) existing.Replies[i].Text = replies[i];
                else existing.Replies.Add(new AiDocumentReply { Author = _userName, Role = "user", Text = replies[i] });
        }
        return true;
    }
}
