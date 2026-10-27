using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TeXeed.App.Controls;

internal sealed class ImagePreview : Window
{
    private static readonly DependencyProperty PreviewTitleProperty = DependencyProperty.RegisterAttached("PreviewTitle", typeof(string), typeof(ImagePreview));
    private static readonly DependencyProperty SourceFactoryProperty = DependencyProperty.RegisterAttached("SourceFactory", typeof(Func<ImageSource>), typeof(ImagePreview));
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _scaleText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 50, TextAlignment = TextAlignment.Center };
    private readonly double _nativeWidth, _nativeHeight;
    private bool _fit = true;
    internal double Scale { get; private set; } = 1;

    static ImagePreview()
    {
        EventManager.RegisterClassHandler(typeof(Image), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, e) => { if (((Image)sender).GetValue(PreviewTitleProperty) is string) { e.Handled = true; Open((Image)sender); } }));
        EventManager.RegisterClassHandler(typeof(Image), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) => { if (e.Key is Key.Enter or Key.Space && ((Image)sender).GetValue(PreviewTitleProperty) is string) { e.Handled = true; if (!e.IsRepeat) Open((Image)sender); } }));
    }
    internal static void Enable(Image image, string title, Func<ImageSource>? source = null)
    {
        image.SetValue(PreviewTitleProperty, title); image.SetValue(SourceFactoryProperty, source);
        image.Cursor = Cursors.Hand; image.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(title) ? "查看图片" : "查看图片 · " + title);
    }
    internal static bool IsPreviewImage(object source) => source is Image image && image.GetValue(PreviewTitleProperty) is string;
    internal static BitmapSource LoadBase64(string source) => LoadBitmap(new MemoryStream(Convert.FromBase64String(source)));
    internal static BitmapSource LoadBitmap(Stream stream)
    {
        using (stream)
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
    }
    internal static void Open(Image image)
    {
        try
        {
            var source = (image.GetValue(SourceFactoryProperty) as Func<ImageSource>)?.Invoke() ?? image.Source;
            if (source is null) return;
            var preview = new ImagePreview(source, image.GetValue(PreviewTitleProperty) as string ?? "图片") { Owner = GetWindow(image) };
            preview.ShowDialog();
            image.Focus();
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException)
        { image.ToolTip = "无法读取图片"; }
    }
    internal ImagePreview(ImageSource source, string title)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "图片预览" : title;
        Width = 1000; Height = 720; MinWidth = 360; MinHeight = 280;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _nativeWidth = source is BitmapSource bitmap ? bitmap.PixelWidth : source.Width;
        _nativeHeight = source is BitmapSource raster ? raster.PixelHeight : source.Height;
        _image = new Image { Source = source, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _scroll = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        var root = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12) };
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); root.Children.Add(_scroll); Content = root;
        Button Action(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button); return button;
        }
        Action("−", () => SetScale(Scale / 1.25)); toolbar.Children.Add(_scaleText);
        Action("+", () => SetScale(Scale * 1.25)); Action("适应", Fit); Action("100%", () => SetScale(1));
        var close = Action("关闭", Close); close.IsCancel = true;
        _scroll.ScrollChanged += (_, e) => { if (_fit && (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) Fit(); };
        Loaded += (_, _) => { Fit(); close.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key is Key.Add or Key.OemPlus) { e.Handled = true; SetScale(Scale * 1.25); }
            else if (e.Key is Key.Subtract or Key.OemMinus) { e.Handled = true; SetScale(Scale / 1.25); }
        };
        Closed += (_, _) => _image.Source = null;
    }
    internal void Fit()
    {
        _fit = true;
        ApplyScale(Math.Min(1, Math.Min(Math.Max(1, _scroll.ViewportWidth - 2) / _nativeWidth, Math.Max(1, _scroll.ViewportHeight - 2) / _nativeHeight)));
    }
    internal void SetScale(double scale) { _fit = false; ApplyScale(Math.Clamp(scale, .05, 8)); }
    private void ApplyScale(double scale)
    {
        Scale = scale; _image.Width = _nativeWidth * scale; _image.Height = _nativeHeight * scale;
        _scaleText.Text = $"{scale:P0}";
    }
}
