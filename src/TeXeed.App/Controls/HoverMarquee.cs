using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

/// <summary>Literal single-line text, ellipsized at rest and revealed while hovered.</summary>
public sealed class HoverMarquee : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoverMarquee),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HoverMarquee)d).Reset()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private long _last, _pause;
    private double _offset;
    private int _direction = 1;
    internal double Offset => _offset;
    internal bool IsScrolling => _timer.IsEnabled;
    private FormattedText Label() => new(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    public HoverMarquee()
    {
        ClipToBounds = true; Focusable = false;
        MouseEnter += (_, _) => Start(); MouseLeave += (_, _) => Reset(); Unloaded += (_, _) => Reset();
        SizeChanged += (_, _) => { Reset(); if (IsMouseOver) Start(); };
        _timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            var elapsed = Math.Clamp((now - _last) / 1000d, 0, .1); _last = now;
            if (now < _pause) return;
            var end = Math.Max(0, Label().WidthIncludingTrailingWhitespace - ActualWidth);
            _offset = Math.Clamp(_offset + _direction * 40 * elapsed, 0, end);
            if (_offset == end || _offset == 0) { _direction *= -1; _pause = now + 700; }
            InvalidateVisual();
        };
    }
    private void Start()
    {
        if (Label().WidthIncludingTrailingWhitespace <= ActualWidth + .5) return;
        _last = Environment.TickCount64; _pause = _last + 450; _direction = 1; _timer.Start(); InvalidateVisual();
    }
    private void Reset() { _timer.Stop(); _offset = 0; InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Label(); return new(Math.Min(label.WidthIncludingTrailingWhitespace, availableSize.Width), label.Height);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 0) return;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var label = Label();
        if (!_timer.IsEnabled) { label.MaxTextWidth = ActualWidth; label.Trimming = TextTrimming.CharacterEllipsis; }
        drawing.DrawText(label, new Point(-_offset, Math.Max(0, (ActualHeight - label.Height) / 2)));
    }
}
