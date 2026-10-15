using System.IO;
using System.Windows;

namespace TeXeed.App;

public partial class MainWindow
{
    private void PrintPreview_Click(object sender, RoutedEventArgs e) => OpenPdfPrint(false);
    private void Print_Click(object sender, RoutedEventArgs e) => OpenPdfPrint(true);

    private void OpenPdfPrint(bool printImmediately)
    {
        if (_closing || _transitioning) return;
        if (_pdfPath is null || !File.Exists(_pdfPath)) { Status("请先编译生成 PDF，再打印或查看打印预览。"); return; }
        var dialog = new PdfPrintWindow(_pdfPath, _project?.Name ?? "TeXeed", Preview.VisiblePage, printImmediately) { Owner = this };
        dialog.ShowDialog();
    }
}
