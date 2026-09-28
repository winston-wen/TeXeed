using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TeXeed.Core;

namespace TeXeed.App;
public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent(); Settings = settings;
        EngineBox.ItemsSource = new[] { new { Label = "自动选择", Value = TexEngine.Auto }, new { Label = "latexmk（自动管理交叉引用与文献）", Value = TexEngine.Latexmk }, new { Label = "XeLaTeX（推荐中文项目）", Value = TexEngine.XeLaTeX }, new { Label = "pdfLaTeX", Value = TexEngine.PdfLaTeX }, new { Label = "LuaLaTeX", Value = TexEngine.LuaLaTeX }, new { Label = "Tectonic", Value = TexEngine.Tectonic } };
        EngineBox.SelectedValue = settings.Engine;
        EnginePathBox.Text = settings.EnginePath; SynctexPathBox.Text = settings.SynctexPath;
        LiveBox.IsChecked = settings.LivePreview;
        FollowModeBox.ItemsSource = new[] { new { Label = "不跟随", Value = PreviewFollowMode.None }, new { Label = "跟随光标", Value = PreviewFollowMode.Cursor }, new { Label = "跟随视图", Value = PreviewFollowMode.Viewport } };
        FollowModeBox.SelectedValue = Enum.IsDefined(settings.FollowMode) ? settings.FollowMode : PreviewFollowMode.Viewport;
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
    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        ToolStatus.Text = "正在检测…";
        try { var tools = await CompileService.DetectToolsAsync(); ToolStatus.Text = tools.Engines.Count == 0 ? "未发现引擎。请安装 TeX Live / MiKTeX，或手动选择可执行文件。" : string.Join("、", tools.Engines.Select(t => t.Name)); ToolStatus.ToolTip = string.Join(Environment.NewLine, tools.Engines.Select(t => t.Path)); }
        catch (Exception ex) { ToolStatus.Text = ex.Message; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DelayBox.Text, out var delay) || delay < 400 || delay > 10000 || !double.TryParse(FontBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var font) || !double.IsFinite(font) || font < 10 || font > 30)
        { MessageBox.Show(this, "编译延迟须为 400–10000 毫秒，字号须为 10–30。", "检查设置"); return; }
        if (new[] { EnginePathBox.Text, SynctexPathBox.Text }.Any(p => !string.IsNullOrWhiteSpace(p) && !File.Exists(p.Trim())))
        { MessageBox.Show(this, "指定的可执行文件不存在。", "检查设置"); return; }
        var editorFont = EditorFontBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(editorFont)) editorFont = new AppSettings().EditorFontFamily;
        Settings = Settings with { Engine = (TexEngine)EngineBox.SelectedValue, EnginePath = EnginePathBox.Text.Trim(), SynctexPath = SynctexPathBox.Text.Trim(), LivePreview = LiveBox.IsChecked == true, FollowMode = FollowModeBox.SelectedValue is PreviewFollowMode mode ? mode : PreviewFollowMode.Viewport, DebounceMs = delay, FontSize = font, EditorFontFamily = editorFont };
        DialogResult = true;
    }
}
