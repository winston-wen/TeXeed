using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool _updatingZoomControls;
    private void InitializeZoomControls()
    {
        Preview.ZoomChanged += (_, _) => UpdateZoomControls();
        UpdateZoomControls();
    }
    private void UpdateZoomControls()
    {
        _updatingZoomControls = true;
        try
        {
            ZoomSlider.Maximum = Preview.MaximumZoom * 100;
            ZoomSlider.Value = Preview.Zoom * 100;
            ZoomButton.Content = (Preview.Zoom * 100).ToString("F2", CultureInfo.CurrentCulture) + "%";
            AutoWidthButton.IsChecked = Preview.IsAutoWidth;
        }
        finally { _updatingZoomControls = false; }
    }
    private void SetManualZoom(double zoom)
    {
        CancelPendingNavigation();
        Preview.SetZoom(zoom);
        UpdateZoomControls();
    }
    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingZoomControls || Preview is null || AutoWidthButton is null) return;
        SetManualZoom(e.NewValue / 100);
    }
    private void AutoWidth_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingNavigation();
        if (AutoWidthButton.IsChecked == true) Preview.FitWidth();
        else Preview.SetZoom(Preview.Zoom);
        UpdateZoomControls();
    }
    private void ZoomPercent_Click(object sender, RoutedEventArgs e)
    {
        ZoomInput.Text = (Preview.Zoom * 100).ToString("F2", CultureInfo.CurrentCulture);
        ZoomInput.ToolTip = $"输入 20–{ZoomSlider.Maximum:F0} 的百分比，Enter 确认，Esc 取消";
        ZoomButton.Visibility = Visibility.Collapsed;
        ZoomInput.Visibility = Visibility.Visible;
        ZoomInput.Focus(); ZoomInput.SelectAll();
    }
    private void ZoomInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseZoomInput(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (TryApplyZoomInput()) CloseZoomInput();
            else { ZoomInput.ToolTip = $"请输入 20–{ZoomSlider.Maximum:F0} 之间的百分比"; ZoomInput.SelectAll(); }
        }
    }
    private bool TryApplyZoomInput()
    {
        var text = ZoomInput.Text.Trim().TrimEnd('%', '％').Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (!double.IsFinite(value) || value < 20 || value > ZoomSlider.Maximum) return false;
        SetManualZoom(value / 100); return true;
    }
    private void ZoomInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ZoomInput.Visibility != Visibility.Visible) return;
        TryApplyZoomInput(); CloseZoomInput();
    }
    private void CloseZoomInput()
    {
        ZoomInput.Visibility = Visibility.Collapsed;
        ZoomButton.Visibility = Visibility.Visible;
    }
}
