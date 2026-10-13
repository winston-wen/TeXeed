using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App;

public sealed record PreviewOptions(double Zoom, bool AutoWidth, PreviewFollowMode Follow, bool Invert, bool Crop,
    bool CompressWhitespace = true, double MaximumWidthCm = 30);

public partial class PreviewSettingsWindow : Window
{
    private readonly Action<PreviewOptions> _apply;
    private readonly PreviewOptions _original;
    private readonly PdfPreview? _preview;
    private PreviewOptions _lastApplied;
    private bool _ready, _updating, _confirmed;

    public PreviewSettingsWindow(PreviewOptions options, Action<PreviewOptions> apply, PdfPreview? preview = null)
    {
        _apply = apply; _original = options; _preview = preview;
        _lastApplied = options.AutoWidth ? options with { Zoom = 1 } : options;
        InitializeComponent(); WindowTheme.Attach(this);
        SetZoomText(options.Zoom);
        MaximumWidthInput.Text = options.MaximumWidthCm.ToString("0.##", CultureInfo.CurrentCulture);
        AutoWidthBox.IsChecked = options.AutoWidth; ManualZoom.IsChecked = !options.AutoWidth;
        InvertPdfBox.IsChecked = options.Invert; AutoCropPdfBox.IsChecked = options.Crop;
        CompressWhitespaceBox.IsChecked = options.CompressWhitespace;
        FollowModeBox.ItemsSource = new[] { new { Label = "不跟随", Value = PreviewFollowMode.None },
            new { Label = "跟随光标", Value = PreviewFollowMode.Cursor }, new { Label = "跟随视图", Value = PreviewFollowMode.Viewport } };
        FollowModeBox.SelectedValue = options.Follow;
        _ready = true;
        UpdatePhysicalHint();
        if (_preview is not null) _preview.ZoomChanged += PreviewZoomChanged;
        Closed += (_, _) =>
        {
            _ready = false;
            if (_preview is not null) _preview.ZoomChanged -= PreviewZoomChanged;
            if (!_confirmed && _lastApplied != (options.AutoWidth ? options with { Zoom = 1 } : options)) _apply(_original);
        };
    }

    private static bool TryNumber(string text, out double value) =>
        (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private bool TryZoom(out double zoom)
    {
        var valid = TryNumber(ZoomInput.Text.Trim().TrimEnd('%', '％'), out var value);
        zoom = value / 100;
        return valid && value >= 20 && value <= (AutoCropPdfBox.IsChecked == true ? 800 : 300);
    }
    private void SetZoomText(double zoom)
    {
        _updating = true;
        ZoomInput.Text = (zoom * 100).ToString("F2", CultureInfo.CurrentCulture) + "%";
        _updating = false;
    }
    private void PreviewZoomChanged(object? sender, EventArgs e)
    {
        if (!_ready || _preview is null) return;
        if (AutoWidthBox.IsChecked == true) SetZoomText(_preview.Zoom);
        UpdatePhysicalHint();
    }
    private void UpdatePhysicalHint() => PhysicalWidthHint.Text = _preview?.PhysicalWidthIsEstimated == true
        ? "显示器未提供物理尺寸，厘米值按系统 DPI 估算。" : "按当前显示器的物理尺寸限制预览宽度。";

    private bool ApplyLive()
    {
        if (!_ready || _updating) return false;
        var autoWidth = AutoWidthBox.IsChecked == true;
        var validZoom = TryZoom(out var zoom);
        var validWidth = TryNumber(MaximumWidthInput.Text, out var maximum) && maximum is >= 1 and <= 200;
        if (!autoWidth && !validZoom) return Invalid($"请输入 20–{(AutoCropPdfBox.IsChecked == true ? 800 : 300)}% 的有效倍率");
        if (autoWidth && !validWidth) return Invalid("最大宽度须为 1–200 cm");
        // A disabled field must not block unrelated settings while the user changes modes.
        var options = new PreviewOptions(autoWidth ? 1 : zoom, autoWidth,
            (PreviewFollowMode)(FollowModeBox.SelectedValue ?? PreviewFollowMode.Viewport),
            InvertPdfBox.IsChecked == true, AutoCropPdfBox.IsChecked == true, CompressWhitespaceBox.IsChecked == true,
            validWidth ? maximum : _lastApplied.MaximumWidthCm);
        ValidationHint.Visibility = Visibility.Collapsed;
        if (options != _lastApplied) { _lastApplied = options; _apply(options); }
        return true;
    }
    private bool Invalid(string hint) { ValidationHint.Text = hint; ValidationHint.Visibility = Visibility.Visible; return false; }
    private void Input_Changed(object sender, TextChangedEventArgs e) => ApplyLive();
    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && !_updating && ReferenceEquals(sender, AutoCropPdfBox) && AutoCropPdfBox.IsChecked != true
            && ManualZoom.IsChecked == true && TryNumber(ZoomInput.Text.Trim().TrimEnd('%', '％'), out var percent) && percent > 300)
            SetZoomText(3);
        ApplyLive();
    }
    private void Follow_Changed(object sender, SelectionChangedEventArgs e) => ApplyLive();
    private void Confirm_Click(object sender, RoutedEventArgs e) { if (ApplyLive()) { _confirmed = true; Close(); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Step(double factor)
    {
        var zoom = _preview?.Zoom ?? (TryZoom(out var value) ? value : _lastApplied.Zoom);
        _updating = true; ManualZoom.IsChecked = true; _updating = false;
        SetZoomText(Math.Clamp(zoom * factor, .2, AutoCropPdfBox.IsChecked == true ? 8 : 3));
        ApplyLive();
    }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Step(1 / 1.15);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Step(1.15);
}
