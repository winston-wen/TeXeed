using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeXeed.App.AI;
using TeXeed.Core;

namespace TeXeed.App.Controls;

/// <summary>A source view for one conversation; the document remains the authoritative model.</summary>
internal sealed class AiSessionSourceView : UserControl
{
    private readonly LatexEditor _editor = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
    private readonly Button _resolveConflict = new() { Content = "复制本地源码并重新载入", Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly AiSharedDocument _document;
    private readonly AiSourceAuthorTransformer _authorStyles = new();
    private string _revision;
    private bool _refreshing;
    private int _changedOffset, _changedLength;
    internal event EventHandler? DocumentEdited;
    internal event EventHandler? ConflictChanged;
    internal event EventHandler? CommentRequested;
    internal event EventHandler? CompileRequested;
    internal string Revision => _revision;
    internal bool HasConflict { get; private set; }
    internal string PendingText => _editor.Text;
    internal (int Start, int Length, string Text) Selection => (_editor.SelectionStart, _editor.SelectionLength, _editor.SelectedText);
    internal LatexEditor Editor => _editor;

    internal AiSessionSourceView(AiSharedDocument document, string sessionDirectory, AppSettings settings)
    {
        _document = document;
        _revision = document.Revision;
        var source = new ProjectFile(Path.Combine(sessionDirectory, "document.tex"), "document.tex", ProjectFileKind.Tex, document.Source);
        _editor.SetProjectFiles([source]); _editor.ShowFile(source, document.Source);
        _authorStyles.SetSpans(document.AuthorSpans); _editor.TextArea.TextView.LineTransformers.Add(_authorStyles);
        _editor.Document.Changing += (_, e) => { _changedOffset = e.Offset; _changedLength = e.InsertionLength; };
        SetSettings(settings);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var comment = new Button { Content = "评论选区", Margin = new Thickness(8, 4, 4, 4) };
        var compile = new Button { Content = "编译会话文稿", Margin = new Thickness(4) };
        comment.Click += (_, _) => CommentRequested?.Invoke(this, EventArgs.Empty);
        compile.Click += (_, _) => CompileRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(comment); actions.Children.Add(compile);
        var notice = new StackPanel(); notice.Children.Add(_status); notice.Children.Add(_resolveConflict);
        var layout = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); layout.Children.Add(actions); DockPanel.SetDock(notice, Dock.Top); layout.Children.Add(notice); layout.Children.Add(_editor); Content = layout;
        _resolveConflict.Click += (_, _) =>
        {
            if (MessageBox.Show(Window.GetWindow(this), "将本地源码复制到剪贴板，然后载入当前文稿。请先保存剪贴板中的内容。", "处理文稿冲突", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            ResolveConflictAfterCopy(text =>
            {
                try { Clipboard.SetText(text); return true; }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException) { _status.Text = "无法复制到剪贴板；本地源码仍保留。"; return false; }
            });
        };
        _editor.ContentEdited += OnContentEdited;
    }

    internal void SetSettings(AppSettings settings)
    {
        _editor.FontSize = settings.FontSize; _editor.FontFamily = new FontFamily(settings.EditorFontFamily); _editor.SetWordWrap(settings.WordWrap);
    }

    private void OnContentEdited(object? sender, EventArgs args)
    {
        if (_refreshing || HasConflict) return;
        if (!_document.TryApply(_revision, _editor.Text, "用户"))
        {
            HasConflict = true;
            _status.Text = "文稿已在其他位置修改。本编辑器中的文字保留，请先处理冲突。";
            _resolveConflict.Visibility = Visibility.Visible;
            _authorStyles.SetSpans([]); _editor.TextArea.TextView.Redraw();
            ConflictChanged?.Invoke(this, EventArgs.Empty); return;
        }
        _revision = _document.Revision; _authorStyles.SetSpans(_document.AuthorSpans);
        var offset = Math.Clamp(_changedOffset, 0, _editor.Document.TextLength);
        if (offset == _editor.Document.TextLength && offset > 0) offset--;
        var length = Math.Clamp(Math.Max(1, _changedLength), 0, _editor.Document.TextLength - offset);
        _editor.TextArea.TextView.Redraw(offset, length, System.Windows.Threading.DispatcherPriority.Normal);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
    }

    internal bool ResolveConflictAfterCopy(Func<string, bool> copy)
    {
        if (!HasConflict || !copy(PendingText)) return false;
        HasConflict = false;
        _resolveConflict.Visibility = Visibility.Collapsed;
        _status.Text = "本地源码已复制；现在显示当前文稿。";
        _revision = "";
        var refreshed = RefreshFromDocument();
        ConflictChanged?.Invoke(this, EventArgs.Empty);
        return refreshed;
    }

    internal bool RefreshFromDocument()
    {
        if (HasConflict) return false;
        if (_document.Revision == _revision) return true;
        var snapshot = _document.Snapshot();
        if (snapshot.Revision == _revision) return true;
        _refreshing = true;
        try
        {
            _authorStyles.SetSpans(snapshot.AuthorSpans);
            var selection = _editor.SelectionStart; var length = _editor.SelectionLength; var caret = _editor.CaretOffset;
            var old = _editor.Text; var start = 0;
            while (start < old.Length && start < snapshot.Source.Length && old[start] == snapshot.Source[start]) start++;
            if (start > 0 && ((start < old.Length && char.IsLowSurrogate(old[start])) || (start < snapshot.Source.Length && char.IsLowSurrogate(snapshot.Source[start])))) start--;
            var oldEnd = old.Length; var newEnd = snapshot.Source.Length;
            while (oldEnd > start && newEnd > start && old[oldEnd - 1] == snapshot.Source[newEnd - 1]) { oldEnd--; newEnd--; }
            if (oldEnd < old.Length && char.IsLowSurrogate(old[oldEnd])) oldEnd++;
            if (newEnd < snapshot.Source.Length && char.IsLowSurrogate(snapshot.Source[newEnd])) newEnd++;
            _editor.Document.Replace(start, oldEnd - start, snapshot.Source[start..newEnd]);
            if (length > 0) _editor.Select(Math.Min(selection, snapshot.Source.Length), Math.Min(length, Math.Max(0, snapshot.Source.Length - selection)));
            else _editor.CaretOffset = Math.Min(caret, snapshot.Source.Length);
            _revision = snapshot.Revision; return true;
        }
        finally { _refreshing = false; }
    }
}

/// <summary>A PDF view and compiler owned by one conversation.</summary>
internal sealed class AiSessionPdfView : UserControl, IDisposable
{
    private readonly PdfPreview _preview = new();
    private readonly AiDocumentCompiler _compiler = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _compileGate = new(1, 1);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
    private readonly Expander _buildLog = new() { Header = "编译日志", Visibility = Visibility.Collapsed, MaxHeight = 200, Margin = new Thickness(8, 0, 8, 4) };
    private string _buildLogText = "";
    private readonly AiReadOnlyCommentList _comments = new() { MaxHeight = 220, Visibility = Visibility.Collapsed };
    private readonly SyncTexService _sync = new();
    private readonly string _project, _conversationId;
    private readonly AiSharedDocument _document;
    private AppSettings _settings;
    private AiDocumentPreview? _displayed;
    private bool _disposed;
    internal event EventHandler? PreviewChanged;
    internal event EventHandler? PreviewSettingsRequested;
    internal PdfPreview Preview => _preview;
    internal AiDocumentPreview? Displayed => _displayed;
    internal string StatusText => _status.Text;

