using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

/// <summary>A persisted turn with transient original previews and activity disclosures.</summary>
public sealed class AiTurnView : StackPanel
{
    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(nameof(Turn), typeof(AiTurn), typeof(AiTurnView), new PropertyMetadata(null, (d, _) => ((AiTurnView)d).Build()));
    public AiTurn Turn { get => (AiTurn)GetValue(TurnProperty); set => SetValue(TurnProperty, value); }
    internal Action? Changed, Refill, Delete;
    internal Action<MarkdownView>? AnswerReady, AnswerGone;
    internal Action<AiEntry>? PreviewChanged;
    internal Action<string>? SourceRequested;
    internal List<MarkdownView> Answers { get; } = [];
    private readonly StackPanel _body = new(), _preview = new();
    private readonly Button _collapsed = new() { Content = "（已折叠，点击展开）", BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 16, FontFamily = new FontFamily("Times New Roman, Source Han Serif SC, FangSong"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
    private FrameworkElement? _footer;
    private string? _previewId;

    public AiTurnView() { KeyDown += (_, e) => { if (e.Key == Key.Escape && _previewId is not null) { ClosePreview(); e.Handled = true; } }; _collapsed.SetResourceReference(Control.ForegroundProperty, "Muted"); _collapsed.Click += (_, _) => Toggle(); }
    internal void Toggle()
    {
        Turn.Collapsed = !Turn.Collapsed; ApplyCollapsed(); Changed?.Invoke();
    }
    private void ApplyCollapsed()
    {
        _body.Visibility = Turn.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapsed.Visibility = Turn.Collapsed ? Visibility.Visible : Visibility.Collapsed;
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
        _body.Margin = new Thickness(12, 10, 4, 0); Children.Add(_body);
        foreach (var segment in Turn.DisplaySegments)
        {
            if (segment.Kind == "activity")
            {
                var row = new DockPanel(); var timestamp = new TextBlock { Text = segment.DisplayTime, FontSize = 10, Margin = new Thickness(12, 0, 0, 0) };
                timestamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(timestamp, Dock.Right); row.Children.Add(timestamp);
                var text = new TextBlock { Text = segment.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(text);
                UIElement content = row;
                if (!string.IsNullOrEmpty(segment.Details)) content = new Expander { Header = row, IsExpanded = false, Content = new TextBox { Text = segment.Details, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                var border = new Border { Child = content, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 7) };
                border.SetResourceReference(Border.BackgroundProperty, "ChatQuestionBackground"); border.SetResourceReference(Border.BorderBrushProperty, "Guide"); _body.Children.Add(border);
            }
            else
            {
                var answer = Rich(segment.Text, new Thickness(0, 0, 0, 10)); answer.Padding = new Thickness(0, 0, 34, 0); answer.DataContext = segment;
                answer.ConfigureFolding(Turn.CollapsedSections, segment.Id, () => Changed?.Invoke());
                answer.Loaded += (_, _) => AnswerReady?.Invoke(answer); answer.Unloaded += (_, _) => AnswerGone?.Invoke(answer);
                Answers.Add(answer); _body.Children.Add(answer);
            }
        }
        Children.Add(_collapsed); _footer = Footer(); Children.Add(_footer); ApplyCollapsed();
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
        var grid = new Grid { Margin = new Thickness(12, 16, 4, 0), MinHeight = 28, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = new Grid { Background = Brushes.Transparent, Focusable = true, Cursor = Cursors.Hand };
        toggle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toggle.ColumnDefinitions.Add(new ColumnDefinition());
        System.Windows.Automation.AutomationProperties.SetName(toggle, Turn.Footer);
        var stamp = new TextBlock { Text = Turn.Footer, FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; toggle.Children.Add(stamp);
        toggle.SizeChanged += (_, _) => stamp.MaxWidth = Math.Max(80, toggle.ActualWidth - 16);
        var dots = new Line { X2 = 1, Stretch = Stretch.Fill, StrokeThickness = 1, StrokeDashArray = [1, 2], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        dots.SetResourceReference(Shape.StrokeProperty, "Muted"); Grid.SetColumn(dots, 1); toggle.Children.Add(dots);
        toggle.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(); e.Handled = true; } };
        toggle.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(); e.Handled = true; } };
        grid.Children.Add(toggle);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1);
        actions.Children.Add(Icon("M7,2 H13 V10 H18 L10,18 2,10 H7 Z", "将问题重新放入编辑栏", () => Refill?.Invoke(), true));
        actions.Children.Add(Icon("M5,5 L15,15 M15,5 L5,15", "删除此轮问题和回答", () => Delete?.Invoke())); grid.Children.Add(actions);
        grid.SetResourceReference(TextElement.ForegroundProperty, "Muted"); return grid;
    }
    private static Button Icon(string data, string title, Action clicked, bool fill = false)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 1.5 };
        path.SetResourceReference(fill ? Shape.FillProperty : Shape.StrokeProperty, "Muted");
        var button = new Button { Content = path, Width = 28, Height = 28, Padding = new Thickness(4), BorderThickness = new Thickness(0), ToolTip = title };
        System.Windows.Automation.AutomationProperties.SetName(button, title); button.Click += (_, _) => clicked(); return button;
    }
}
