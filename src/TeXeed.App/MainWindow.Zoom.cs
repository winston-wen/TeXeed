using System.Windows;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private void SetManualZoom(double zoom) { CancelPendingNavigation(); Preview.SetZoom(zoom); }
    private PreviewOptions CurrentPreviewOptions => new(Preview.Zoom, Preview.IsAutoWidth, _settings.FollowMode, Preview.InvertColors, Preview.AutoCrop);
    private void ApplyPreviewOptions(PreviewOptions options)
    {
        CancelPendingNavigation();
        _settings = _settings with { FollowMode = options.Follow, InvertPdfColors = options.Invert, AutoCropPdf = options.Crop };
        Preview.InvertColors = options.Invert; Preview.AutoCrop = options.Crop;
        if (options.AutoWidth) Preview.FitWidth(); else Preview.SetZoom(options.Zoom);
        PersistSettings(); QueueFollowSync();
    }
    private void ChangeFollowMode(PreviewFollowMode mode) => ApplyPreviewOptions(CurrentPreviewOptions with { Follow = mode });
    private PreviewSettingsWindow CreatePreviewSettings() => new(CurrentPreviewOptions, ApplyPreviewOptions) { Owner = this };
    private void OpenPreviewSettings()
    {
        if (_closing || _transitioning) return;
        CreatePreviewSettings().ShowDialog();
    }
    private void AutoWidth_Click(object sender, RoutedEventArgs e) { CancelPendingNavigation(); Preview.FitWidth(); }
}
