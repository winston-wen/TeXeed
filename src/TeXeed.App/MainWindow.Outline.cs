using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private bool IsPreviewOnly => Preview.IsVisible && !Editors.Any(editor => editor.IsVisible);
    private async Task<bool> TryNavigateOutlinePreviewAsync(SourceLocation location)
    {
        if (!IsPreviewOnly || _project is null || _pdfPath is null || Preview.PageCount == 0 || _previewInputs is null ||
            _pdfRevision != _revision || _previewWarning || _changes.Count > 0 || _building || _externalChangePending || _transitioning || _closing)
            return false;
        var project = _previewProject ?? _project; var inputs = _previewInputs; var pdf = _pdfPath; var settings = _settings;
        var session = _session; var revision = _revision; var generation = _pdfGeneration; var inputVersion = inputs.ChangeVersion;
        _syncTimer.Stop(); CancelForwardSync(); var request = ++_navigationRequest;
        using var cancellation = new CancellationTokenSource(); _forwardSyncCancellation = cancellation;
        bool IsCurrent() => session == _session && revision == _revision && generation == _pdfGeneration && request == _navigationRequest &&
            ReferenceEquals(inputs, _previewInputs) && _pdfRevision == revision && !_previewWarning && _changes.Count == 0 &&
            !_building && !_externalChangePending && !_transitioning && !_closing && IsPreviewOnly;
        try
        {
            // A matching editor revision alone does not cover external assets or
            // macro-loaded dependencies. Reuse the displayed PDF's input snapshot.
            var current = await inputs.IsCurrentAsync(_projects, cancellation.Token);
            if (!IsCurrent()) return true; // A newer action owns navigation now.
            if (!current) return false;
            var point = await _sync.ForwardAsync(project, pdf, location, settings, cancellation.Token);
            if (!IsCurrent() || inputVersion != inputs.ChangeVersion) return true;
            if (point is null) return false;
            Preview.ShowLocation(point, highlight: true, viewportFraction: .35);
            if (_workspaceTabs.Owner(TabWorkspace.PreviewKey) is { } side) _workspaceTabs.Focus(side);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return true; }
        catch (Exception ex)
        {
            if (IsCurrent()) Status("定位失败：" + ex.Message);
            return true;
        }
        finally { if (ReferenceEquals(_forwardSyncCancellation, cancellation)) _forwardSyncCancellation = null; }
    }
}
