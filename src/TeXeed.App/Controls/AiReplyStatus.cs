using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TeXeed.App.Controls;

/// <summary>One stable footer for live and persisted replies; only dot opacity animates.</summary>
public sealed class AiReplyStatus : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(AiReplyStatus), new PropertyMetadata("", (d, _) => ((AiReplyStatus)d).Refresh()));
    public static readonly DependencyProperty IsThinkingProperty = DependencyProperty.Register(nameof(IsThinking), typeof(bool), typeof(AiReplyStatus), new PropertyMetadata(false, (d, _) => ((AiReplyStatus)d).Refresh()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsThinking { get => (bool)GetValue(IsThinkingProperty); set => SetValue(IsThinkingProperty, value); }
    private readonly TextBlock _stamp = new() { FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Ellipse _indicator = new() { Width = 7, Height = 7, Margin = new Thickness(7, 0, 0, 0), IsHitTestVisible = false };
    private readonly InlineUIContainer _indicatorInline;
    private bool _pulsing;
    public AiReplyStatus()
    {
        Background = Brushes.Transparent;
        _indicatorInline = new InlineUIContainer(_indicator) { BaselineAlignment = BaselineAlignment.Center };
        _stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _indicator.SetResourceReference(Shape.FillProperty, "ChatThinking");
        var line = new Line { X2 = 1, Stretch = Stretch.Fill, StrokeThickness = 1, StrokeDashArray = [1, 2], Margin = new Thickness(0, 7, 0, 6), IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, "ChatDivider");
        Children.Add(_stamp); Children.Add(line);
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
        IsVisibleChanged += (_, _) => UpdatePulse();
    }
    private void Refresh()
    {
        _stamp.Inlines.Clear();
        if (IsThinking)
        {
            _stamp.Inlines.Add(new Run("思考中"));
            _stamp.Inlines.Add(_indicatorInline);
        }
        else _stamp.Text = Text;
        System.Windows.Automation.AutomationProperties.SetName(this, IsThinking ? "思考中" : Text);
        UpdatePulse();
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
