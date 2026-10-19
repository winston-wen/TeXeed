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
    internal Action? Changed;
    internal Action<MarkdownView>? AnswerReady, AnswerGone;
    internal Action<AiEntry>? PreviewChanged;
    internal Action<string>? SourceRequested;
    internal List<MarkdownView> Answers { get; } = [];
    private readonly StackPanel _body = new(), _preview = new();
    private readonly TextBlock _sentTime = new() { FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 4, 8) };
    private FrameworkElement? _footer;
    private string? _previewId;
    private bool _bodyBuilt;

    public AiTurnView() { KeyDown += (_, e) => { if (e.Key == Key.Escape && _previewId is not null) { ClosePreview(); e.Handled = true; } }; _sentTime.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
    internal void Toggle()
    {
        Turn.Collapsed = !Turn.Collapsed; ApplyCollapsed(); Changed?.Invoke();
    }
    private void ApplyCollapsed()
    {
        if (!Turn.Collapsed && !_bodyBuilt) BuildBody();
        _body.Visibility = Turn.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        _sentTime.Text = Turn.SentTime;
        if (_footer is not null) System.Windows.Automation.AutomationProperties.SetHelpText(_footer, Turn.Collapsed ? "已折叠，双击展开" : "双击收起");
    }
    private void Build()
    {
        foreach (var old in Answers) AnswerGone?.Invoke(old);
        Answers.Clear(); Children.Clear(); _body.Children.Clear(); _preview.Children.Clear(); _previewId = null;
        if (Turn is null) return;
        var entries = Turn.SentAnnotations;
        var prompt = entries.Count == 0 ? Turn.DisplayQuestion : Turn.Prompt ?? Turn.DisplayQuestion;
        Children.Add(Rich(prompt, new Thickness(12, 8, 12, 0)));
        if (entries.Count > 0)
        {
            var links = Rich(string.Join("\n", entries.Select((e, i) => $"{i + 1}. [{LinkLabel(e)}](#snapshot-{i + 1})")), new Thickness(12, 0, 12, 0), true);
            links.LocalLink = key => { if (int.TryParse(key.AsSpan("snapshot-".Length), out var n) && n > 0 && n <= entries.Count) ShowPreview(entries[n - 1], n); };
            Children.Add(links);
        }
        _preview.Visibility = Visibility.Collapsed; _preview.Margin = new Thickness(12, 8, 12, 10); Children.Add(_preview);
        Children.Add(_sentTime);
        _body.Margin = new Thickness(12, 10, 4, 0); Children.Add(_body);
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
                var row = new DockPanel(); var timestamp = new TextBlock { Text = segment.DisplayTime, FontSize = 10, Margin = new Thickness(12, 0, 0, 0) };
                timestamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(timestamp, Dock.Right); row.Children.Add(timestamp);
                var text = new TextBlock { Text = segment.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(text);
                UIElement content = row;
                if (!string.IsNullOrEmpty(segment.Details)) content = new Expander { Header = row, IsExpanded = false, Content = new TextBox { Text = segment.Details, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                var border = new Border { Child = content, DataContext = segment, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 7) };
                border.SetResourceReference(Border.BackgroundProperty, "ChatQuestionBackground"); border.SetResourceReference(Border.BorderBrushProperty, "Guide"); _body.Children.Add(border);
            }
            else
            {
                var answer = Rich("", new Thickness(0, 0, 0, 10)); answer.Padding = new Thickness(0, 0, 34, 0); answer.DataContext = segment;
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
    private MarkdownView Rich(string source, Thickness margin, bool numbers = false) => new() { SourceLink = source => SourceRequested?.Invoke(source), LinkListNumbers = numbers, Markdown = source, Margin = margin, Padding = new Thickness(0) };
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
                contents.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 10) });
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException) { contents.Children.Add(new TextBlock { Text = "截图无法读取" }); }
        }
        _preview.Children.Add(new ScrollViewer { Content = contents, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _preview.Visibility = Visibility.Visible; PreviewChanged?.Invoke(entry);
    }
    internal void ClosePreview() { _preview.Visibility = Visibility.Collapsed; _preview.Children.Clear(); _previewId = null; }
    private FrameworkElement Footer()
    {
        var toggle = new AiReplyStatus { Text = Turn.AnswerTime, Margin = new Thickness(12, 14, 4, 0), Focusable = true, Cursor = Cursors.Hand };
        toggle.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(); e.Handled = true; } };
        toggle.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(); e.Handled = true; } };
        return toggle;
    }
}
