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
    public int TurnNumber { get => _turnNumber; set { _turnNumber = value; _number.Text = value > 0 ? $"[{value}]" : ""; _ordinalAlignment?.Schedule(); foreach (var answer in Answers) answer.TurnNumber = value; } }
    private ChatOrdinalAlignment? _ordinalAlignment;
    private string? _imageBaseDirectory;
    public string? ImageBaseDirectory { get => _imageBaseDirectory; set { _imageBaseDirectory = value; foreach (var answer in Answers) answer.ImageBaseDirectory = value; } }
    internal Action? Changed;
    internal Action<MarkdownView>? AnswerReady, AnswerGone;
    internal Action<string>? SourceRequested;
    internal Action? RetryRequested;
    private bool _canRetry;
    internal bool CanRetry { get => _canRetry; set { _canRetry = value; if (_footer is not null) _footer.CanRetry = value; } }
    internal List<MarkdownView> Answers { get; } = [];
    private readonly StackPanel _body = new();
    private readonly TextBlock _number = new() { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei"), FontSize = AiChatTypography.FontSize, Margin = new Thickness(-40, 8, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _sentTime = new() { FontSize = AiChatTypography.MetadataFontSize, TextWrapping = TextWrapping.Wrap, LineHeight = AiChatTypography.ExpandedLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0), Focusable = true, Cursor = Cursors.Hand };
    private AiReplyStatus? _footer;
    private bool _bodyBuilt;

    public AiTurnView()
    {
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
            _footer.Margin = new Thickness(AiChatTypography.TextLeft, Turn.Collapsed ? 8 : 0, 4, 0);
            _footer.IsCollapsed = Turn.Collapsed;
            System.Windows.Automation.AutomationProperties.SetHelpText(_footer, Turn.Collapsed ? "已折叠，双击任一时间行展开" : "双击收起");
        }
    }
    private void Build()
    {
        _ordinalAlignment?.Dispose(); _ordinalAlignment = null;
        foreach (var old in Answers) AnswerGone?.Invoke(old);
        if (_number.Parent is Panel question) question.Children.Remove(_number);
        Answers.Clear(); Children.Clear(); _body.Children.Clear();
        if (Turn is null) return;
        var questionRow = new Grid(); questionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AiChatTypography.TextLeft) }); questionRow.ColumnDefinitions.Add(new ColumnDefinition());
        var content = Rich(Turn.DisplayQuestion, new Thickness(0, 8, 12, -AiChatTypography.RichTextInset)); Grid.SetColumn(content, 1);
        _number.FontFamily = content.FontFamily;
        _number.Margin = new Thickness(-40, 8 + (content.FontFamily.Baseline - _number.FontFamily.Baseline) * AiChatTypography.FontSize, 10, 0);
        questionRow.Children.Add(_number); questionRow.Children.Add(content); Children.Add(questionRow);
        _ordinalAlignment = new ChatOrdinalAlignment(content, _number);
        Children.Add(_sentTime);
        _body.Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0); Children.Add(_body);
        _bodyBuilt = false;
        _footer = Footer(); Children.Add(_footer); ApplyCollapsed();
    }
    private void BuildBody()
    {
        _bodyBuilt = true;
        var segments = Turn.DisplaySegments;
        var remainingAnswers = segments.Count(segment => segment.Kind != "activity");
        var headings = new AiHeadingNumbers();
        foreach (var segment in segments)
        {
            if (segment.Kind == "activity")
            {
                _body.Children.Add(new AiActivityView { DataContext = segment });
            }
            else
            {
                if (segment.Kind == "final")
                {
                    var divider = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), IsHitTestVisible = false };
                    divider.SetResourceReference(Border.BackgroundProperty, "ChatDivider");
                    _body.Children.Add(divider);
                }
                var answer = Rich("", new Thickness(0, 2, 0, 0)); answer.Padding = new Thickness(0, 0, 34, 0); answer.DataContext = segment;
                answer.HeadingSeed = headings.Snapshot();
                answer.ChatMode = true; answer.TurnNumber = TurnNumber; answer.ImageBaseDirectory = ImageBaseDirectory;
                answer.ConfigureFolding(Turn.CollapsedSections, segment.Id, () => Changed?.Invoke());
                answer.Markdown = segment.Text;
                answer.Loaded += (_, _) => AnswerReady?.Invoke(answer); answer.Unloaded += (_, _) => AnswerGone?.Invoke(answer);
                Answers.Add(answer); _body.Children.Add(answer);
                if (--remainingAnswers > 0)
                {
                    if (answer.RenderTask.IsCompletedSuccessfully) headings = new AiHeadingNumbers(answer.HeadingCounts);
                    else foreach (var level in MarkdownView.HeadingLevels(segment.Text)) headings.Next(level);
                }
            }
        }
    }
    private MarkdownView Rich(string source, Thickness margin, bool numbers = false) => new() { SourceLink = source => SourceRequested?.Invoke(source), LinkListNumbers = numbers, UseWavyEmphasis = true, Markdown = source,
        Margin = new Thickness(margin.Left - AiChatTypography.RichTextInset, margin.Top, margin.Right, margin.Bottom), Padding = new Thickness(0) };
    private AiReplyStatus Footer()
    {
        var toggle = new AiReplyStatus { Text = Turn.AnswerTime, Margin = new Thickness(AiChatTypography.TextLeft, 0, 4, 0), Focusable = true, Cursor = Cursors.Hand };
        toggle.CanRetry = _canRetry; toggle.RetryRequested = () => RetryRequested?.Invoke();
        toggle.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Toggle(); e.Handled = true; } };
        toggle.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { if (!e.IsRepeat) Toggle(); e.Handled = true; } };
        return toggle;
    }
}
