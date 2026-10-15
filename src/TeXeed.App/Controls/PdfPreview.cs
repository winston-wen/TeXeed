using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App.Controls;

public sealed partial class PdfPreview : UserControl, IDisposable
{
    private const double PageGap = 18;
    private const int RasterBudgetPixels = 16 * 1024 * 1024;
    private static readonly Brush DarkPaperBrush = CreateDarkPaperBrush();
    private static Brush CreateDarkPaperBrush()
    {
        var brush = new SolidColorBrush(PdfDocument.DarkPaperColor); brush.Freeze(); return brush;
    }
    private readonly ScrollViewer _scroll = new PreviewScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
    private readonly Canvas _pages = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20) };
    private readonly TextBlock _empty = new() { Text = "预览将在编译成功后出现\n\n从左侧开始写作，让想法慢慢成形。", TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(195, 183, 184)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(25), TextWrapping = TextWrapping.Wrap, LineHeight = 26 };
    private readonly List<PageLayout> _layouts = [];
    private readonly Dictionary<int, PageVisual> _visuals = [];
    private PdfDocument? _document;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _rendering, _renderAgain, _fit = true, _disposed, _updatingLayout;
    private double _zoom = 1, _layoutZoom = double.NaN;
    private Thickness _anchorPadding;
    private ZoomAnchor? _readingAnchor;
    private Size _readingViewport;
    private int _loadVersion, _renderVersion;
    private CancellationTokenSource? _loadCancellation, _cropCancellation;
    private Task? _cropTask;
    private PdfDocument? _cropDocument;
    private Window? _hostWindow;
    private HwndSource? _hostSource;
    private PhysicalScreenMetrics _screen = PhysicalScreenMetrics.Estimated;
    private double _autoWidthMaximumCm = 30;
    private PdfLocation? _highlight;
    public event EventHandler<PdfLocation>? InverseRequested;
    public event EventHandler? ViewInteracted;
    public event EventHandler<string>? StatusChanged;
    public int PageCount => _document?.Pages.Count ?? 0;
    public int VisiblePage => (CaptureAnchor(ViewportCenter)?.Page ?? 0) + 1;
    public double Zoom => _zoom;
    public bool IsAutoWidth => _fit;
    public bool PhysicalWidthIsEstimated => _screen.IsEstimated;
    public double AutoWidthMaximumCm
    {
        get => _autoWidthMaximumCm;
        set
        {
            if (_disposed || !double.IsFinite(value) || value is < 1 or > 200 || value == _autoWidthMaximumCm) return;
            _autoWidthMaximumCm = value;
            if (_fit) FitWidth();
        }
    }
    public double MaximumZoom => _autoCrop ? 8 : 3;
    public event EventHandler? ZoomChanged;
    private bool _autoCrop;
    private bool _compressWhitespace = true;
    public bool CompressVerticalWhitespace
    {
        get => _compressWhitespace;
        set => SetCropOptions(_autoCrop, value);
    }
    public bool AutoCrop
    {
        get => _autoCrop;
        set => SetCropOptions(value, _compressWhitespace);
    }
    public void SetCropOptions(bool crop, bool compressWhitespace)
    {
        if (_disposed || _autoCrop == crop && _compressWhitespace == compressWhitespace) return;
        var anchor = CaptureAnchor(ViewportCenter);
        _autoCrop = crop; _compressWhitespace = compressWhitespace;
        _zoom = Math.Clamp(_zoom, .2, MaximumZoom);
        RebuildCropLayout(anchor);
        if (crop && _document is { Crops: null } document) StartCropAnalysis(document);
    }

    private void RebuildCropLayout(ZoomAnchor? anchor)
    {
        _layoutZoom = double.NaN; _anchorPadding = new Thickness();
        UpdatePageSizes(); RestoreAnchor(anchor, horizontal: !_fit);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        if (_fit) _scroll.ScrollToHorizontalOffset(0);
    }

    private void StartCropAnalysis(PdfDocument document)
    {
        if (ReferenceEquals(_cropDocument, document) && _cropTask is { IsCompleted: false }) return;
        _cropCancellation?.Cancel(); _cropCancellation?.Dispose();
        _cropCancellation = new CancellationTokenSource(); _cropDocument = document;
        _cropTask = AnalyzeCropAsync(document, _cropCancellation.Token);
    }

    private async Task AnalyzeCropAsync(PdfDocument document, CancellationToken cancellation)
    {
        try
        {
            StatusChanged?.Invoke(this, "正在分析 PDF 空白…");
            await Task.Run(() => document.PrepareCrops(cancellation), cancellation);
            if (_disposed || document != _document || cancellation.IsCancellationRequested) return;
            if (_autoCrop) RebuildCropLayout(CaptureAnchor(ViewportCenter));
            StatusChanged?.Invoke(this, $"{PageCount} 页 · PDFium");
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        { if (!_disposed && document == _document) StatusChanged?.Invoke(this, "裁边分析失败：" + ex.Message); }
    }
    private bool _invertColors;
    public bool InvertColors
    {
        get => _invertColors;
        set
        {
            if (_disposed || _invertColors == value) return;
            _invertColors = value; _renderVersion++;
            foreach (var visual in _visuals.Values)
                ((Grid)visual.Holder.Child).Background = value ? DarkPaperBrush : Brushes.White;
            ScheduleRender();
        }
    }

    public PdfPreview()
    {
        InitializeAiSelection();
        Loaded += (_, _) => AttachDisplayEvents();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var grid = new Grid();
        grid.Children.Add(_scroll); grid.Children.Add(_empty); Content = grid;
        _scroll.Content = _pages;
        _scroll.ScrollChanged += (_, e) =>
        {
            // Retire an old viewport's queued pages as soon as the reader
            // moves. A slow cover scan must not render its neighbors before
            // the newly requested chapter gets a turn.
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) _renderVersion++;
            if (!_updatingLayout)
            {
                if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) ResizePages();
                else RememberReadingAnchor();
            }
            ScheduleRender();
        };
        SizeChanged += (_, _) => ResizePages();
        IsVisibleChanged += (_, _) => { if (IsVisible) { UpdatePhysicalScreen(); UpdatePageSizes(); ScheduleRender(); } else _renderTimer.Stop(); };
        _renderTimer.Tick += async (_, _) => { _renderTimer.Stop(); await RenderVisibleAsync(); };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); ClearHighlights(); };
        PreviewMouseDown += (_, _) => ViewInteracted?.Invoke(this, EventArgs.Empty);
        PreviewKeyDown += (_, _) => ViewInteracted?.Invoke(this, EventArgs.Empty);
        PreviewMouseWheel += (_, e) =>
        {
            ViewInteracted?.Invoke(this, EventArgs.Empty);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            { SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(_scroll)); e.Handled = true; }
        };
    }

    private void AttachDisplayEvents()
    {
        var host = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, host))
        {
            DetachDisplayEvents(); _hostWindow = host;
            if (host is not null)
            {
                host.LocationChanged += DisplayLocationChanged;
                host.DpiChanged += DisplayDpiChanged;
                _hostSource = PresentationSource.FromVisual(host) as HwndSource;
                _hostSource?.AddHook(DisplayMessage);
            }
        }
        UpdatePhysicalScreen();
    }
    private void DetachDisplayEvents()
    {
        if (_hostWindow is not null)
        {
            _hostWindow.LocationChanged -= DisplayLocationChanged;
            _hostWindow.DpiChanged -= DisplayDpiChanged;
        }
        _hostSource?.RemoveHook(DisplayMessage); _hostWindow = null; _hostSource = null;
    }
    private void DisplayLocationChanged(object? sender, EventArgs e) => UpdatePhysicalScreen();
    private void DisplayDpiChanged(object sender, DpiChangedEventArgs e) => QueuePhysicalScreenUpdate();
    private nint DisplayMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x007e) { PhysicalScreen.Invalidate(); QueuePhysicalScreenUpdate(); } // WM_DISPLAYCHANGE
        return 0;
    }
    private void QueuePhysicalScreenUpdate() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePhysicalScreen));
    private void UpdatePhysicalScreen()
    {
        if (!_disposed && IsVisible) SetScreenMeasurement(PhysicalScreen.Measure(this));
    }
    internal void SetScreenMeasurement(PhysicalScreenMetrics measurement)
    {
        if (_screen == measurement) return;
        _screen = measurement;
        if (_fit) FitWidth();
    }

    public async Task LoadAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelAiSelection();
        var version = ++_loadVersion;
        _cropCancellation?.Cancel();
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellation = _loadCancellation.Token; var analyzeCrop = _autoCrop;
        PdfDocument document;
        try { document = await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new IOException("PDF 文件超过 256 MB，无法预览。");
            var loaded = new PdfDocument(File.ReadAllBytes(path), TexPageLayout.Read(path));
            try
            {
                if (analyzeCrop) loaded.PrepareCrops(cancellation);
                cancellation.ThrowIfCancellationRequested();
                return loaded;
            }
            catch { loaded.Dispose(); throw; }
        }, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        // A resize/zoom only invalidates rasters; it must never discard an in-flight document load.
        if (_disposed || version != _loadVersion) { document.Dispose(); return; }
        var offset = _scroll.VerticalOffset - _pages.Margin.Top;
        var horizontalOffset = _scroll.HorizontalOffset - _pages.Margin.Left;
        var anchor = CaptureAnchor(ViewportCenter);
        _renderVersion++;
        _cropCancellation?.Cancel();
        _document?.Dispose(); _document = document;
        ClearVisuals(); _layouts.Clear(); _layoutZoom = double.NaN; _anchorPadding = new Thickness(); _readingAnchor = null;
        ClearHighlights(); _empty.Visibility = Visibility.Collapsed;
        UpdatePageSizes();
        _scroll.ScrollToVerticalOffset(Math.Max(0, offset + _pages.Margin.Top));
        _scroll.ScrollToHorizontalOffset(Math.Max(0, horizontalOffset + _pages.Margin.Left));
        if (anchor is not null && anchor.Page < PageCount) RestoreAnchor(anchor, horizontal: !_fit);
        else if (_autoCrop) _scroll.ScrollToVerticalOffset(Math.Max(0, _pages.Margin.Top - 12));
        RememberReadingAnchor();
        ScheduleRender();
        StatusChanged?.Invoke(this, $"{PageCount} 页 · PDFium");
        if (_autoCrop && document.Crops is null) StartCropAnalysis(document);
    }

    /// <summary>Fit width while retaining the content nearest the viewport center.</summary>
    public void FitWidth()
    {
        if (_disposed) return;
        var anchor = CaptureAnchor(ViewportCenter);
        _fit = true; _anchorPadding = new Thickness(); UpdatePageSizes();
        RestoreAnchor(anchor, horizontal: false);
        _scroll.ScrollToHorizontalOffset(0);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toolbar zoom retains the content nearest the viewport center.</summary>
    public void SetZoom(double zoom) => SetZoom(zoom, ViewportCenter);

    /// <summary>
    /// Zoom around a point in the PDF ScrollViewer's viewport coordinates. The same PDF
    /// content remains under that point, including transitions into horizontal scrolling.
    /// In a page gap or margin, retain the nearest content point at its current position.
    /// </summary>
    public void SetZoom(double zoom, Point viewportAnchor)
    {
        if (_disposed || !double.IsFinite(zoom) || !double.IsFinite(viewportAnchor.X) || !double.IsFinite(viewportAnchor.Y)) return;
        var nextZoom = Math.Clamp(zoom, .2, MaximumZoom);
        _fit = false;
        if (_zoom == nextZoom) { ZoomChanged?.Invoke(this, EventArgs.Empty); return; }
        var anchor = CaptureAnchor(viewportAnchor);
        _zoom = nextZoom; _anchorPadding = new Thickness(); UpdatePageSizes();
        RestoreAnchor(anchor);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        CancelAiSelection();
        _loadVersion++; _renderVersion++;
        _loadCancellation?.Cancel(); _cropCancellation?.Cancel();
        _renderTimer.Stop(); _highlightTimer.Stop(); _renderAgain = false;
        _document?.Dispose(); _document = null;
        _cropDocument = null;
        ClearVisuals(); _layouts.Clear(); _highlight = null; _layoutZoom = double.NaN; _anchorPadding = new Thickness(); _readingAnchor = null;
        _pages.Width = 0; _pages.Height = 0; _empty.Visibility = Visibility.Visible;
    }

    public void ShowLocation(PdfLocation location, bool highlight = true, double viewportFraction = .35, bool centerBounds = false, bool pageStart = false)
    {
        if (_disposed || _document is null || location.Page < 1 || location.Page > _layouts.Count
            || !double.IsFinite(location.X) || !double.IsFinite(location.Y) || !double.IsFinite(viewportFraction)) return;
        _renderVersion++;
        UpdatePageSizes(); ClearHighlights();
        if (highlight) _highlight = location;
        var index = location.Page - 1;
        var target = pageStart ? DisplayToPage(index, new Point()) : new Point(location.X + (centerBounds && double.IsFinite(location.Width) ? Math.Max(0, location.Width) / 2 : 0),
            location.Y + (centerBounds && double.IsFinite(location.Height) ? Math.Max(0, location.Height) / 2 : 0));
        RestoreAnchor(new ZoomAnchor(index, target, new Point()), horizontal: false, viewportFraction: pageStart ? 0 : Math.Clamp(viewportFraction, 0, 1));
        _highlightTimer.Stop();
        if (highlight) _highlightTimer.Start();
        ScheduleRender();
        if (_visuals.TryGetValue(index, out var visual)) DrawHighlight(index, visual);
    }

    private void UpdatePageSizes()
    {
        if (_disposed || _document is null || _updatingLayout) return;
        _updatingLayout = true;
        try
        {
            UpdatePageSizesCore();
            ArrangeViewportPadding();
        }
        finally { _updatingLayout = false; }
    }

    private void ResizePages()
    {
        if (_disposed || _document is null || _updatingLayout) return;
        // ScrollViewer may have already arranged the new viewport when SizeChanged
        // arrives. Use the reading point captured before that arrangement rather
        // than hit-testing its newly shifted center against the old page scale.
        var anchor = _readingAnchor;
        var oldViewport = _readingViewport;
        if (IsVisible) _screen = PhysicalScreen.Measure(this);
        UpdatePageSizes();
        if (anchor is not null)
        {
            var target = anchor.ViewportPoint + new Vector((_scroll.ViewportWidth - oldViewport.Width) / 2,
                (_scroll.ViewportHeight - oldViewport.Height) / 2);
            RestoreAnchor(anchor with { ViewportPoint = target });
        }
        else RememberReadingAnchor();
    }

    private void RememberReadingAnchor()
    {
        if (_updatingLayout || _disposed || _document is null) return;
        _updatingLayout = true;
        try
        {
            _readingAnchor = CaptureAnchor(ViewportCenter);
            _readingViewport = new Size(_scroll.ViewportWidth, _scroll.ViewportHeight);
        }
        finally { _updatingLayout = false; }
    }

    private void UpdatePageSizesCore()
    {
        var document = _document!;
        var crops = _autoCrop ? (_compressWhitespace ? document.Crops : document.EdgeCrops) : null;
        var sizes = document.Pages.Select((size, index) => crops?[index].Size ?? size).ToArray();
        if (_fit && ActualWidth > 65)
            // Symmetric 20-DIP gutters plus the two page-border pixels. Overlay
            // scrollbars do not subtract width from just the right-hand side.
            _zoom = Math.Min(Math.Min(ActualWidth - 42, _autoWidthMaximumCm * _screen.DipsPerCentimeter)
                / (sizes.Max(s => s.Width) * 96 / 72), MaximumZoom);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        if (_layoutZoom == _zoom && _layouts.Count == document.Pages.Count) { ScheduleRender(); return; }
        CancelAiSelection();
        _layoutZoom = _zoom;
        _renderVersion++;
        // Keep the last raster on screen while the background renderer catches up.
        // Only a different document (LoadAsync/Clear) invalidates the page content.
        _layouts.Clear();
        var top = 0d;
        var widest = sizes.Max(s => s.Width) * 96 / 72 * _zoom;
        var gap = crops is null ? PageGap : 8;
        for (var index = 0; index < sizes.Length; index++)
        {
            var size = sizes[index];
            var displayed = new Size(size.Width * 96 / 72 * _zoom, size.Height * 96 / 72 * _zoom);
            _layouts.Add(new PageLayout(top, (widest - displayed.Width) / 2, displayed, crops?[index]));
            top += displayed.Height + gap;
        }
        _pages.Width = widest + 2;
        _pages.Height = Math.Max(0, top - gap + 2);
        foreach (var (index, visual) in _visuals)
        {
            var layout = _layouts[index];
            var page = (Grid)visual.Holder.Child;
            page.Width = layout.Size.Width; page.Height = layout.Size.Height;
            Canvas.SetLeft(visual.Holder, layout.Left); Canvas.SetTop(visual.Holder, layout.Top);
            DrawHighlight(index, visual);
        }
        ScheduleRender();
    }

    private Point ViewportCenter => new(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);

    private void ArrangeViewportPadding()
    {
        // Half a viewport before/after the document lets even its first and last
        // content point reach the center. Horizontal centering is explicit so it
        // remains predictable when zooming creates a horizontal scrollbar.
        for (var pass = 0; pass < 3; pass++)
        {
            _scroll.UpdateLayout();
            var horizontal = Math.Max(20, (_scroll.ViewportWidth - _pages.Width) / 2);
            var vertical = Math.Max(20, _scroll.ViewportHeight / 2);
            var margin = new Thickness(horizontal + _anchorPadding.Left, vertical + _anchorPadding.Top,
                horizontal + _anchorPadding.Right, vertical + _anchorPadding.Bottom);
            if (_pages.Margin == margin) break;
            _pages.Margin = margin;
        }
        _scroll.UpdateLayout();
    }

    private ZoomAnchor? CaptureAnchor(Point viewportPoint)
    {
        if (_document is null || _layouts.Count == 0) return null;
        _scroll.UpdateLayout();
        if (_scroll.ViewportWidth <= 0 || _scroll.ViewportHeight <= 0) return null;
        viewportPoint = new Point(Math.Clamp(viewportPoint.X, 0, _scroll.ViewportWidth), Math.Clamp(viewportPoint.Y, 0, _scroll.ViewportHeight));
        var canvasPoint = _scroll.TranslatePoint(viewportPoint, _pages);
        var distance = double.PositiveInfinity;
        var pageIndex = 0;
        var nearest = new Point();
        for (var index = 0; index < _layouts.Count; index++)
        {
            var layout = _layouts[index];
            // Page content starts one DIP inside its border.
            var candidate = new Point(Math.Clamp(canvasPoint.X, layout.Left + 1, layout.Left + 1 + layout.Size.Width),
                Math.Clamp(canvasPoint.Y, layout.Top + 1, layout.Top + 1 + layout.Size.Height));
            var squaredDistance = (candidate - canvasPoint).LengthSquared;
            if (squaredDistance < distance) { distance = squaredDistance; pageIndex = index; nearest = candidate; }
        }
        var page = _layouts[pageIndex];
        var documentPoint = DisplayToPage(pageIndex, new Point(nearest.X - page.Left - 1, nearest.Y - page.Top - 1));
        return new ZoomAnchor(pageIndex, documentPoint, _pages.TranslatePoint(nearest, _scroll));
    }

    private void RestoreAnchor(ZoomAnchor? anchor, bool horizontal = true, double? viewportFraction = null)
    {
        if (anchor is null || _document is null || anchor.Page >= _layouts.Count) return;
        var alreadyUpdating = _updatingLayout;
        _updatingLayout = true;
        try
        {
            for (var pass = 0; pass < 5; pass++)
            {
                ArrangeViewportPadding();
                if (_scroll.ViewportHeight <= 0 || _scroll.ViewportWidth <= 0) return;
                var layout = _layouts[anchor.Page];
                var pagePoint = PageToDisplay(anchor.Page, anchor.DocumentPoint);
                var current = _pages.TranslatePoint(new Point(layout.Left + 1 + pagePoint.X, layout.Top + 1 + pagePoint.Y), _scroll);
                var target = new Point(Math.Clamp(anchor.ViewportPoint.X, 0, _scroll.ViewportWidth),
                    viewportFraction is { } fraction ? _scroll.ViewportHeight * fraction : Math.Clamp(anchor.ViewportPoint.Y, 0, _scroll.ViewportHeight));
                var x = _scroll.HorizontalOffset + current.X - target.X;
                var y = _scroll.VerticalOffset + current.Y - target.Y;
                var left = horizontal ? Math.Max(0, -x) : 0;
                var right = horizontal ? Math.Max(0, x - _scroll.ScrollableWidth) : 0;
                var top = Math.Max(0, -y);
                var bottom = Math.Max(0, y - _scroll.ScrollableHeight);
                if (left > .01 || right > .01 || top > .01 || bottom > .01)
                {
                    // Narrow pages can have no horizontal scroll range. Add only
                    // the space needed to preserve the pointer's content anchor;
                    // a new zoom recomputes this instead of accumulating padding.
                    _anchorPadding = new Thickness(_anchorPadding.Left + left, _anchorPadding.Top + top,
                        _anchorPadding.Right + right, _anchorPadding.Bottom + bottom);
                    continue;
                }
                if (horizontal) _scroll.ScrollToHorizontalOffset(Math.Max(0, x));
                _scroll.ScrollToVerticalOffset(Math.Max(0, y));
                _scroll.UpdateLayout();
                var placed = _pages.TranslatePoint(new Point(layout.Left + 1 + pagePoint.X, layout.Top + 1 + pagePoint.Y), _scroll);
                if ((!horizontal || Math.Abs(placed.X - target.X) < .01) && Math.Abs(placed.Y - target.Y) < .01) break;
            }
        }
        finally { _updatingLayout = alreadyUpdating; }
        if (!alreadyUpdating) RememberReadingAnchor();
        ScheduleRender();
    }

    private PageVisual RealizePage(int index)
    {
        if (_visuals.TryGetValue(index, out var existing)) return existing;
        var layout = _layouts[index];
        var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var page = new Grid { Background = _invertColors ? DarkPaperBrush : Brushes.White, Width = layout.Size.Width, Height = layout.Size.Height, ClipToBounds = true };
        page.Children.Add(image); page.Children.Add(overlay);
        page.PreviewMouseDown += (_, e) =>
        {
            if ((e.ChangedButton == MouseButton.Right && e.ClickCount == 2)
                || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                if (_document is null || page.ActualWidth <= 0 || page.ActualHeight <= 0) return;
                var point = DisplayToPage(index, e.GetPosition(page));
                InverseRequested?.Invoke(this, new PdfLocation(index + 1, point.X, point.Y));
                e.Handled = true;
            }
        };
        var holder = new Border { Child = page, BorderBrush = new SolidColorBrush(Color.FromRgb(64, 56, 56)), BorderThickness = new Thickness(1) };
        holder.SetResourceReference(Border.BorderBrushProperty, "Line");
        Canvas.SetLeft(holder, layout.Left); Canvas.SetTop(holder, layout.Top);
        var visual = new PageVisual(holder, image, overlay);
        _visuals.Add(index, visual); _pages.Children.Add(holder);
        DrawHighlight(index, visual);
        return visual;
    }

    private void DrawHighlight(int index, PageVisual visual)
    {
        visual.Overlay.Children.Clear();
        if (_document is null || _highlight is not { } location || location.Page != index + 1) return;
        var size = _layouts[index].Size;
        var width = double.IsFinite(location.Width) ? Math.Max(1, location.Width) : 1;
        var height = double.IsFinite(location.Height) ? Math.Max(1, location.Height) : 1;
        var corners = new[] { new Point(location.X, location.Y), new Point(location.X + width, location.Y), new Point(location.X, location.Y + height), new Point(location.X + width, location.Y + height) }
            .Select(p => PageToDisplay(index, p)).ToArray();
        var left = Math.Clamp(corners.Min(p => p.X), 0, size.Width);
        var top = Math.Clamp(corners.Min(p => p.Y), 0, size.Height);
        var rectangle = new Rectangle
        {
            Width = Math.Min(size.Width - left, Math.Max(30, corners.Max(p => p.X) - left)),
            Height = Math.Min(size.Height - top, Math.Max(12, corners.Max(p => p.Y) - top)),
            Fill = new SolidColorBrush(Color.FromArgb(95, 239, 194, 61)), Stroke = new SolidColorBrush(Color.FromRgb(207, 161, 48)), StrokeThickness = 1
        };
        Canvas.SetLeft(rectangle, left); Canvas.SetTop(rectangle, top); visual.Overlay.Children.Add(rectangle);
    }

    private void ScheduleRender()
    {
        if (_disposed || _document is null || !IsVisible) return;
        _renderTimer.Stop(); _renderTimer.Start();
    }

    private void ClearHighlights()
    {
        _highlight = null;
        foreach (var visual in _visuals.Values) visual.Overlay.Children.Clear();
    }

    private void ClearVisuals()
    {
        foreach (var visual in _visuals.Values) visual.Image.Source = null;
        _visuals.Clear(); _pages.Children.Clear();
    }

    private async Task RenderVisibleAsync()
    {
        if (_disposed || _document is null || !IsVisible) return;
        if (_rendering) { _renderAgain = true; return; }
        _rendering = true;
        try
        {
            var document = _document;
            var version = _renderVersion;
            var inverted = _invertColors;
            var viewportHeight = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight;
            var canvasOffset = _scroll.VerticalOffset - _pages.Margin.Top;
            var firstY = canvasOffset - 300;
            var lastY = canvasOffset + viewportHeight + 300;
            var visible = Enumerable.Range(0, _layouts.Count)
                .Where(i => _layouts[i].Top + _layouts[i].Size.Height > firstY && _layouts[i].Top < lastY).ToHashSet();
            // Remove the visual tree and bitmap for every page outside the nearby viewport.
            foreach (var index in _visuals.Keys.Where(i => !visible.Contains(i)).ToArray())
            {
                var old = _visuals[index]; old.Image.Source = null; _pages.Children.Remove(old.Holder); _visuals.Remove(index);
            }
            var budgetPerPage = Math.Max(1, RasterBudgetPixels / Math.Max(1, visible.Count));
            foreach (var index in visible.OrderBy(i => Math.Abs(_layouts[i].Top - canvasOffset)))
            {
                if (_disposed || version != _renderVersion || document != _document) { _renderAgain = !_disposed; break; }
                var item = RealizePage(index);
                var crop = _layouts[index].Crop;
                var scale = _layouts[index].Size.Width / (crop?.Width ?? document.Pages[index].Width) * VisualTreeHelper.GetDpi(this).DpiScaleX;
                var budget = Math.Min(budgetPerPage, 8 * 1024 * 1024);
                var current = item.Image.Source as BitmapSource;
                var sameSize = current is not null && item.RasterScale == scale && item.RasterBudget == budget && ReferenceEquals(item.RasterCrop, crop);
                if (sameSize && item.RasterInverted == inverted) continue;
                // The softer palette is intentionally lossy. Restore original colors from PDF,
                // never by inverting the tinted raster, so repeated toggles cannot accumulate error.
                var bitmap = await Task.Run(() => sameSize && inverted
                    ? PdfDocument.ApplyDarkColors(current!) : document.Render(index, scale, budget, inverted, crop));
                if (_disposed || version != _renderVersion || document != _document) { _renderAgain = !_disposed; break; }
                // Replace in one assignment; the previous image stays visible even
                // if rendering fails or this request is superseded by another zoom.
                item.Image.Source = bitmap;
                item.RasterScale = scale; item.RasterBudget = budget;
                item.RasterInverted = inverted;
                item.RasterCrop = crop;
            }
        }
        catch (ObjectDisposedException) { _renderAgain = !_disposed && _document is not null; }
        catch (Exception ex) { if (!_disposed) StatusChanged?.Invoke(this, "PDF 渲染失败：" + ex.Message); }
        finally
        {
            _rendering = false;
            if (_renderAgain) { _renderAgain = false; ScheduleRender(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        DetachDisplayEvents(); _disposed = true; Clear();
        _loadCancellation?.Dispose(); _cropCancellation?.Dispose();
    }

    private Point PageToDisplay(int index, Point point)
    {
        var layout = _layouts[index];
        if (layout.Crop is not { } crop) return _document!.PageToView(index, point, layout.Size);
        var mapped = crop.ToCropped(_document!.PageToView(index, point, _document.Pages[index]));
        return new Point(mapped.X / crop.Width * layout.Size.Width, mapped.Y / crop.Height * layout.Size.Height);
    }

    private Point DisplayToPage(int index, Point point)
    {
        var layout = _layouts[index];
        if (layout.Crop is not { } crop) return _document!.ViewToPage(index, point, layout.Size);
        var source = crop.ToSource(new Point(point.X / layout.Size.Width * crop.Width, point.Y / layout.Size.Height * crop.Height));
        return _document!.ViewToPage(index, source, _document.Pages[index]);
    }

    private sealed record PageLayout(double Top, double Left, Size Size, PdfPageCrop? Crop = null);
    private sealed record ZoomAnchor(int Page, Point DocumentPoint, Point ViewportPoint);
    private sealed record PageVisual(Border Holder, Image Image, Canvas Overlay)
    {
        public double RasterScale { get; set; } = double.NaN;
        public int RasterBudget { get; set; }
        public bool RasterInverted { get; set; }
        public PdfPageCrop? RasterCrop { get; set; }
    }
}
