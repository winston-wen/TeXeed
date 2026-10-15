using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TeXeed.App.Services;

namespace TeXeed.App;

internal sealed class PdfPrintPreviewWindow : Window
{
    private readonly PdfDocument _document;
    private readonly PdfPrintFace[] _faces;
    private readonly PdfPrintOptions _options;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Canvas _sheet = new() { Background = Brushes.White, ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Rectangle _guide = new() { Stroke = Brushes.Gray, StrokeThickness = .8, StrokeDashArray = [4, 4], IsHitTestVisible = false };
    private readonly Viewbox _view = new() { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _index = new() { Width = 52, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _ratio = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _previous, _next;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _tasks = [];
    private int _version;
    private bool _automatic = true, _closing;
    private double _zoom = 1;
    internal int FaceIndex { get; private set; }

    internal PdfPrintPreviewWindow(PdfDocument document, PdfPrintFace[] faces, PdfPrintOptions options, int index)
    {
        _document = document; _faces = faces; _options = options; FaceIndex = index;
        Title = "打印预览"; Width = Math.Min(1100, SystemParameters.WorkArea.Width - 32); Height = Math.Min(900, SystemParameters.WorkArea.Height - 32);
        MinWidth = 650; MinHeight = 450; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowTheme.Attach(this); PdfPrintDialogs.AttachCaption(this);
        var root = new DockPanel(); Content = root;
        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(zoom, Dock.Right); bar.Children.Add(zoom);
        zoom.Children.Add(Button("缩小", () => Zoom(1 / 1.2))); zoom.Children.Add(Button("放大", () => Zoom(1.2))); zoom.Children.Add(Button("自动倍率", () => { _automatic = true; Fit(); })); zoom.Children.Add(_ratio);
        var nav = new StackPanel { Orientation = Orientation.Horizontal }; bar.Children.Add(nav);
        _previous = Button("上一面", () => Navigate(FaceIndex - 1)); _next = Button("下一面", () => Navigate(FaceIndex + 1));
        nav.Children.Add(_previous); nav.Children.Add(new TextBlock { Text = "第", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        System.Windows.Automation.AutomationProperties.SetName(_index, "指定输出面");
        nav.Children.Add(_index); nav.Children.Add(_count); nav.Children.Add(_next);
        _index.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
        _index.LostKeyboardFocus += (_, _) => Go();
        _view.Child = _sheet; _sheet.Children.Add(_image); _sheet.Children.Add(_guide); _scroll.Content = _view;
        _scroll.SetResourceReference(BackgroundProperty, "PreviewBackground"); root.Children.Add(_scroll);
        _scroll.SizeChanged += (_, _) => Fit(); Loaded += (_, _) => Navigate(FaceIndex);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closing += OnClosing;
    }
    private static Button Button(string title, Action action)
    { var button = new Button { Content = title, Padding = new Thickness(10, 5, 10, 5) }; button.Click += (_, _) => action(); return button; }
    private void Go() { if (int.TryParse(_index.Text, out var page)) Navigate(Math.Clamp(page - 1, 0, _faces.Length - 1)); else _index.Text = (FaceIndex + 1).ToString(); }
    private void Navigate(int index)
    {
        if (_closing) return;
        FaceIndex = Math.Clamp(index, 0, _faces.Length - 1); _index.Text = (FaceIndex + 1).ToString(); _count.Text = $"/ {_faces.Length} 面";
        _previous.IsEnabled = FaceIndex > 0; _next.IsEnabled = FaceIndex < _faces.Length - 1;
        var task = RenderAsync(++_version, _faces[FaceIndex]); _tasks.Add(task);
    }
    private async Task RenderAsync(int version, PdfPrintFace face)
    {
        try
        {
            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                if (version != _version || _closing) return;
                var drawing = await Task.Run(() => PdfPrintDrawing.Render(_document, face, _options, 120, _lifetime.Token));
                if (version != _version || _closing) return;
                _sheet.Width = _image.Width = face.Layout.Paper.Width; _sheet.Height = _image.Height = face.Layout.Paper.Height;
                _image.Source = new DrawingImage(drawing); var r = face.Layout.Printable;
                Canvas.SetLeft(_guide, r.X); Canvas.SetTop(_guide, r.Y); _guide.Width = r.Width; _guide.Height = r.Height; Fit();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) _count.Text = "预览失败：" + ex.Message; }
    }
    private void Fit()
    {
        if (_sheet.Width <= 0 || !double.IsFinite(_sheet.Width) || _scroll.ActualWidth <= 64 || _scroll.ActualHeight <= 64) return;
        var factor = _automatic ? Math.Min((_scroll.ActualWidth - 64) / _sheet.Width, (_scroll.ActualHeight - 64) / _sheet.Height) : _zoom;
        _view.Width = _sheet.Width * factor; _view.Height = _sheet.Height * factor; _ratio.Text = $"{factor * 100:0.##}%";
    }
    private void Zoom(double factor) { _zoom = Math.Clamp((_automatic && double.IsFinite(_view.Width) ? _view.Width / _sheet.Width : _zoom) * factor, .1, 5); _automatic = false; Fit(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        // Keep the owner modal until PDFium has stopped using its document.
        e.Cancel = true; _closing = true; _lifetime.Cancel(); ++_version;
        await Task.WhenAll(_tasks); _lifetime.Dispose(); await System.Windows.Threading.Dispatcher.Yield(); Close();
    }
}
