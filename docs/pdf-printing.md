# PDF 打印与打印预览（0.3.11）

在“文件”菜单选择“打印 PDF…”（Ctrl+P）或“PDF 打印预览…”（Ctrl+Shift+P）。需要已有编译好的 PDF；不会为打印额外启动编译。打开窗口时读取独立的 PDF 快照，之后重新编译、裁边或调整阅读配色不会改变此打印任务。

打印预览可以输入页码、前后翻页，选择 A4、A3 或 Letter、横向／纵向，以及“适合可打印区域”或“实际大小 (100%)”。起始页取阅读预览中央所在页。虚线表示默认打印机的硬件可打印边界，不会印出。没有可用打印服务时仍可预览，暂用 24 DIP 安全边距；系统打印对话框最终选定的打印机、纸张和方向决定实际边界。

点击“打印…”后，在 Windows 打印对话框选择打印机、全部页／当前页／连续页码范围、份数及驱动支持的双面等选项，确认后才提交。此处“当前页”指打印预览中显示的页。使用实际大小时，超出硬件可打印区域的内容会裁切，界面会提示。

`PdfPrintLayout` 统一处理纸张、硬件边距、等比居中和页码范围；`PdfPrintPaginator` 与屏幕预览使用同一放置算法，按需逐页调用 PDFium 打印模式渲染。输出目标为 300 dpi，单页限制 32 MP／单边 10,000 像素，异常大纸张会在预算内降低分辨率。打印使用原色完整页，保留注释，不使用 LCD 子像素抗锯齿。当前实现将页面栅格化后交给 WPF 打印管线，不保留 PDF 文本的矢量属性。

`PdfPrintJob` 通过 WPF XPS 异步写入打印队列；可取消尚未提交完成的任务。已经送进设备的页面以 Windows 队列状态为准。关闭或取消时等待任务结束，独立 PDF 句柄不与阅读预览共用。不会预先将全书所有页面渲染进内存。

验证：`TeXeed.AppTests.exe --print-only --output artifacts/ui-print` 使用独立的三页彩色 PDF，检查横竖混排、页码边界、当前页、300 dpi、原始配色、硬件边距、实际大小、异步 XPS 输出／取消、深浅主题、翻页及加载中关闭，并生成界面截图；不向实体打印机发送任务。`--caption-only` 验证标题栏宽窄布局、产品图标顺序、最小宽度、系统菜单与窗口按钮，不要求桌面可见时的全屏遮挡检测；原 `--titlebar-only` 仍包含完整全屏测试。

实现参考：[WPF 打印对话框与页码范围](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/documents/how-to-display-print-dialog)、[XpsDocumentWriter.WriteAsync](https://learn.microsoft.com/en-us/dotnet/api/system.windows.xps.xpsdocumentwriter.writeasync?view=windowsdesktop-10.0)、[PDFium 渲染标记](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdfview.h)。
