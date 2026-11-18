using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TeXeed.App.Controls;
using TeXeed.Core;

namespace TeXeed.App;
public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    public PreviewOptions? PreviewSettings { get; private set; }
    private readonly TexProject? _project;
    private readonly PreviewOptions? _originalPreview;
    private readonly Action<PreviewOptions>? _applyPreview;
    private readonly PdfPreview? _preview;
    private bool _previewReady, _previewUpdating, _previewConfirmed;
    private bool _saving;
    public string? ProjectName { get; private set; }
    public SettingsWindow(AppSettings settings, TexProject? project = null, PreviewOptions? preview = null,
        Action<PreviewOptions>? applyPreview = null, PdfPreview? previewView = null)
    {
        InitializeComponent(); Settings = settings; _originalPreview = preview; _applyPreview = applyPreview; _preview = previewView;
        Services.WindowTheme.Attach(this);
        _project = project;
        ProjectName = project?.ConfiguredName;
        ProjectNameSection.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectNameBox.Text = ProjectName ?? "";
        ProjectNameHint.Text = project is null ? "" : $"留空使用主文件所在目录名：{new DirectoryInfo(project.Root).Name}";
        UseTitleButton.IsEnabled = !string.IsNullOrWhiteSpace(project?.DocumentTitle);
        UseTitleButton.ToolTip = UseTitleButton.IsEnabled ? project!.DocumentTitle : "主文件没有可用的 \\title";
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        EngineBox.ItemsSource = new[] { new { Label = "自动选择", Value = TexEngine.Auto }, new { Label = "latexmk（自动管理交叉引用与文献）", Value = TexEngine.Latexmk }, new { Label = "XeLaTeX（推荐中文项目）", Value = TexEngine.XeLaTeX }, new { Label = "pdfLaTeX", Value = TexEngine.PdfLaTeX }, new { Label = "LuaLaTeX", Value = TexEngine.LuaLaTeX }, new { Label = "Tectonic", Value = TexEngine.Tectonic } };
        EngineBox.SelectedValue = settings.Engine;
        EnginePathBox.Text = settings.EnginePath; SynctexPathBox.Text = settings.SynctexPath;
        LiveBox.IsChecked = settings.LivePreview;
        InvertPdfBox.IsChecked = settings.InvertPdfColors;
        AutoCropPdfBox.IsChecked = settings.AutoCropPdf;
        CompressWhitespaceBox.IsChecked = settings.CompressPdfWhitespace;
        FollowModeBox.ItemsSource = new[] { new { Label = "不跟随", Value = PreviewFollowMode.None }, new { Label = "跟随光标", Value = PreviewFollowMode.Cursor }, new { Label = "跟随视图", Value = PreviewFollowMode.Viewport } };
        FollowModeBox.SelectedValue = Enum.IsDefined(settings.FollowMode) ? settings.FollowMode : PreviewFollowMode.Viewport;
        PreviewSettings = preview;
        PreviewManualZoom.IsChecked = preview?.AutoWidth != true;
        PreviewAutoWidth.IsChecked = preview?.AutoWidth == true;
        PreviewZoomInput.Text = ((preview?.Zoom ?? 1) * 100).ToString("0.##", CultureInfo.CurrentCulture) + "%";
        PreviewMaximumWidth.Text = settings.AutoWidthMaximumCm.ToString("0.##", CultureInfo.CurrentCulture);
        PreviewPhysicalWidthHint.Text = previewView?.PhysicalWidthIsEstimated == true ? "显示器未提供物理尺寸，厘米值按系统 DPI 估算。" : "按当前显示器的物理尺寸限制预览宽度。";
        PreviewZoomInput.TextChanged += (_, _) => ApplyPreviewLive(); PreviewMaximumWidth.TextChanged += (_, _) => ApplyPreviewLive();
        PreviewManualZoom.Checked += (_, _) => ApplyPreviewLive(); PreviewAutoWidth.Checked += (_, _) => ApplyPreviewLive();
        InvertPdfBox.Checked += (_, _) => ApplyPreviewLive(); InvertPdfBox.Unchecked += (_, _) => ApplyPreviewLive();
        AutoCropPdfBox.Checked += (_, _) => ApplyPreviewLive(); AutoCropPdfBox.Unchecked += (_, _) =>
        {
            if (_previewReady && PreviewManualZoom.IsChecked == true && Number(PreviewZoomInput.Text.Trim().TrimEnd('%', '％'), out var value) && value > 300)
                PreviewZoomInput.Text = "300%";
            ApplyPreviewLive();
        };
        CompressWhitespaceBox.Checked += (_, _) => ApplyPreviewLive(); CompressWhitespaceBox.Unchecked += (_, _) => ApplyPreviewLive();
        FollowModeBox.SelectionChanged += (_, _) => ApplyPreviewLive();
        _previewReady = true;
        if (_preview is not null) _preview.ZoomChanged += PreviewZoomChanged;
        Closed += (_, _) =>
        {
            _previewReady = false;
            if (_preview is not null) _preview.ZoomChanged -= PreviewZoomChanged;
            if (!_previewConfirmed && _originalPreview is not null && PreviewSettings != _originalPreview) _applyPreview?.Invoke(_originalPreview);
        };
        EditorFontBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase).ToArray();
        EditorFontBox.Text = settings.EditorFontFamily;
        DelayBox.Text = settings.DebounceMs.ToString(CultureInfo.InvariantCulture); FontBox.Text = settings.FontSize.ToString(CultureInfo.InvariantCulture);
    }
    private string? Browse()
    {
        var dialog = new OpenFileDialog { Title = "选择可执行文件", Filter = "可执行文件 (*.exe)|*.exe", CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
    private void EngineBrowse_Click(object sender, RoutedEventArgs e) { if (Browse() is { } path) EnginePathBox.Text = path; }
    private void SynctexBrowse_Click(object sender, RoutedEventArgs e) { if (Browse() is { } path) SynctexPathBox.Text = path; }
    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        ToolStatus.Text = "正在检测…";
        try { var tools = CompileService.DetectTools(); ToolStatus.Text = tools.Engines.Count == 0 ? "未发现引擎。请安装 TeX Live / MiKTeX，或手动选择可执行文件。" : string.Join("、", tools.Engines.Select(t => t.Name)); ToolStatus.ToolTip = string.Join(Environment.NewLine, tools.Engines.Select(t => t.Path)); }
        catch (Exception ex) { ToolStatus.Text = ex.Message; }
    }
    private void UseTitle_Click(object sender, RoutedEventArgs e) => ProjectNameBox.Text = _project?.DocumentTitle ?? "";
    private static bool Number(string text, out double value) =>
        (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private void PreviewZoomChanged(object? sender, EventArgs e)
    {
        if (!_previewReady || _preview is null) return;
        PreviewPhysicalWidthHint.Text = _preview.PhysicalWidthIsEstimated ? "显示器未提供物理尺寸，厘米值按系统 DPI 估算。" : "按当前显示器的物理尺寸限制预览宽度。";
        if (PreviewAutoWidth.IsChecked != true) return;
        _previewUpdating = true;
        try { PreviewZoomInput.Text = (_preview.Zoom * 100).ToString("0.##", CultureInfo.CurrentCulture) + "%"; }
        finally { _previewUpdating = false; }
    }
    private void ApplyPreviewLive()
    {
        if (!_previewReady || _previewUpdating || PreviewSettings is null || _applyPreview is null) return;
        var validWidth = Number(PreviewMaximumWidth.Text, out var maximum) && maximum is >= 1 and <= 200;
        if (!validWidth && PreviewAutoWidth.IsChecked == true) return;
        if (!validWidth) maximum = PreviewSettings.MaximumWidthCm;
        var zoom = _preview?.Zoom ?? PreviewSettings.Zoom;
        if (PreviewManualZoom.IsChecked == true)
        {
            if (!Number(PreviewZoomInput.Text.Trim().TrimEnd('%', '％'), out var percent) || percent < 20 || percent > (AutoCropPdfBox.IsChecked == true ? 800 : 300)) return;
            zoom = percent / 100;
        }
        var options = new PreviewOptions(zoom, PreviewAutoWidth.IsChecked == true,
            FollowModeBox.SelectedValue is PreviewFollowMode mode ? mode : PreviewFollowMode.Viewport,
            InvertPdfBox.IsChecked == true, AutoCropPdfBox.IsChecked == true, CompressWhitespaceBox.IsChecked == true, maximum);
        if (options != PreviewSettings) { PreviewSettings = options; _applyPreview(options); }
    }
    private void StepPreviewZoom(double factor)
    {
        var zoom = _preview?.Zoom ?? PreviewSettings?.Zoom ?? 1;
        _previewUpdating = true;
        try { PreviewManualZoom.IsChecked = true; PreviewZoomInput.Text = (Math.Clamp(zoom * factor, .2, AutoCropPdfBox.IsChecked == true ? 8 : 3) * 100).ToString("0.##", CultureInfo.CurrentCulture) + "%"; }
        finally { _previewUpdating = false; }
        ApplyPreviewLive();
    }
    private void PreviewZoomOut_Click(object sender, RoutedEventArgs e) => StepPreviewZoom(1 / 1.15);
    private void PreviewZoomIn_Click(object sender, RoutedEventArgs e) => StepPreviewZoom(1.15);
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DelayBox.Text, out var delay) || delay < 400 || delay > 10000 || !double.TryParse(FontBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var font) || !double.IsFinite(font) || font < 10 || font > 30)
        { MessageBox.Show(this, "编译延迟须为 400–10000 毫秒，字号须为 10–30。", "检查设置"); return; }
        if (new[] { EnginePathBox.Text, SynctexPathBox.Text }.Any(p => !string.IsNullOrWhiteSpace(p) && !File.Exists(p.Trim())))
        { MessageBox.Show(this, "指定的可执行文件不存在。", "检查设置"); return; }
        var validWidth = Number(PreviewMaximumWidth.Text, out var maximumWidth) && maximumWidth is >= 1 and <= 200;
        if (!validWidth && PreviewAutoWidth.IsChecked == true)
        { MessageBox.Show(this, "自动宽度上限须为 1–200 cm。", "检查设置"); return; }
        if (!validWidth) maximumWidth = PreviewSettings?.MaximumWidthCm ?? Settings.AutoWidthMaximumCm;
        var zoom = PreviewSettings?.Zoom ?? 1;
        if (PreviewManualZoom.IsChecked == true && (!Number(PreviewZoomInput.Text.Trim().TrimEnd('%', '％'), out var percent) ||
            percent < 20 || percent > (AutoCropPdfBox.IsChecked == true ? 800 : 300)))
        { MessageBox.Show(this, $"缩放倍率须为 20–{(AutoCropPdfBox.IsChecked == true ? 800 : 300)}%。", "检查设置"); return; }
        if (PreviewManualZoom.IsChecked == true && Number(PreviewZoomInput.Text.Trim().TrimEnd('%', '％'), out var chosenPercent)) zoom = chosenPercent / 100;
        var editorFont = EditorFontBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(editorFont)) editorFont = new AppSettings().EditorFontFamily;
        if (_project is not null && ProjectNameBox.Text.Trim() != (_project.ConfiguredName ?? ""))
        {
            _saving = true; IsEnabled = false;
            try { ProjectName = (await new ProjectService().SetNameAsync(_project, ProjectNameBox.Text)).ConfiguredName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "无法保存项目名称"); return; }
            finally { _saving = false; IsEnabled = true; }
        }
        Settings = Settings with { Engine = (TexEngine)EngineBox.SelectedValue, EnginePath = EnginePathBox.Text.Trim(), SynctexPath = SynctexPathBox.Text.Trim(), LivePreview = LiveBox.IsChecked == true, FollowMode = FollowModeBox.SelectedValue is PreviewFollowMode mode ? mode : PreviewFollowMode.Viewport, DebounceMs = delay, FontSize = font, EditorFontFamily = editorFont };
        Settings = Settings with { InvertPdfColors = InvertPdfBox.IsChecked == true, AutoCropPdf = AutoCropPdfBox.IsChecked == true,
            CompressPdfWhitespace = CompressWhitespaceBox.IsChecked == true, AutoWidthMaximumCm = maximumWidth };
        if (PreviewSettings is not null) PreviewSettings = new PreviewOptions(zoom, PreviewAutoWidth.IsChecked == true, Settings.FollowMode,
            Settings.InvertPdfColors, Settings.AutoCropPdf, Settings.CompressPdfWhitespace, maximumWidth);
        _previewConfirmed = true;
        DialogResult = true;
    }
}
