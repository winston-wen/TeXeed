using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Globalization;

namespace TeXeed.App.Controls;

/// <summary>One stable footer for live and persisted replies; only dot opacity animates.</summary>
public sealed class AiReplyStatus : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("", (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty IsThinkingProperty = DependencyProperty.Register(nameof(IsThinking), typeof(bool), typeof(AiReplyStatus), new PropertyMetadata(false, (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty PendingTextProperty = DependencyProperty.Register(nameof(PendingText), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("等待 AI 响应", (d, _) => ((AiReplyStatus)d).Refresh()));
    public string PendingText { get => (string)GetValue(PendingTextProperty); set => SetValue(PendingTextProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsThinking { get => (bool)GetValue(IsThinkingProperty); set => SetValue(IsThinkingProperty, value); }
    private readonly TextBlock _stamp = new() { FontSize = AiChatTypography.MetadataFontSize, TextWrapping = TextWrapping.Wrap };
    private readonly Ellipse _indicator = new() { Width = AiChatTypography.MetadataFontSize, Height = AiChatTypography.MetadataFontSize, Margin = new Thickness(7, 0, 0, 0), IsHitTestVisible = false };
    private readonly InlineUIContainer _indicatorInline;
    private bool _pulsing;
    private Button? _retry;
    private readonly WrapPanel _row = new();
    internal Action? RetryRequested;
    internal bool CanRetry
    {
        get => _retry?.Visibility == Visibility.Visible;
        set
        {
            if (value && _retry is null)
            {
                _retry = new Button { Content = "↻  重试", FontSize = AiChatTypography.MetadataFontSize, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
                _retry.SetResourceReference(Control.ForegroundProperty, "ChatHeading");
                System.Windows.Automation.AutomationProperties.SetName(_retry, "重试本轮");
                _retry.Click += (_, e) => { e.Handled = true; RetryRequested?.Invoke(); };
                _row.Children.Add(_retry);
            }
            if (_retry is not null) _retry.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    internal bool IsCollapsed
    {
        set { _stamp.LineHeight = value ? double.NaN : AiChatTypography.ExpandedLineHeight; _stamp.LineStackingStrategy = value ? LineStackingStrategy.MaxHeight : LineStackingStrategy.BlockLineHeight; }
    }
    public AiReplyStatus()
    {
        Background = Brushes.Transparent;
        IsCollapsed = false;
        _indicatorInline = new InlineUIContainer(_indicator) { BaselineAlignment = BaselineAlignment.Baseline };
        _stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking");
        var line = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0), IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "ChatDivider");
        _row.Children.Add(_stamp);
        Children.Add(_row); Children.Add(line);
        Loaded += (_, _) => { AlignIndicator(); UpdatePulse(); };
        Unloaded += (_, _) => StopPulse();
        IsVisibleChanged += (_, _) => UpdatePulse();
    }
    private void Refresh()
    {
        _stamp.Inlines.Clear();
        if (IsThinking)
        {
            _stamp.Inlines.Add(new Run(PendingText));
            _stamp.Inlines.Add(_indicatorInline);
            AlignIndicator();
        }
        else _stamp.Text = Text;
        System.Windows.Automation.AutomationProperties.SetName(this, IsThinking ? PendingText : Text);
        UpdatePulse();
    }
    private void AlignIndicator()
    {
        if (!IsThinking) return;
        var text = new FormattedText(PendingText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_stamp.FontFamily, _stamp.FontStyle, _stamp.FontWeight, _stamp.FontStretch), _stamp.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var axis = text.Height + text.OverhangAfter - text.Extent / 2 - text.Baseline;
        TextBlock.SetBaselineOffset(_indicator, _indicator.Height / 2 - axis);
    }
    private void UpdatePulse()
    {
        if (!IsThinking || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) { StopPulse(); return; }
        if (_pulsing) return;
        _pulsing = true;
        _indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromSeconds(.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void StopPulse()
    {
        _indicator.BeginAnimation(OpacityProperty, null); _indicator.Opacity = 1; _pulsing = false;
    }
}
