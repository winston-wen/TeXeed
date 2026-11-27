using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Globalization;
using System.ComponentModel;

namespace TeXeed.App.Controls;

/// <summary>One stable footer for live and persisted replies.</summary>
public sealed class AiReplyStatus : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("", (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty IsThinkingProperty = DependencyProperty.Register(nameof(IsThinking), typeof(bool), typeof(AiReplyStatus), new PropertyMetadata(false, (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty PendingTextProperty = DependencyProperty.Register(nameof(PendingText), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("等待 AI 响应", (d, _) => ((AiReplyStatus)d).Refresh()));
    public string PendingText { get => (string)GetValue(PendingTextProperty); set => SetValue(PendingTextProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsThinking { get => (bool)GetValue(IsThinkingProperty); set => SetValue(IsThinkingProperty, value); }
    private readonly TextBlock _stamp = new() { FontSize = AiChatTypography.MetadataFontSize, TextWrapping = TextWrapping.Wrap,
        LineHeight = AiChatTypography.ExpandedLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    private readonly Ellipse _indicator = new() { Width = AiChatTypography.MetadataFontSize, Height = AiChatTypography.MetadataFontSize, Margin = new Thickness(7, 0, 0, 0), IsHitTestVisible = false };
    private readonly InlineUIContainer _indicatorInline;
    private bool _pulsing;
    private readonly Func<bool> _animationsEnabled;
    private DispatcherTimer? _blinkTimer;
    private Button? _retry;
    private Button? _confirm, _end;
    private readonly WrapPanel _row = new();
    internal Action? RetryRequested;
    internal Action? ConfirmRequested, EndRequested;
    internal void SetInputActions(bool show, bool canConfirm, bool canEnd)
    {
        if (!show && _confirm is null) return;
        if (_confirm is null)
        {
            Button ActionButton(string text, Action activate)
            {
                var button = new Button { Content = text, FontSize = 15, MinHeight = 28,
                    Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(9, 2, 9, 2),
                    VerticalAlignment = VerticalAlignment.Center };
                button.Click += (_, e) => { e.Handled = true; activate(); };
                _row.Children.Add(button);
                return button;
            }
            _confirm = ActionButton("确认并继续", () => ConfirmRequested?.Invoke());
            _confirm.SetResourceReference(Control.ForegroundProperty, "ChatHeading");
            _confirm.SetResourceReference(Control.BorderBrushProperty, "ChatHeading");
            _stamp.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetShowOnDisabled(_confirm, true);
            _end = ActionButton("结束本轮", () => EndRequested?.Invoke());
            _end.BorderThickness = new Thickness(0);
            _end.Background = Brushes.Transparent;
            _end.SetResourceReference(Control.ForegroundProperty, "Muted");
        }
        _confirm.Visibility = _end!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _confirm.IsEnabled = canConfirm;
        _confirm.ToolTip = canConfirm ? "提交这道题的答案，在本轮继续" : "请先填写或选择答案";
        _end.IsEnabled = canEnd;
    }
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
    public AiReplyStatus() : this(() => SystemParameters.ClientAreaAnimation) { }
    internal AiReplyStatus(Func<bool> animationsEnabled)
    {
        _animationsEnabled = animationsEnabled;
        Background = Brushes.Transparent;
        _indicatorInline = new InlineUIContainer(_indicator) { BaselineAlignment = BaselineAlignment.Baseline };
        _stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking");
        var line = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0), IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "ChatDivider");
        _row.Children.Add(_stamp);
        Children.Add(_row); Children.Add(line);
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += OnSystemParameterChanged; AlignIndicator(); UpdatePulse(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged; StopPulse(); };
        IsVisibleChanged += (_, _) => UpdatePulse();
    }
    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) { StopPulse(); UpdatePulse(); }
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
        if (!IsThinking || !IsLoaded || !IsVisible) { StopPulse(); return; }
        if (_pulsing) return;
        _pulsing = true;
        if (_animationsEnabled())
            _indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromSeconds(.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else
        {
            _indicator.SetResourceReference(Shape.StrokeProperty, "ChatThinking");
            _indicator.StrokeThickness = 1.5;
            _blinkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _blinkTimer.Tick += Blink;
            _blinkTimer.Start();
        }
    }
    private void Blink(object? sender, EventArgs e)
    {
        if (_indicator.Fill == Brushes.Transparent) _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking");
        else _indicator.Fill = Brushes.Transparent;
    }
    private void StopPulse()
    {
        if (_blinkTimer is not null) { _blinkTimer.Stop(); _blinkTimer.Tick -= Blink; _blinkTimer = null; }
        _indicator.BeginAnimation(OpacityProperty, null); _indicator.Opacity = 1;
        _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking"); _indicator.StrokeThickness = 0; _pulsing = false;
    }
}
