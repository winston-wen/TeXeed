using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>A persisted turn with transient original previews and activity disclosures.</summary>
public sealed class AiTurnView : StackPanel
{
    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(nameof(Turn), typeof(AiTurn), typeof(AiTurnView), new PropertyMetadata(null, (d, _) => ((AiTurnView)d).Build()));
    public AiTurn Turn { get => (AiTurn)GetValue(TurnProperty); set => SetValue(TurnProperty, value); }
    private int _turnNumber;
    public int TurnNumber { get => _turnNumber; set { _turnNumber = value; _number.Text = value > 0 ? $"[{value}]" : ""; foreach (var answer in Answers) answer.TurnNumber = value; } }
    private string? _imageBaseDirectory;
    public string? ImageBaseDirectory { get => _imageBaseDirectory; set { _imageBaseDirectory = value; foreach (var answer in Answers) answer.ImageBaseDirectory = value; } }
    internal Action? Changed;
    internal Action<MarkdownView>? AnswerReady, AnswerGone;
    internal Action<AiEntry>? PreviewChanged;
    internal Action<string>? SourceRequested;
    internal List<MarkdownView> Answers { get; } = [];
    private readonly StackPanel _body = new(), _preview = new();
    private readonly TextBlock _number = new() { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei"), FontSize = AiChatTypography.FontSize, Margin = new Thickness(-40, 8, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _sentTime = new() { FontSize = AiChatTypography.MetadataFontSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(AiChatTypography.TextLeft, 8, 4, 0), Focusable = true, Cursor = Cursors.Hand };
    private AiReplyStatus? _footer;
    private string? _previewId;
    private bool _bodyBuilt;

    public AiTurnView()
    {
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _previewId is not null) { ClosePreview(); e.Handled = true; } };
        _sentTime.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _number.SetResourceReference(TextBlock.ForegroundProperty, "ChatHeading");
        _sentTime.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(); e.Handled = true; } };
        _sentTime.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetHelpText(_sentTime, "双击收起本轮回答");
    }
    internal void Toggle()
    {
        Turn.Collapsed = !Turn.Collapsed; ApplyCollapsed(); Changed?.Invoke();
    }
    private void ApplyCollapsed()
    {
        if (!Turn.Collapsed && !_bodyBuilt) BuildBody();
        _body.Visibility = Turn.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        _sentTime.Text = Turn.SentTime;
        _sentTime.Visibility = Turn.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_footer is not null)
        {
            _footer.Text = Turn.Collapsed ? Turn.SentTime + "\n" + Turn.AnswerTime : Turn.AnswerTime;
            _footer.Margin = new Thickness(AiChatTypography.TextLeft, Turn.Collapsed ? 8 : 16, 4, 0);
            System.Windows.Automation.AutomationProperties.SetHelpText(_footer, Turn.Collapsed ? "已折叠，双击任一时间行展开" : "双击收起");
        }
    }
    private void Build()
    {
        foreach (var old in Answers) AnswerGone?.Invoke(old);
        if (_number.Parent is Panel question) question.Children.Remove(_number);
        Answers.Clear(); Children.Clear(); _body.Children.Clear(); _preview.Children.Clear(); _previewId = null;
        if (Turn is null) return;
        var entries = Turn.SentAnnotations;
        var prompt = entries.Count == 0 ? Turn.DisplayQuestion : Turn.Prompt ?? Turn.DisplayQuestion;
        var questionRow = new Grid(); questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AiChatTypography.TextLeft) }); questionRow.ColumnDefinitions.Add(new ColumnDefinition());
        var content = Rich(prompt, new Thickness(0, 8, 12, 0)); Grid.SetColumn(content, 1);
        _number.Margin = new Thickness(-40, 8 + (content.FontFamily.Baseline - _number.FontFamily.Baseline) * AiChatTypography.FontSize, 10, 0);
        questionRow.Children.Add(_number); questionRow.Children.Add(content); Children.Add(questionRow);
        if (entries.Count > 0)
        {
            var links = Rich(string.Join("\n", entries.Select((e, i) => $"{i + 1}. [{LinkLabel(e)}](#snapshot-{i + 1})")), new Thickness(AiChatTypography.TextLeft, 0, 12, 0), true);
            links.LocalLink = key => { if (int.TryParse(key.AsSpan("snapshot-".Length), out var n) && n > 0 && n <= entries.Count) ShowPreview(entries[n - 1], n); };
            Children.Add(links);
        }
        _preview.Visibility = Visibility.Collapsed; _preview.Margin = new Thickness(AiChatTypography.TextLeft, 8, 12, 10); Children.Add(_preview);
        Children.Add(_sentTime);
        _body.Margin = new Thickness(AiChatTypography.TextLeft, 14, 4, 0); Children.Add(_body);
        _bodyBuilt = false;
        _footer = Footer(); Children.Add(_footer); ApplyCollapsed();
    }
    private void BuildBody()
    {
        _bodyBuilt = true;
        foreach (var segment in Turn.DisplaySegments)
        {
            if (segment.Kind == "activity")
            {
                _body.Children.Add(new AiActivityView { DataContext = segment });
            }
            else
            {
                var answer = Rich("", new Thickness(0)); answer.Padding = new Thickness(0, 0, 34, 0); answer.DataContext = segment;
                answer.ChatMode = true; answer.TurnNumber = TurnNumber; answer.ImageBaseDirectory = ImageBaseDirectory;
                answer.ConfigureFolding(Turn.CollapsedSections, segment.Id, () => Changed?.Invoke());
                answer.Markdown = segment.Text;
                answer.Loaded += (_, _) => AnswerReady?.Invoke(answer); answer.Unloaded += (_, _) => AnswerGone?.Invoke(answer);
                Answers.Add(answer); _body.Children.Add(answer);
            }
        }
    }
    internal static string LinkLabel(AiEntry entry) => AnnotationSummary(entry).Replace("[", "\\[").Replace("]", "\\]");
    internal static string AnnotationSummary(AiEntry entry)
    {
        var text = string.IsNullOrWhiteSpace(entry.Question) ? entry.AnswerAnchor?.Quote ?? entry.Sources.FirstOrDefault()?.Text ?? entry.Title : entry.Question;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = string.Concat(text.EnumerateRunes().Take(80));
        return text;
    }
    private MarkdownView Rich(string source, Thickness margin, bool numbers = false) => new() { SourceLink = source => SourceRequested?.Invoke(source), LinkListNumbers = numbers, Markdown = source,
        Margin = new Thickness(margin.Left - AiChatTypography.RichTextInset, margin.Top, margin.Right, margin.Bottom), Padding = new Thickness(0) };
    internal void ShowPreview(AiEntry entry, int number)
    {
        if (_previewId == entry.Id) { ClosePreview(); return; }
        _previewId = entry.Id; _preview.Children.Clear();
        var header = new DockPanel(); var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), ToolTip = "关闭原文预览" }; DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => ClosePreview(); header.Children.Add(close); header.Children.Add(new TextBlock { Text = $"原文 · {number}", FontSize = 12 }); _preview.Children.Add(header);
        var contents = new StackPanel();
        if (entry.AnswerAnchor is { } quote) contents.Children.Add(Rich(quote.Quote, new Thickness(0)));
        foreach (var source in entry.Sources) { contents.Children.Add(new TextBlock { Text = source.File, FontSize = 12 }); contents.Children.Add(Rich(source.Text, new Thickness(0))); }
        foreach (var pdf in entry.PdfBlocks)
        {
            contents.Children.Add(new TextBlock { Text = $"PDF 第 {pdf.Page} 页 · 圈选区域快照", FontSize = 12 });
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(pdf.ImageBase64)); bitmap.EndInit(); bitmap.Freeze();
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 10) };
                ImagePreview.Enable(image, $"PDF 第 {pdf.Page} 页"); contents.Children.Add(image);
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException) { contents.Children.Add(new TextBlock { Text = "截图无法读取" }); }
        }
        _preview.Children.Add(new ScrollViewer { Content = contents, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _preview.Visibility = Visibility.Visible; PreviewChanged?.Invoke(entry);
    }
    internal void ClosePreview() { _preview.Visibility = Visibility.Collapsed; _preview.Children.Clear(); _previewId = null; }
    private AiReplyStatus Footer()
    {
        var toggle = new AiReplyStatus { Text = Turn.AnswerTime, Margin = new Thickness(AiChatTypography.TextLeft, 16, 4, 0), Focusable = true, Cursor = Cursors.Hand };
        toggle.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(); e.Handled = true; } };
        toggle.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(); e.Handled = true; } };
        return toggle;
    }
}
