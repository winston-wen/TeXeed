using System.Windows;
using TeXeed.App.Controls;
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
    private void ApplyPreviewOptions(PreviewOptions options) => ApplyPreviewOptionsTo(Preview, options);
    private void ApplyPreviewOptionsTo(PdfPreview target, PreviewOptions options)
    {
        CancelPendingNavigation();
        if (_settings.FollowMode != options.Follow) { _previewUserOwnsView = false; _userNavigationSequence++; }
        _settings = _settings with { FollowMode = options.Follow, InvertPdfColors = options.Invert, AutoCropPdf = options.Crop,
            CompressPdfWhitespace = options.CompressWhitespace, AutoWidthMaximumCm = options.MaximumWidthCm };
        Preview.InvertColors = options.Invert; Preview.SetCropOptions(options.Crop, options.CompressWhitespace);
        Preview.AutoWidthMaximumCm = options.MaximumWidthCm;
        foreach (var preview in _aiPreviews.Values)
        {
            preview.SetSettings(_settings);
        }
        if (options.AutoWidth) target.FitWidth(); else target.SetZoom(options.Zoom);
        if (ReferenceEquals(target, Preview)) UpdatePendingPreviewZoom();
        PersistSettings(); QueueFollowSync();
    }
    private void ChangeFollowMode(PreviewFollowMode mode) => ApplyPreviewOptions(CurrentPreviewOptions with { Follow = mode });
    private PreviewSettingsWindow CreatePreviewSettings() => new(CurrentPreviewOptions, ApplyPreviewOptions, Preview) { Owner = this };
    private PreviewSettingsWindow CreateAiPreviewSettings(PdfPreview preview) => new(new PreviewOptions(preview.Zoom, preview.IsAutoWidth, _settings.FollowMode,
        preview.InvertColors, preview.AutoCrop, preview.CompressVerticalWhitespace, preview.AutoWidthMaximumCm), options => ApplyPreviewOptionsTo(preview, options), preview) { Owner = this };
    private void OpenAiPreviewSettings(PdfPreview preview)
    {
        if (_closing || _transitioning) return;
        CreateAiPreviewSettings(preview).ShowDialog();
    }
    private void OpenPreviewSettings()
    {
        if (_closing || _transitioning) return;
        CreatePreviewSettings().ShowDialog();
    }
    private void AutoWidth_Click(object sender, RoutedEventArgs e) { CancelPendingNavigation(); Preview.FitWidth(); UpdatePendingPreviewZoom(); }
}
