using System.Globalization;
using System.Windows;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App;

public sealed record PreviewOptions(double Zoom, bool AutoWidth, PreviewFollowMode Follow, bool Invert, bool Crop);
public partial class PreviewSettingsWindow : Window
{
    private readonly Action<PreviewOptions> _apply;
    public PreviewSettingsWindow(PreviewOptions options, Action<PreviewOptions> apply)
    {
        InitializeComponent(); _apply = apply; WindowTheme.Attach(this);
        ZoomInput.Text = (options.Zoom * 100).ToString("F2", CultureInfo.CurrentCulture) + "%";
        AutoWidthBox.IsChecked = options.AutoWidth; ManualZoom.IsChecked = !options.AutoWidth;
        InvertPdfBox.IsChecked = options.Invert; AutoCropPdfBox.IsChecked = options.Crop;
        FollowModeBox.ItemsSource = new[] { new { Label = "不跟随", Value = PreviewFollowMode.None },
            new { Label = "跟随光标", Value = PreviewFollowMode.Cursor }, new { Label = "跟随视图", Value = PreviewFollowMode.Viewport } };
        FollowModeBox.SelectedValue = options.Follow;
    }
    private bool TryValue(out double zoom)
    {
        var text = ZoomInput.Text.Trim().TrimEnd('%', '％').Trim();
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        var maximum = AutoCropPdfBox.IsChecked == true ? 800 : 300;
        zoom = value / 100;
        return valid && double.IsFinite(value) && value >= 20 && value <= maximum;
    }
    private bool Apply()
    {
        var valid = TryValue(out var zoom);
        if (!valid && AutoWidthBox.IsChecked != true) { ValidationHint.Text = $"请输入 20–{(AutoCropPdfBox.IsChecked == true ? 800 : 300)}% 的有效倍率"; return false; }
        if (AutoWidthBox.IsChecked == true) zoom = 1;
        _apply(new PreviewOptions(zoom, AutoWidthBox.IsChecked == true, (PreviewFollowMode)(FollowModeBox.SelectedValue ?? PreviewFollowMode.Viewport), InvertPdfBox.IsChecked == true, AutoCropPdfBox.IsChecked == true));
        ValidationHint.Text = "已应用"; return true;
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();
    private void Confirm_Click(object sender, RoutedEventArgs e) { if (Apply()) DialogResult = true; }
    private void Step(double factor)
    { if (TryValue(out var zoom)) { ManualZoom.IsChecked = true; ZoomInput.Text = (Math.Clamp(zoom * factor, .2, AutoCropPdfBox.IsChecked == true ? 8 : 3) * 100).ToString("F2", CultureInfo.CurrentCulture) + "%"; } }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Step(1 / 1.15);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Step(1.15);
}
