using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TeXeed.App.Services;

namespace TeXeed.App;

internal static class PdfPrintDialogs
{
    internal static void AttachCaption(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongPtr(hwnd, -16, GetWindowLongPtr(hwnd, -16) & ~(nint)0x30000); // native caption: no min/max buttons
        SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 1); // dialog frame, no caption icon
        SendMessage(hwnd, 0x80, 0, 0); SendMessage(hwnd, 0x80, 1, 0);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37); // refresh non-client frame, do not activate or move
    };

    private static (Window Window, StackPanel Fields, Button Accept) Create(Window owner, string title, double width = 480)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 32, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        WindowTheme.Attach(dialog); AttachCaption(dialog);
        var root = new DockPanel { Margin = new Thickness(20) }; dialog.Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => dialog.DialogResult = false;
        var accept = new Button { Content = "确定", IsDefault = true }; accept.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); actions.Children.Add(cancel); actions.Children.Add(accept);
        var fields = new StackPanel(); var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        return (dialog, fields, accept);
    }
    private static void Row(Panel parent, string title, FrameworkElement field)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock { Text = title, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        field.MinWidth = 110; row.Children.Add(field); parent.Children.Add(row);
    }
    private static ComboBox Choices(params string[] values)
    { var box = new ComboBox { ItemsSource = values, SelectedIndex = 0 }; return box; }
    private static TextBox Input(double value, string label)
    { var box = new TextBox { Text = value.ToString("0.##") }; System.Windows.Automation.AutomationProperties.SetName(box, label); return box; }
    private static double Read(TextBox input, string title, double min, double max)
    {
        if (!double.TryParse(input.Text, out var value) || !double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"{title}应为 {min}–{max} mm。");
        return value;
    }

    internal static (Size Paper, Thickness? Margins, int Orientation)? PageSetup(Window owner, Size paper, Thickness? margins, int orientation, ImageSource? image)
    {
        var (dialog, fields, accept) = Create(owner, "页面设置", 520);
        var thumbnail = new Image { Source = image, Width = 135, Height = 180, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 16) };
        fields.Children.Add(thumbnail);
        var sizes = Choices("A4 · 210 × 297 mm", "A3 · 297 × 420 mm", "Letter · 215.9 × 279.4 mm", "自定义");
        var mmW = Math.Min(paper.Width, paper.Height) * 25.4 / 96; var mmH = Math.Max(paper.Width, paper.Height) * 25.4 / 96;
        sizes.SelectedIndex = Math.Abs(mmW - 210) < 1 && Math.Abs(mmH - 297) < 1 ? 0 : Math.Abs(mmW - 297) < 1 && Math.Abs(mmH - 420) < 1 ? 1 : Math.Abs(mmW - 215.9) < 1 && Math.Abs(mmH - 279.4) < 1 ? 2 : 3;
        Row(fields, "纸张", sizes);
        var width = Input(mmW, "纸张宽度 mm"); var height = Input(mmH, "纸张高度 mm"); Row(fields, "宽度 (mm)", width); Row(fields, "高度 (mm)", height);
        void EnableDimensions() { width.IsEnabled = height.IsEnabled = sizes.SelectedIndex == 3; }
        sizes.SelectionChanged += (_, _) =>
        {
            var dims = sizes.SelectedIndex switch { 0 => (210d, 297d), 1 => (297d, 420d), 2 => (215.9, 279.4), _ => (mmW, mmH) };
            width.Text = dims.Item1.ToString("0.##"); height.Text = dims.Item2.ToString("0.##"); EnableDimensions();
        };
        EnableDimensions();
        var directions = Choices("自动", "纵向", "横向"); directions.SelectedIndex = orientation; Row(fields, "方向", directions);
        var hardware = new CheckBox { Content = "使用打印机最小页边距", IsChecked = margins is null, Margin = new Thickness(0, 0, 0, 12) }; fields.Children.Add(hardware);
        var m = margins ?? new Thickness(24); var inputs = new[] { Input(m.Left * 25.4 / 96, "左页边距"), Input(m.Top * 25.4 / 96, "上页边距"), Input(m.Right * 25.4 / 96, "右页边距"), Input(m.Bottom * 25.4 / 96, "下页边距") };
        string[] labels = ["左边距 (mm)", "上边距 (mm)", "右边距 (mm)", "下边距 (mm)"];
        for (var i = 0; i < 4; ++i) Row(fields, labels[i], inputs[i]);
        void EnableMargins() { foreach (var input in inputs) input.IsEnabled = hardware.IsChecked != true; }
        hardware.Checked += (_, _) => EnableMargins(); hardware.Unchecked += (_, _) => EnableMargins(); EnableMargins();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; error.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); fields.Children.Add(error);
        (Size Paper, Thickness? Margins, int Orientation)? result = null;
        accept.Click += (_, _) =>
        {
            try
            {
                var w = Read(width, "纸张宽度", 25, 2000) * 96 / 25.4; var h = Read(height, "纸张高度", 25, 2000) * 96 / 25.4;
                Thickness? requested = hardware.IsChecked == true ? null : new Thickness(Read(inputs[0], "左边距", 0, 500) * 96 / 25.4, Read(inputs[1], "上边距", 0, 500) * 96 / 25.4, Read(inputs[2], "右边距", 0, 500) * 96 / 25.4, Read(inputs[3], "下边距", 0, 500) * 96 / 25.4);
                if (requested is { } r && (r.Left + r.Right >= w || r.Top + r.Bottom >= h)) throw new ArgumentException("页边距之和不能超过纸张尺寸。");
                result = (new Size(w, h), requested, directions.SelectedIndex); dialog.DialogResult = true;
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    internal static (int Dpi, int Color)? Advanced(Window owner, int dpi, int color)
    {
        var (dialog, fields, accept) = Create(owner, "高级打印设置");
        var resolutions = Choices("150 dpi", "300 dpi", "600 dpi", "1200 dpi"); int[] values = [150, 300, 600, 1200]; resolutions.SelectedIndex = Array.IndexOf(values, dpi);
        Row(fields, "输出分辨率", resolutions);
        var colors = Choices("彩色", "灰度", "黑白（仅黑墨）"); colors.SelectedIndex = color; Row(fields, "颜色", colors);
        fields.Children.Add(new TextBlock { Text = "输出方式：图像（PDFium 打印渲染）\n\n原有纯黑会保持纯黑；省墨、纸张来源与设备色彩选项由打印机驱动控制。\n\n每面图像总量限制为 32 MP；大纸张会自动降低分辨率。", TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
        accept.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? (values[resolutions.SelectedIndex], colors.SelectedIndex) : null;
    }

    internal static (bool Reverse, bool Rotate)? ManualDuplex(Window owner, int sheets, bool shortEdge)
    {
        var (dialog, fields, accept) = Create(owner, "手动双面：放回纸张", 510); accept.Content = "提交背面";
        fields.Children.Add(new TextBlock { Text = $"已提交 {sheets} 张纸的正面。\n\n请等待设备打印完成，再将整叠纸按设备的进纸方向放回，继续打印背面。所选方式：{(shortEdge ? "短边" : "长边")}翻转。", TextWrapping = TextWrapping.Wrap, LineHeight = 24, Margin = new Thickness(0, 0, 0, 14) });
        var reverse = new CheckBox { Content = "逆序输出背面", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
        var rotate = new CheckBox { Content = "背面旋转 180°", IsChecked = shortEdge };
        fields.Children.Add(reverse); fields.Children.Add(rotate);
        fields.Children.Add(new TextBlock { Text = "以上两项需与打印机出纸堆叠、放回方式对应。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 12, 0, 0) });
        accept.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? (reverse.IsChecked == true, rotate.IsChecked == true) : null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
