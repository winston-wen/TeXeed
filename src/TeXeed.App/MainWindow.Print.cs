using System.IO;
using System.Windows;

namespace TeXeed.App;

public partial class MainWindow
{
    private void Print_Click(object sender, RoutedEventArgs e) => OpenPdfPrint();

    private void OpenPdfPrint()
    {
        if (_closing || _transitioning) return;
        if (_pdfPath is null || !File.Exists(_pdfPath)) { Status("请先编译生成 PDF，再打印或查看打印预览。"); return; }
        var dialog = new PdfPrintWindow(_pdfPath, _project?.Name ?? "TeXeed", Preview.VisiblePage, viewRegion: Preview.CapturePrintView()) { Owner = this };
        dialog.ShowDialog();
    }
}
