using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Xps;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class PdfPrintWindow : Window
{
    private readonly string _path;
    private readonly bool _printImmediately;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _renderGate = new(1);
    private readonly PrintDialog _dialog = new() { UserPageRangeEnabled = true, CurrentPageEnabled = true };
    private PdfDocument? _document;
    private PdfPrintLayout _layout = new(PdfPrintLayout.A4, PdfPrintLayout.Inset(PdfPrintLayout.A4, 24));
    private int _page, _renderVersion;
    private bool _ready, _printing;
    private XpsDocumentWriter? _writer;
    private Task _renderTask = Task.CompletedTask;
    private Task _loadTask = Task.CompletedTask;

    internal PdfPrintWindow(string path, string title, int page = 1, bool printImmediately = false)
    {
        InitializeComponent(); WindowTheme.Attach(this);
        _path = path; _page = page; _printImmediately = printImmediately;
        DocumentTitle.Text = title; Title = title + " — PDF 打印预览";
        Loaded += async (_, _) => { _loadTask = LoadAsync(); await _loadTask; };
        PageScroll.SizeChanged += (_, _) => FitSheet();
        Closing += Window_Closing;
        Closed += async (_, _) =>
        {
            _lifetime.Cancel(); ++_renderVersion;
            // PDFium may still be rendering on its worker. Never close its
            // native handle until the outstanding work has finished.
            try { await _loadTask; await _renderTask; } catch (OperationCanceledException) { }
            _document?.Dispose();
            try { _dialog.PrintQueue?.Dispose(); } catch (PrintSystemException) { }
            _lifetime.Dispose();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            _document = await Task.Run(() =>
            {
                using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 256L * 1024 * 1024) throw new IOException("PDF 文件超过 256 MB，无法打印。");
                using var bytes = new MemoryStream(); file.CopyTo(bytes);
                _lifetime.Token.ThrowIfCancellationRequested();
                return new PdfDocument(bytes.ToArray());
            }, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _page = Math.Clamp(_page, 1, _document.Pages.Count);
            _dialog.MinPage = 1; _dialog.MaxPage = (uint)_document.Pages.Count;
            _dialog.PageRange = new PageRange(1, _document.Pages.Count);
            // Reading the default printer can fail when the spooler is disabled.
            // Preview remains usable without any installed printer.
            try
            {
                var ticket = _dialog.PrintTicket;
                var media = ticket?.PageMediaSize?.PageMediaSizeName;
                foreach (ComboBoxItem item in PaperBox.Items)
                    if (Enum.TryParse<PageMediaSizeName>((string)item.Tag, out var name) && name == media) PaperBox.SelectedItem = item;
                OrientationBox.SelectedIndex = ticket?.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape ? 1 : 0;
            }
            catch (PrintSystemException) { }
            _ready = true; PrintButton.IsEnabled = true; SettingsBar.IsEnabled = true;
            ApplySettings();
            await _renderTask;
            if (_printImmediately && !_lifetime.IsCancellationRequested && await PrintAsync()) Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) StatusLabel.Text = "无法打开或打印 PDF：" + ex.Message; }
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ApplySettings(); }

    private void ApplySettings()
    {
        var paperName = Enum.Parse<PageMediaSizeName>((string)((ComboBoxItem)PaperBox.SelectedItem).Tag);
        var paper = paperName switch
        {
            PageMediaSizeName.ISOA3 => new Size(297 * 96 / 25.4, 420 * 96 / 25.4),
            PageMediaSizeName.NorthAmericaLetter => new Size(816, 1056),
            _ => PdfPrintLayout.A4
        };
        if (OrientationBox.SelectedIndex == 1) paper = new Size(paper.Height, paper.Width);
        _layout = new(paper, PdfPrintLayout.Inset(paper, 24), ScaleBox.SelectedIndex == 1);
        try
        {
            var ticket = _dialog.PrintTicket?.Clone() ?? new PrintTicket();
            ticket.PageMediaSize = new PageMediaSize(paperName);
            ticket.PageOrientation = OrientationBox.SelectedIndex == 1 ? PageOrientation.Landscape : PageOrientation.Portrait;
            _dialog.PrintTicket = ticket;
            _layout = PdfPrintLayout.FromPrinter(_dialog, paper, ScaleBox.SelectedIndex == 1);
            StatusLabel.Text = _dialog.PrintQueue is { } queue ? "打印机：" + queue.FullName : "未选择打印机；仍可预览。";
        }
        catch (PrintSystemException) { StatusLabel.Text = "打印服务不可用；使用纸张安全边距预览。"; }
        QueuePage();
    }

    private void QueuePage()
    {
        if (!_ready || _lifetime.IsCancellationRequested) return;
        PageInput.Text = _page.ToString(); PageCountLabel.Text = $"/ {_document!.Pages.Count}";
        PreviousButton.IsEnabled = _page > 1; NextButton.IsEnabled = _page < _document.Pages.Count;
        _renderTask = RenderPageAsync(++_renderVersion, _page - 1, _layout);
    }

    private async Task RenderPageAsync(int version, int index, PdfPrintLayout layout)
    {
        try
        {
            await _renderGate.WaitAsync(_lifetime.Token);
            try
            {
                if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                var target = layout.Place(_document!.Pages[index]);
                var image = await Task.Run(() => _document.RenderForPrint(index, 1.6, 4 * 1024 * 1024), _lifetime.Token);
                if (version != _renderVersion || _lifetime.IsCancellationRequested) return;
                Sheet.Width = layout.Paper.Width; Sheet.Height = layout.Paper.Height;
                Canvas.SetLeft(PageImage, target.X); Canvas.SetTop(PageImage, target.Y);
                PageImage.Width = target.Width; PageImage.Height = target.Height; PageImage.Source = image;
                PageImage.Clip = new System.Windows.Media.RectangleGeometry(new Rect(
                    Math.Max(0, layout.Printable.X - target.X), Math.Max(0, layout.Printable.Y - target.Y),
                    Math.Min(target.Width, layout.Printable.Width), Math.Min(target.Height, layout.Printable.Height)));
                Canvas.SetLeft(PrintableGuide, layout.Printable.X); Canvas.SetTop(PrintableGuide, layout.Printable.Y);
                PrintableGuide.Width = layout.Printable.Width; PrintableGuide.Height = layout.Printable.Height;
                FitSheet();
                if (layout.ActualSize && (target.Width > layout.Printable.Width + .1 || target.Height > layout.Printable.Height + .1))
                    StatusLabel.Text = "实际大小超出可打印范围，边缘会被裁掉。可改为“适合可打印区域”。";
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) StatusLabel.Text = "页面预览失败：" + ex.Message; }
    }

    private void FitSheet()
    {
        if (PageScroll.ViewportWidth <= 48 || PageScroll.ViewportHeight <= 48) return;
        var scale = Math.Min((PageScroll.ViewportWidth - 48) / Sheet.Width, (PageScroll.ViewportHeight - 48) / Sheet.Height);
        SheetView.Width = Sheet.Width * scale; SheetView.Height = Sheet.Height * scale;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) { if (_page > 1) { --_page; QueuePage(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_page < _document?.Pages.Count) { ++_page; QueuePage(); } }
    private void Page_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; GoToPage(); } }
    private void Page_LostFocus(object sender, RoutedEventArgs e) { if (_ready) GoToPage(); }
    private void GoToPage()
    {
        if (_document is null) return;
        if (int.TryParse(PageInput.Text, out var number) && number >= 1 && number <= _document.Pages.Count) { _page = number; QueuePage(); }
        else { PageInput.Text = _page.ToString(); StatusLabel.Text = $"请输入 1–{_document.Pages.Count} 内的页码。"; }
    }

    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();
    private async Task<bool> PrintAsync()
    {
        if (!_ready || _printing || _document is null || _lifetime.IsCancellationRequested) return false;
        _printing = true; PrintButton.IsEnabled = false; SettingsBar.IsEnabled = false;
        try
        {
            if (_dialog.ShowDialog() != true) return true;
            var queue = _dialog.PrintQueue ?? throw new InvalidOperationException("请选择打印机。");
            var ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, _dialog.PrintTicket).ValidatedPrintTicket;
            _dialog.PrintTicket = ticket;
            _layout = PdfPrintLayout.FromPrinter(_dialog, _layout.Paper, _layout.ActualSize);
            QueuePage(); await _renderTask;
            var pages = PdfPrintLayout.Pages(_document.Pages.Count, _dialog.PageRangeSelection, _dialog.PageRange, _page);
            var paginator = new PdfPrintPaginator(_document, _layout, pages);
            queue.CurrentJobSettings.Description = DocumentTitle.Text;
            _writer = PrintQueue.CreateXpsDocumentWriter(queue);
            CancelPrintButton.Visibility = Visibility.Visible;
            StatusLabel.Text = $"正在提交 {pages.Length} 页到打印机…";
            await PdfPrintJob.WriteAsync(_writer, paginator, ticket);
            StatusLabel.Text = $"已提交 {pages.Length} 页到打印机：{queue.FullName}";
            return true;
        }
        catch (OperationCanceledException) { StatusLabel.Text = "已取消打印提交。已发送到打印机的页面请在系统队列中检查。"; return true; }
        catch (Exception ex) { StatusLabel.Text = "打印失败：" + ex.Message; return false; }
        finally
        {
            _writer = null; _printing = false; PrintButton.IsEnabled = true; SettingsBar.IsEnabled = true;
            CancelPrintButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelPrint_Click(object sender, RoutedEventArgs e) => CancelPrint();
    private void CancelPrint()
    {
        try { _writer?.CancelAsync(); }
        catch (Exception ex) { StatusLabel.Text = "无法取消此打印提交，请检查 Windows 打印队列：" + ex.Message; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_printing) return;
        e.Cancel = true; CancelPrint();
        StatusLabel.Text = "正在取消打印提交，请稍候再关闭窗口。";
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (!e.IsRepeat) await PrintAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; if (_printing) CancelPrint(); else Close(); }
    }
}