    internal AiSessionPdfView(string project, string conversationId, AiSharedDocument document, AppSettings settings)
    {
        _project = project; _conversationId = conversationId; _document = document; _settings = settings;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var compile = new Button { Content = "编译会话文稿", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 4, 4) };
        var comments = new Button { Content = "只读评论", Margin = new Thickness(4) };
        var settingsButton = new Button { Content = "预览设置", Margin = new Thickness(4) };
        compile.Click += async (_, _) => { compile.IsEnabled = false; try { await CompileAsync(); } finally { compile.IsEnabled = true; } };
        comments.Click += (_, _) => { _comments.Visibility = _comments.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (_comments.Visibility == Visibility.Visible) RefreshComments(); else _comments.Clear(); };
        settingsButton.Click += (_, _) => PreviewSettingsRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(compile); actions.Children.Add(comments); actions.Children.Add(settingsButton);
        _buildLog.Expanded += (_, _) => MountBuildLog();
        _buildLog.Collapsed += (_, _) => _buildLog.Content = null;
        var layout = new DockPanel(); DockPanel.SetDock(actions, Dock.Top); layout.Children.Add(actions); DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status); DockPanel.SetDock(_buildLog, Dock.Top); layout.Children.Add(_buildLog); DockPanel.SetDock(_comments, Dock.Top); layout.Children.Add(_comments); layout.Children.Add(_preview); Content = layout;
        _status.Text = "尚未编译会话文稿。"; SetSettings(settings);
        var restoreStarted = false;
        Loaded += async (_, _) => { if (!restoreStarted) { restoreStarted = true; await RestorePreviewAsync(); } };
    }

    private async Task RestorePreviewAsync()
    {
        var acquired = false;
        try
        {
            await _compileGate.WaitAsync(_shutdown.Token); acquired = true;
            if (_disposed || _displayed is not null) return;
            var restored = await _compiler.RestoreAsync(_project, _conversationId, _shutdown.Token);
            if (_disposed) return;
            if (restored is null)
            {
                if (File.Exists(AiDocumentCompiler.ManifestPath(_project, _conversationId))) _status.Text = "上次预览记录无效或文件已变化，无法恢复；原文件仍保留。";
                return;
            }
            await _preview.LoadAsync(restored.PdfPath);
            if (_disposed) return;
            _displayed = restored;
            _status.Text = await AiDocumentCompiler.IsCurrentAsync(restored, _document, _shutdown.Token) ? "已恢复上次预览" : "已恢复上次预览；文稿或资源已变化，预览已过期。";
            RefreshComments(); PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { if (!_disposed) _status.Text = "无法恢复上次预览：" + ex.Message; }
        finally { if (acquired) _compileGate.Release(); }
    }

    internal void SetSettings(AppSettings settings)
    {
        _settings = settings; _preview.InvertColors = settings.InvertPdfColors;
        _preview.SetCropOptions(settings.AutoCropPdf, settings.CompressPdfWhitespace);
        _preview.AutoWidthMaximumCm = settings.AutoWidthMaximumCm;
    }

    internal async Task CompileAsync(CancellationToken token = default)
    {
        if (_disposed) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = cancellation.Token;
        var acquired = false;
        try
        {
            await _compileGate.WaitAsync(token); acquired = true;
            if (_disposed) return;
            _status.Text = "正在编译会话文稿…";
            _buildLog.IsExpanded = false; _buildLog.Content = null; _buildLog.Visibility = Visibility.Collapsed; _buildLogText = "";
            var result = await _compiler.CompileAsync(_project, _conversationId, _document, _settings, token);
            if (_disposed) return;
            if (result.Preview is null)
            {
                if (result.Log is { Length: > 0 } log) ShowBuildFailure(result.Message, log);
                else _status.Text = result.Message;
                return;
            }
            var previous = _displayed;
            try { await _preview.LoadAsync(result.Preview.PdfPath); }
            catch { _compiler.DiscardUnshown(result.Preview, previous); throw; }
            if (_disposed) { _compiler.DiscardUnshown(result.Preview, previous); return; }
            _displayed = result.Preview;
            _status.Text = await AiDocumentCompiler.IsCurrentAsync(result.Preview, _document, token) ? "编译成功" : "预览已过期，请重新编译。";
            try { await _compiler.PublishAsync(_project, _conversationId, result.Preview, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            { if (!_disposed) _status.Text += "；无法保存预览记录：" + ex.Message; }
            if (previous is not null) _compiler.Release(previous);
            RefreshComments();
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "编译已停止。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_disposed) ShowBuildFailure("编译未完成", ex.Message); }
        finally { if (acquired) _compileGate.Release(); }
    }

    internal void ShowBuildFailure(string message, string log)
    {
        _status.Text = message + (_displayed is null ? "；尚无可用预览。" : "；保留上次预览，预览可能已过期。");
        _buildLogText = log; _buildLog.IsExpanded = false; _buildLog.Content = null;
        _buildLog.Visibility = string.IsNullOrEmpty(log) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MountBuildLog()
    {
        if (_buildLog.Content is not null || _buildLogText.Length == 0) return;
        var copy = new Button { Content = "复制日志", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 2, 8, 2) };
        copy.Click += (_, _) =>
        {
            try { CopyBuildLog(text => { Clipboard.SetText(text); return true; }); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "剪贴板暂时不可用，请选择日志文字后复制。"; }
        };
        var text = new TextBox { Name = "BuildLogText", Text = _buildLogText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei"), FontSize = 12, MinHeight = 60, MaxHeight = 145 };
        System.Windows.Automation.AutomationProperties.SetName(text, "会话编译日志");
        var body = new DockPanel(); DockPanel.SetDock(copy, Dock.Top); body.Children.Add(copy); body.Children.Add(text); _buildLog.Content = body;
    }

    internal bool CopyBuildLog(Func<string, bool> write) => _buildLogText.Length > 0 && write(_buildLogText);

    internal async Task RefreshStatusAsync()
    {
        if (_displayed is null || _disposed) return;
        var current = await AiDocumentCompiler.IsCurrentAsync(_displayed, _document);
        if (!_disposed) _status.Text = current ? "预览与当前文稿一致" : "预览已过期，请重新编译。";
    }

    internal void MarkSourceChanged()
    {
        if (!_disposed && _displayed is not null && _displayed.SourceRevision != _document.Revision)
            _status.Text = "预览已过期，请重新编译。";
    }

    internal void RefreshComments()
    {
        if (_disposed || _comments.Visibility != Visibility.Visible) return;
        var snapshot = _document.Snapshot();
        var rows = snapshot.Comments.Select(comment =>
        {
            var author = string.IsNullOrWhiteSpace(comment.Author) ? "用户" : comment.Author;
            var label = author + "：" + comment.Text + (comment.Orphaned ? " · 无法定位" : " · 点击尝试近似定位");
            return new AiReadOnlyCommentRow(label, comment.Replies, () => LocateCommentAsync(comment));
        }).ToArray();
        _comments.SetRows(rows);
    }

    internal async Task LocateCommentAsync(AiDocumentComment comment)
    {
        var displayed = _displayed;
        if (_disposed || displayed is null) { _status.Text = "尚无成功编译的预览，无法定位。"; return; }
        if (!await AiDocumentCompiler.IsCurrentAsync(displayed, _document)) { if (!_disposed) _status.Text = "预览已过期，请重新编译后定位。"; return; }
        var location = await AiCommentLocator.LocateAsync(_document, comment, displayed.SourceRevision, displayed.Project, displayed.PdfPath, _settings, _sync);
        if (_disposed || !ReferenceEquals(displayed, _displayed)) return;
        if (!await AiDocumentCompiler.IsCurrentAsync(displayed, _document)) { _status.Text = "预览已过期，请重新编译后定位。"; return; }
        if (location.Status != AiCommentLocationStatus.Approximate || location.Location is null) { _status.Text = "此评论无法定位到 PDF。"; return; }
        _preview.ShowLocation(location.Location, highlight: false);
        _status.Text = "已定位到源码对应的近似位置。";
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _shutdown.Cancel(); _compiler.Cancel(); _preview.Dispose(); }
}
