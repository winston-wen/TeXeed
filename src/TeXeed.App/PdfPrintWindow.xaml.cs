using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Xps;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class PdfPrintWindow : Window
{
    private readonly string _path;
    private readonly int _readingPage;
    private readonly Rect? _viewRegion;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _renderGate = new(1);
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, PdfPrinterProfile> _profiles = [];
    private readonly List<Task> _updates = [];
    private PdfDocument? _document;
    private PdfPrintLayout _layout = new(PdfPrintLayout.A4, PdfPrintLayout.Inset(PdfPrintLayout.A4, 24));
    private PdfPrintFace[] _faces = [];
    private PdfPrintOptions _options = new();
    private Size _paper = PdfPrintLayout.A4;
    private Thickness? _margins;
    private byte[]? _driverTicket;
    private string? _printer;
    private int _page, _renderVersion, _dpi = 300;
    private bool _ready, _printing, _syncing, _autoZoom = true, _cancelRequested, _monochrome;
    private double _zoom = 1;
    private XpsDocumentWriter? _writer;
    private Task _renderTask = Task.CompletedTask, _loadTask = Task.CompletedTask;

    internal PdfPrintWindow(string path, string title, int page = 1, Rect? viewRegion = null)
    {
        InitializeComponent(); WindowTheme.Attach(this); PdfPrintDialogs.AttachCaption(this);
        _path = path; _page = _readingPage = page; _viewRegion = viewRegion;
        Title = "打印：" + Path.GetFileName(path);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); StartUpdate(); };
        Loaded += async (_, _) => { _loadTask = LoadAsync(); await _loadTask; };
        Closing += Window_Closing;
        Closed += async (_, _) =>
        {
            _lifetime.Cancel(); _refreshTimer.Stop(); ++_renderVersion;
            try { await _loadTask; await Task.WhenAll(_updates); } catch (Exception) { /* UI already reports failures; closing cannot reuse PDF handles. */ }
            _document?.Dispose(); _lifetime.Dispose();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var printerTask = PdfPrinterService.ListAsync();
            _document = await Task.Run(() =>
            {
                using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 256L * 1024 * 1024) throw new IOException("PDF 文件超过 256 MB，无法打印。");
                using var bytes = new MemoryStream(); file.CopyTo(bytes);
                _lifetime.Token.ThrowIfCancellationRequested(); return new PdfDocument(bytes.ToArray());
            });
            if (_lifetime.IsCancellationRequested) return;
            _syncing = true;
            CurrentRange.Content = $"当前页 {Math.Clamp(_readingPage, 1, _document.Pages.Count)}";
            RangeInput.Text = $"1-{_document.Pages.Count}";
            ViewRange.IsEnabled = _viewRegion is { IsEmpty: false };
            try
            {
                var list = await printerTask;
                if (_lifetime.IsCancellationRequested) return;
                PrinterBox.ItemsSource = list.Names; PrinterBox.SelectedItem = list.Default ?? list.Names.FirstOrDefault();
                _printer = PrinterBox.SelectedItem as string;
            }
            catch (Exception ex) { StatusLabel.Text = "打印服务不可用；仍可预览：" + ex.Message; }
            finally { _syncing = false; }
            _ready = true; SettingsBar.IsEnabled = true; PropertiesButton.IsEnabled = _printer is not null;
            StartUpdate(); await _renderTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) StatusLabel.Text = "无法打开 PDF：" + ex.Message; }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncing || _printing) return;
        if (ReferenceEquals(sender, GrayCheck)) _monochrome = false;
        PrintButton.IsEnabled = false; ++_renderVersion;
        _refreshTimer.Stop(); _refreshTimer.Start();
    }
    private void Range_Changed(object sender, TextChangedEventArgs e) { if (_ready && !_syncing) CustomRange.IsChecked = true; Option_Changed(sender, e); }
    private void LayoutMode_Changed(object sender, SelectionChangedEventArgs e) { if (e.Source == LayoutTabs) Option_Changed(sender, e); }
    private void Printer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        _printer = PrinterBox.SelectedItem as string; _driverTicket = null; _profiles.Clear();
        PropertiesButton.IsEnabled = _printer is not null; Option_Changed(sender, e);
    }
    private void StartUpdate()
    {
        _renderTask = UpdateAsync(++_renderVersion); _updates.Add(_renderTask);
    }
    private static double Number(TextBox input, double minimum, double maximum, string label)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentException($"{label}应为 {minimum}–{maximum}。");
        return value;
    }
    private PdfPrintOptions ReadOptions() => new()
    {
        Mode = (PdfPrintMode)LayoutTabs.SelectedIndex,
        ScaleMode = ActualScale.IsChecked == true ? PdfPrintScale.Actual : ShrinkScale.IsChecked == true ? PdfPrintScale.Shrink : CustomScale.IsChecked == true ? PdfPrintScale.Custom : PdfPrintScale.Fit,
        Scale = CustomScale.IsChecked == true ? Number(CustomScaleInput, 1, 1000, "打印倍率 (%)") : 100,
        Orientation = OrientationBox.SelectedIndex, NUp = int.Parse((string)((ComboBoxItem)NUpBox.SelectedItem).Content),
        ColumnOrder = OrderBox.SelectedIndex == 1, PageBorder = PageBorderCheck.IsChecked == true,
        RightBinding = BindingBox.SelectedIndex == 1, BookletSide = BookletSideBox.SelectedIndex,
        PosterScale = LayoutTabs.SelectedIndex == 3 ? Number(PosterScaleInput, 10, 1000, "拼接倍率 (%)") : 180,
        OverlapMm = LayoutTabs.SelectedIndex == 3 ? Number(OverlapInput, 0, 30, "重叠边 (mm)") : 5, CutMarks = CutMarksCheck.IsChecked == true,
        Gray = GrayCheck.IsChecked == true, Monochrome = _monochrome, Content = (PdfPrintContent)ContentBox.SelectedIndex, Dpi = _dpi,
        SourcePaper = SourcePaperCheck.IsChecked == true && LayoutTabs.SelectedIndex == 0, Margins = _margins,
        ViewRegion = ViewRange.IsChecked == true ? _viewRegion : null
    };
    private (Size Paper, bool Landscape) PaperFor(Size source, PdfPrintOptions options)
    {
        var paper = options.SourcePaper ? new Size(source.Width * 96 / 72, source.Height * 96 / 72) : _paper;
        var landscape = options.Orientation == 2 || options.Orientation == 0 && (options.Mode == PdfPrintMode.Booklet || options.Mode == PdfPrintMode.Multiple && options.NUp is 2 or 6 || source.Width > source.Height);
        return (new Size(Math.Min(paper.Width, paper.Height), Math.Max(paper.Width, paper.Height)), landscape);
    }
    private static string PaperKey(Size paper, bool landscape) => $"{paper.Width:R}/{paper.Height:R}/{landscape}";

    private async Task UpdateAsync(int version)
    {
        try
        {
            if (!_ready || _document is null || _lifetime.IsCancellationRequested) return;
            _refreshTimer.Stop(); PrintButton.IsEnabled = false;
            var options = ReadOptions(); var copies = Number(CopiesInput, 1, 999, "打印份数");
            if (copies != Math.Truncate(copies)) throw new ArgumentException("打印份数必须是整数。");
            var parity = OddParity.IsChecked == true ? 1 : EvenParity.IsChecked == true ? 2 : 0;
            var source = CurrentRange.IsChecked == true || ViewRange.IsChecked == true ? [Math.Clamp(_readingPage, 1, _document.Pages.Count) - 1] : CustomRange.IsChecked == true ? PdfPrintPages.Parse(RangeInput.Text, _document.Pages.Count) : Enumerable.Range(0, _document.Pages.Count).ToArray();
            var pages = PdfPrintPages.Filter(source, parity, ReverseCheck.IsChecked == true);
            ActualPagesLabel.Text = "实际页码：" + PdfPrintPages.Describe(pages, parity); ActualPagesLabel.ToolTip = ActualPagesLabel.Text;
            if (pages.Length == 0) throw new ArgumentException("当前筛选没有可打印页面。");
            SourcePaperCheck.IsEnabled = options.Mode == PdfPrintMode.Single;
            var layouts = new Dictionary<string, PdfPrintLayout>();
            var printer = _printer; var driverTicket = _driverTicket;
            var perPagePaper = options.Mode is PdfPrintMode.Single or PdfPrintMode.Poster;
            var papers = (perPagePaper ? pages.Select(p => _document.Pages[p]) : [_document.Pages[pages[0]]]).Select(s => PaperFor(s, options)).Distinct().ToArray();
            if (papers.Length > 64) throw new ArgumentException("本次包含超过 64 种纸张尺寸，请分批选择页码。");
            PdfPrinterProfile? firstProfile = null; string? printerError = null;
            foreach (var (paper, landscape) in papers)
            {
                var key = PaperKey(paper, landscape);
                if (printer is null)
                {
                    var oriented = landscape ? new Size(paper.Height, paper.Width) : paper;
                    layouts[key] = new(oriented, PdfPrintLayout.Inset(oriented, 24)); continue;
                }
                if (!_profiles.TryGetValue(key, out var profile))
                {
                    try { profile = await PdfPrinterService.ProfileAsync(printer, driverTicket, paper, landscape); }
                    catch (Exception ex)
                    {
                        if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                        var fallback = landscape ? new Size(paper.Height, paper.Width) : paper;
                        layouts[key] = new(fallback, PdfPrintLayout.Inset(fallback, 24)); printerError = "打印机不可用；使用安全边距预览：" + ex.Message; continue;
                    }
                    if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                    _profiles[key] = profile;
                }
                layouts[key] = profile.Layout; firstProfile ??= profile;
            }
            if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
            UpdateDuplexChoices(firstProfile);
            _faces = PdfPrintPlan.Build(_document.Pages, pages, options, size => { var p = perPagePaper ? PaperFor(size, options) : papers[0]; return layouts[PaperKey(p.Item1, p.Item2)]; });
            if ((long)_faces.Length * copies > 100000) throw new ArgumentException("本次超过 100,000 个输出面，请减少份数或分批打印。");
            _options = options; _page = Math.Clamp(_page, 1, _faces.Length);
            _layout = _faces[_page - 1].Layout;
            var chosen = _faces[_page - 1];
            _syncing = true; PageInput.Text = _page.ToString(); PageCountLabel.Text = $"/ {_faces.Length} 面"; _syncing = false;
            PreviousButton.IsEnabled = _page > 1; NextButton.IsEnabled = _page < _faces.Length;
            var sourceSize = _document.Pages[chosen.Items.FirstOrDefault()?.Page ?? pages[0]];
            PaperInfoLabel.Text = $"PDF · {sourceSize.Width * 25.4 / 72:0.##} × {sourceSize.Height * 25.4 / 72:0.##} mm    纸张 · {_layout.Paper.Width * 25.4 / 96:0.##} × {_layout.Paper.Height * 25.4 / 96:0.##} mm";
            var first = chosen.Items.FirstOrDefault();
            ScaleResultLabel.Text = first is null ? "补齐装订的空白面" : $"当前缩放 {first.Target.Width / first.Source.Width * 72 / 96 * 100:0.##}% · {_faces.Length} 个输出面";
            PreviewWarning.Text = chosen.Items.Any(i => i.Target.Left < i.Clip.Left - .1 || i.Target.Top < i.Clip.Top - .1 || i.Target.Right > i.Clip.Right + .1 || i.Target.Bottom > i.Clip.Bottom + .1) && options.Mode != PdfPrintMode.Poster ? "内容超出可打印区域，边缘将被裁切。" : "";
            StatusLabel.Text = printerError ?? (printer is null ? "未找到打印机；仍可预览。" : "");
            await _renderGate.WaitAsync(_lifetime.Token);
            try
            {
                if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                var drawing = await Task.Run(() => PdfPrintDrawing.Render(_document, chosen, options, 110, _lifetime.Token));
                if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                Sheet.Width = _layout.Paper.Width; Sheet.Height = _layout.Paper.Height;
                PageImage.Width = Sheet.Width; PageImage.Height = Sheet.Height; PageImage.Source = new DrawingImage(drawing);
                Canvas.SetLeft(PrintableGuide, _layout.Printable.X); Canvas.SetTop(PrintableGuide, _layout.Printable.Y);
                PrintableGuide.Width = _layout.Printable.Width; PrintableGuide.Height = _layout.Printable.Height;
                FitSheet(); PrintButton.IsEnabled = printer is not null && printerError is null && !_printing;
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
            StatusLabel.Text = ex.Message; PrintButton.IsEnabled = false; PageImage.Source = null;
        }
    }

    private void UpdateDuplexChoices(PdfPrinterProfile? profile)
    {
        var tag = ((ComboBoxItem)DuplexBox.SelectedItem).Tag as string;
        _syncing = true;
        foreach (var item in DuplexBox.Items.OfType<ComboBoxItem>().Where(i => ((string)i.Tag).StartsWith("auto")).ToArray()) DuplexBox.Items.Remove(item);
        if (profile?.LongDuplex == true) DuplexBox.Items.Add(new ComboBoxItem { Content = "自动 · 长边", Tag = "auto-long" });
        if (profile?.ShortDuplex == true) DuplexBox.Items.Add(new ComboBoxItem { Content = "自动 · 短边", Tag = "auto-short" });
        DuplexBox.SelectedItem = DuplexBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? DuplexBox.Items[0];
        _syncing = false;
    }
    private void FitSheet()
    {
        if (!_ready || PageScroll.ActualWidth < 60 || PageScroll.ActualHeight < 60) return;
        var scale = _autoZoom ? Math.Min((PageScroll.ActualWidth - 64) / Sheet.Width, (PageScroll.ActualHeight - 64) / Sheet.Height) : _zoom;
        SheetView.Width = Sheet.Width * Math.Max(.05, scale); SheetView.Height = Sheet.Height * Math.Max(.05, scale);
    }
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => FitSheet();
    private void Previous_Click(object sender, RoutedEventArgs e) { if (_page > 1) { --_page; StartUpdate(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_page < _faces.Length) { ++_page; StartUpdate(); } }
    private void Page_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; GoToPage(); } }
    private void Page_LostFocus(object sender, RoutedEventArgs e) { if (_ready && !_syncing) GoToPage(); }
    private void GoToPage() { if (int.TryParse(PageInput.Text, out var number)) { _page = Math.Clamp(number, 1, Math.Max(1, _faces.Length)); StartUpdate(); } else PageInput.Text = _page.ToString(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoom = Math.Max(.1, (_autoZoom ? SheetView.Width / Sheet.Width : _zoom) / 1.2); _autoZoom = false; FitSheet(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoom = Math.Min(5, (_autoZoom ? SheetView.Width / Sheet.Width : _zoom) * 1.2); _autoZoom = false; FitSheet(); }
    private void AutoZoom_Click(object sender, RoutedEventArgs e) { _autoZoom = true; FitSheet(); }
    private void Scale_Focus(object sender, KeyboardFocusChangedEventArgs e) { if (_ready) CustomScale.IsChecked = true; }
    private void Scale_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!CustomScaleInput.IsKeyboardFocusWithin || !double.TryParse(CustomScaleInput.Text, out var value)) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : 1;
        CustomScaleInput.Text = Math.Clamp(value + Math.Sign(e.Delta) * step, 1, 1000).ToString("0.00"); e.Handled = true;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (_ready) { _refreshTimer.Stop(); StartUpdate(); } }
    private async void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (_printer is null || _printing) return;
        try
        {
            await FlushAsync();
            var ticket = _profiles.Values.FirstOrDefault()?.Ticket;
            var changed = PdfPrinterService.ShowProperties(new WindowInteropHelper(this).Handle, _printer, ticket);
            if (changed is null) return;
            _driverTicket = changed; _profiles.Clear();
            var settings = PdfPrinterService.ReadTicket(changed);
            _syncing = true;
            if (settings.PageMediaSize?.Width is > 0 && settings.PageMediaSize.Height is > 0) _paper = new Size(settings.PageMediaSize.Width.Value, settings.PageMediaSize.Height.Value);
            OrientationBox.SelectedIndex = settings.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape ? 2 : 1;
            GrayCheck.IsChecked = settings.OutputColor is OutputColor.Grayscale or OutputColor.Monochrome; _monochrome = settings.OutputColor == OutputColor.Monochrome;
            _syncing = false; StartUpdate();
        }
        catch (Exception ex) { _syncing = false; StatusLabel.Text = "打印机属性：" + ex.Message; }
    }
    private void PageSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _printing) return;
        var result = PdfPrintDialogs.PageSetup(this, _paper, _margins, OrientationBox.SelectedIndex, PageImage.Source);
        if (result is null) return;
        _paper = result.Value.Paper; _margins = result.Value.Margins; OrientationBox.SelectedIndex = result.Value.Orientation; StartUpdate();
    }
    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _printing) return;
        if (PdfPrintDialogs.Advanced(this, _dpi, _monochrome ? 2 : GrayCheck.IsChecked == true ? 1 : 0) is { } result)
        { _dpi = result.Dpi; _syncing = true; GrayCheck.IsChecked = result.Color != 0; _monochrome = result.Color == 2; _syncing = false; StartUpdate(); }
    }
    private async void Sheet_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _printing || !_ready) return; e.Handled = true;
        await FlushAsync(); if (_faces.Length == 0 || PageImage.Source is null) return;
        var preview = new PdfPrintPreviewWindow(_document!, _faces, _options, _page - 1) { Owner = this, Title = "打印预览：" + Path.GetFileName(_path) };
        preview.ShowDialog(); _page = preview.FaceIndex + 1; StartUpdate();
    }
    private async Task FlushAsync() { _refreshTimer.Stop(); StartUpdate(); await _renderTask; }
    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();
    private async Task PrintAsync()
    {
        if (!_ready || _printing || _document is null || _printer is null) return;
        await FlushAsync(); if (!PrintButton.IsEnabled || _printing || _lifetime.IsCancellationRequested) return;
        var faces = _faces; var options = _options; var copies = (int)Number(CopiesInput, 1, 999, "打印份数");
        var duplex = (string)((ComboBoxItem)DuplexBox.SelectedItem).Tag;
        if (options.Mode == PdfPrintMode.Booklet && options.BookletSide == 0 && duplex == "none")
        { StatusLabel.Text = "小册子正反两面需要选择自动或手动双面方式。"; return; }
        if (options.Mode == PdfPrintMode.Booklet && options.BookletSide != 0 && duplex != "none")
        { StatusLabel.Text = "只打印小册子正面或背面时，请选择单面。"; return; }
        _printing = true; _cancelRequested = false; PrintButton.IsEnabled = false; SettingsBar.IsEnabled = PrinterSettings.IsEnabled = false;
        PageSetupButton.IsEnabled = AdvancedButton.IsEnabled = false; CancelPrintButton.Visibility = Visibility.Visible;
        try
        {
            using var server = new LocalPrintServer(); using var queue = new PrintQueue(server, _printer);
            var ticket = PdfPrinterService.ReadTicket(_profiles.Values.First().Ticket);
            ticket.CopyCount = 1; ticket.Collation = Collation.Uncollated;
            ticket.OutputColor = options.Monochrome ? OutputColor.Monochrome : options.Gray ? OutputColor.Grayscale : OutputColor.Color;
            ticket.Duplexing = duplex == "auto-long" ? Duplexing.TwoSidedLongEdge : duplex == "auto-short" ? Duplexing.TwoSidedShortEdge : Duplexing.OneSided;
            ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
            if (duplex.StartsWith("auto") && ticket.Duplexing != (duplex == "auto-long" ? Duplexing.TwoSidedLongEdge : Duplexing.TwoSidedShortEdge)) throw new InvalidOperationException("驱动拒绝了所选自动双面方式，请改用手动双面。");
            var output = PdfPrintPlan.Copies(faces, copies, CollateCheck.IsChecked == true, duplex != "none");
            if (duplex.StartsWith("manual"))
            {
                await SubmitAsync(queue, output.Where((_, i) => i % 2 == 0).ToArray(), options, ticket);
                if (_cancelRequested) throw new OperationCanceledException();
                var instruction = PdfPrintDialogs.ManualDuplex(this, output.Length / 2, duplex == "manual-short");
                if (instruction is null || _cancelRequested) throw new OperationCanceledException();
                var backs = output.Where((_, i) => i % 2 == 1).ToArray();
                if (instruction.Value.Reverse) Array.Reverse(backs);
                if (instruction.Value.Rotate) backs = backs.Select(f => f with { Rotate180 = true }).ToArray();
                await SubmitAsync(queue, backs, options, ticket);
            }
            else
            {
                await SubmitAsync(queue, output, options, ticket);
            }
            StatusLabel.Text = "打印任务已提交：" + _printer;
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消后续提交；已发送的页面请在 Windows 打印队列中检查。"; }
        catch (Exception ex) { StatusLabel.Text = "打印失败：" + ex.Message; }
        finally
        {
            _writer = null; _printing = false; PrintButton.IsEnabled = _printer is not null;
            SettingsBar.IsEnabled = PrinterSettings.IsEnabled = PageSetupButton.IsEnabled = AdvancedButton.IsEnabled = true;
            CancelPrintButton.Visibility = Visibility.Collapsed;
        }
    }
    private async Task SubmitAsync(PrintQueue queue, PdfPrintFace[] faces, PdfPrintOptions options, PrintTicket ticket)
    {
        if (_cancelRequested) throw new OperationCanceledException();
        queue.CurrentJobSettings.Description = Path.GetFileName(_path);
        _writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var ticketCache = new Dictionary<Size, PrintTicket>();
        var pageTickets = faces.Select(f =>
        {
            if (ticketCache.TryGetValue(f.Layout.Paper, out var cached)) return cached;
            var validated = _profiles.Values.FirstOrDefault(p => p.Layout.Paper == f.Layout.Paper);
            var t = validated is null ? ticket.Clone() : PdfPrinterService.ReadTicket(validated.Ticket);
            t.CopyCount = 1; t.Collation = Collation.Uncollated; t.Duplexing = ticket.Duplexing; t.OutputColor = ticket.OutputColor;
            if (validated is null) t.PageMediaSize = new PageMediaSize(Math.Min(f.Layout.Paper.Width, f.Layout.Paper.Height), Math.Max(f.Layout.Paper.Width, f.Layout.Paper.Height));
            t.PageOrientation = f.Layout.Paper.Width > f.Layout.Paper.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
            ticketCache[f.Layout.Paper] = t; return t;
        }).ToArray();
        StatusLabel.Text = $"正在提交 {faces.Length} 个输出面…";
        await PdfPrintJob.WriteAsync(_writer, new PdfPrintPaginator(_document!, faces, options), ticket, pageTickets);
        _writer = null;
    }
    private void CancelPrint_Click(object sender, RoutedEventArgs e) => CancelPrint();
    private void CancelPrint() { _cancelRequested = true; try { _writer?.CancelAsync(); } catch (Exception ex) { StatusLabel.Text = "请检查 Windows 打印队列：" + ex.Message; } }
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_printing) Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_printing) { e.Cancel = true; CancelPrint(); StatusLabel.Text = "正在取消提交，请稍候再关闭。"; } }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (!e.IsRepeat) await PrintAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; if (_printing) CancelPrint(); else Close(); }
        else if (e.Key == Key.F5) { e.Handled = true; Refresh_Click(this, e); }
    }
}
