using System.Windows;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private void SetManualZoom(double zoom) { CancelPendingNavigation(); Preview.SetZoom(zoom); UpdatePendingPreviewZoom(); }
    private void UpdatePendingPreviewZoom()
    {
        if (_pendingPreviewPosition is { } position)
            _pendingPreviewPosition = position with { FitWidth = Preview.IsAutoWidth, Zoom = Preview.Zoom };
    }
    private PreviewOptions CurrentPreviewOptions => new(Preview.Zoom, Preview.IsAutoWidth, _settings.FollowMode,
        Preview.InvertColors, Preview.AutoCrop, Preview.CompressVerticalWhitespace, Preview.AutoWidthMaximumCm);
    private void ApplyPreviewOptions(PreviewOptions options)
    {
        CancelPendingNavigation();
        _settings = _settings with { FollowMode = options.Follow, InvertPdfColors = options.Invert, AutoCropPdf = options.Crop,
            CompressPdfWhitespace = options.CompressWhitespace, AutoWidthMaximumCm = options.MaximumWidthCm };
        Preview.InvertColors = options.Invert; Preview.SetCropOptions(options.Crop, options.CompressWhitespace);
        Preview.AutoWidthMaximumCm = options.MaximumWidthCm;
        if (options.AutoWidth) Preview.FitWidth(); else Preview.SetZoom(options.Zoom);
        UpdatePendingPreviewZoom();
        PersistSettings(); QueueFollowSync();
    }
    private void ChangeFollowMode(PreviewFollowMode mode) => ApplyPreviewOptions(CurrentPreviewOptions with { Follow = mode });
    private PreviewSettingsWindow CreatePreviewSettings() => new(CurrentPreviewOptions, ApplyPreviewOptions, Preview) { Owner = this };
    private void OpenPreviewSettings()
    {
        if (_closing || _transitioning) return;
        CreatePreviewSettings().ShowDialog();
    }
    private void AutoWidth_Click(object sender, RoutedEventArgs e) { CancelPendingNavigation(); Preview.FitWidth(); UpdatePendingPreviewZoom(); }
}
