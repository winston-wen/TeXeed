using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Services;

namespace TeXeed.App.Controls;
public sealed partial class PdfPreview
{
    public event EventHandler<AiEntry>? AiSelectionConfirmed;
    private bool _aiSelecting, _aiDragging, _aiCapturing;
    private Point _aiStart;
    private Rect _aiSelection = Rect.Empty;
    private Rectangle? _aiOutline;
    private readonly DispatcherTimer _aiScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    public bool IsAiSelecting => _aiSelecting;
    private void InitializeAiSelection()
    {
        var menu = new ContextMenu(); var start = new MenuItem { Header = "框选 PDF 区块用于 AI 问答…" };
        start.Click += (_, _) => BeginAiSelection(); menu.Items.Add(start);
        var fit = new MenuItem { Header = "自动宽度" }; fit.Click += (_, _) => FitWidth(); menu.Items.Add(fit);
        ContextMenu = menu;
        ContextMenuOpening += (_, e) => { start.IsEnabled = _document is not null && !_aiCapturing; if (_aiDragging) e.Handled = true; };
        _pages.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!_aiSelecting || _document is null || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true; _aiStart = e.GetPosition(_pages); _aiDragging = true; _pages.CaptureMouse(); _aiScrollTimer.Start(); UpdateAiSelection(_aiStart);
        };
        _pages.PreviewMouseMove += (_, e) => { if (_aiDragging) { UpdateAiSelection(e.GetPosition(_pages)); e.Handled = true; } };
        _pages.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_aiDragging) return;
            e.Handled = true; UpdateAiSelection(e.GetPosition(_pages)); _aiDragging = false; _aiScrollTimer.Stop(); _pages.ReleaseMouseCapture();
            if (_aiSelection.Width < 5 || _aiSelection.Height < 5) { CancelAiSelection(); return; }
            var confirm = new ContextMenu(); var add = new MenuItem { Header = "确认选区，添加 AI 问题" }; var cancel = new MenuItem { Header = "取消框选" };
            add.Click += async (_, _) => await ConfirmAiSelectionAsync(); cancel.Click += (_, _) => CancelAiSelection(); confirm.Items.Add(add); confirm.Items.Add(cancel);
            confirm.PlacementTarget = this; confirm.Closed += (_, _) => { if (!_aiCapturing) CancelAiSelection(); }; confirm.IsOpen = true;
        };
        _pages.LostMouseCapture += (_, _) => { if (_aiDragging) CancelAiSelection(); };
        PreviewKeyDown += (_, e) => { if (_aiSelecting && e.Key == Key.Escape) { CancelAiSelection(); e.Handled = true; } };
        Unloaded += (_, _) => CancelAiSelection();
        _aiScrollTimer.Tick += (_, _) =>
        {
            if (!_aiDragging) { _aiScrollTimer.Stop(); return; }
            var p = Mouse.GetPosition(_scroll);
            if (p.Y < 32) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 24);
            else if (p.Y > _scroll.ActualHeight - 32) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 24);
            UpdateAiSelection(Mouse.GetPosition(_pages));
        };
    }
    public void BeginAiSelection()
    {
        if (_document is null || _aiCapturing) return;
        CancelAiSelection(); _aiSelecting = true; _pages.Cursor = Cursors.Cross;
        StatusChanged?.Invoke(this, "拖动框选，可跨页；最大 1600 × 2400 显示像素、3 页。靠近上下边缘自动滚动，Esc 取消。");
    }
    public void CancelAiSelection()
    {
        _aiDragging = _aiSelecting = false; _aiScrollTimer.Stop();
        if (_pages.IsMouseCaptured) _pages.ReleaseMouseCapture(); _pages.ClearValue(CursorProperty);
        if (_aiOutline is not null) _pages.Children.Remove(_aiOutline); _aiOutline = null; _aiSelection = Rect.Empty;
    }
    private void UpdateAiSelection(Point end)
    {
        end = new Point(Math.Clamp(end.X, Math.Max(0, _aiStart.X - 1600), Math.Min(_pages.ActualWidth, _aiStart.X + 1600)), Math.Clamp(end.Y, Math.Max(0, _aiStart.Y - 2400), Math.Min(_pages.ActualHeight, _aiStart.Y + 2400)));
        _aiSelection = new Rect(_aiStart, end);
        if (_aiOutline is null) { _aiOutline = new Rectangle { Stroke = Brushes.Goldenrod, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 210, 100)), IsHitTestVisible = false }; _pages.Children.Add(_aiOutline); Panel.SetZIndex(_aiOutline, 99); }
        Canvas.SetLeft(_aiOutline, _aiSelection.X); Canvas.SetTop(_aiOutline, _aiSelection.Y); _aiOutline.Width = _aiSelection.Width; _aiOutline.Height = _aiSelection.Height;
    }
    // Split compressed whitespace into real source bands before mapping through PDF rotation/cropbox transforms.
    internal static IReadOnlyList<Rect> SelectionBands(Rect display, Size displayedPage, Size originalPage, PdfPageCrop? crop)
    {
        if (crop is null) return [new Rect(display.X / displayedPage.Width * originalPage.Width, display.Y / displayedPage.Height * originalPage.Height, display.Width / displayedPage.Width * originalPage.Width, display.Height / displayedPage.Height * originalPage.Height)];
        var x = crop.Left + display.X / displayedPage.Width * crop.Width; var width = display.Width / displayedPage.Width * crop.Width;
        var top = display.Y / displayedPage.Height * crop.Height; var bottom = display.Bottom / displayedPage.Height * crop.Height; var offset = 0d; var result = new List<Rect>();
        foreach (var band in crop.Bands)
        {
            var from = Math.Max(top, offset); var to = Math.Min(bottom, offset + band.Height);
            if (to > from) result.Add(new Rect(x, band.Top + from - offset, width, to - from)); offset += band.Height;
        }
        return result;
    }
    internal async Task ConfirmAiSelectionAsync()
    {
        if (_document is not { } document || _aiCapturing || _aiSelection.IsEmpty) return;
        _aiCapturing = true; var version = _loadVersion; var selected = new List<(int Page, Rect View)>();
        try
        {
            for (var i = 0; i < _layouts.Count; i++)
            {
                var page = _layouts[i]; var intersection = Rect.Intersect(_aiSelection, new Rect(page.Left + 1, page.Top + 1, page.Size.Width, page.Size.Height));
                if (intersection.IsEmpty || intersection.Width < 2 || intersection.Height < 2) continue;
                intersection.Offset(-page.Left - 1, -page.Top - 1);
                selected.AddRange(SelectionBands(intersection, page.Size, document.Pages[i], page.Crop).Select(rect => (i, rect)));
            }
            if (selected.Select(p => p.Page).Distinct().Count() > 3 || selected.Count > 16) throw new InvalidOperationException("选区最多跨 3 页、16 个压缩区块，请缩小范围后重选。");
            if (selected.Count == 0) return;
            var entry = await Task.Run(() =>
            {
                var result = new AiEntry { Title = $"PDF 第 {selected.Min(p => p.Page) + 1}–{selected.Max(p => p.Page) + 1} 页", PdfRevision = document.Fingerprint };
                var total = 0;
                foreach (var group in selected.GroupBy(s => s.Page))
                {
                    var size = document.Pages[group.Key]; var raster = document.Render(group.Key, 2, 4_000_000);
                    foreach (var item in group)
                    {
                        var rect = item.View; var left = Math.Clamp((int)Math.Floor(rect.Left / size.Width * raster.PixelWidth), 0, raster.PixelWidth - 1); var top = Math.Clamp((int)Math.Floor(rect.Top / size.Height * raster.PixelHeight), 0, raster.PixelHeight - 1);
                        var right = Math.Clamp((int)Math.Ceiling(rect.Right / size.Width * raster.PixelWidth), left + 1, raster.PixelWidth); var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom / size.Height * raster.PixelHeight), top + 1, raster.PixelHeight);
                        var crop = new CroppedBitmap(raster, new Int32Rect(left, top, right - left, bottom - top)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop)); using var stream = new MemoryStream(); encoder.Save(stream);
                        total += (int)stream.Length; if (total > 8_000_000) throw new InvalidOperationException("选区截图超过 8 MB，请缩小选区。");
                        var a = document.ViewToPage(group.Key, rect.TopLeft, size); var b = document.ViewToPage(group.Key, rect.BottomRight, size); var bounds = new Rect(a, b);
                        result.PdfBlocks.Add(new AiPdfBlock(group.Key + 1, bounds.X, bounds.Y, bounds.Width, bounds.Height, Convert.ToBase64String(stream.ToArray())));
                    }
                }
                return result;
            });
            if (!_disposed && version == _loadVersion) AiSelectionConfirmed?.Invoke(this, entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { StatusChanged?.Invoke(this, "AI 框选失败：" + ex.Message); }
        finally { _aiCapturing = false; CancelAiSelection(); }
    }
}
