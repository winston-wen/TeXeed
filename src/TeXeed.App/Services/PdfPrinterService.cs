using System.IO;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Services;

internal sealed record PdfPrinterProfile(string Name, byte[] Ticket, PdfPrintLayout Layout, bool LongDuplex, bool ShortDuplex, int MaxCopies);
internal sealed record PdfPrinterList(string[] Names, string? Default);

internal static class PdfPrinterService
{
    // Drivers and spooler RPC can be slow. Keep all discovery/capability calls
    // off the UI, in an STA with locally owned PrintSystemObjects.
    internal static Task<T> OnSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true, Name = "PDF printer capabilities" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    internal static Task<PdfPrinterList> ListAsync() => OnSta(() =>
    {
        using var server = new LocalPrintServer();
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        var names = new List<string>();
        foreach (var queue in queues) { using (queue) names.Add(queue.FullName); }
        string? preferred = null;
        try { using var queue = LocalPrintServer.GetDefaultPrintQueue(); preferred = queue.FullName; } catch (PrintSystemException) { }
        return new PdfPrinterList(names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), preferred);
    });

    internal static PrintTicket ReadTicket(byte[]? xml) => xml is null ? new PrintTicket() : new PrintTicket(new MemoryStream(xml, false));

    internal static Task<PdfPrinterProfile> ProfileAsync(string name, byte[]? ticketXml, Size paper, bool landscape) => OnSta(() =>
    {
        using var server = new LocalPrintServer(); using var queue = new PrintQueue(server, name);
        var ticket = ticketXml is null ? queue.UserPrintTicket ?? queue.DefaultPrintTicket : ReadTicket(ticketXml);
        ticket = ticket.Clone();
        var width = Math.Min(paper.Width, paper.Height); var height = Math.Max(paper.Width, paper.Height);
        var caps = queue.GetPrintCapabilities(ticket);
        var media = caps.PageMediaSizeCapability.FirstOrDefault(p => p.Width is { } w && p.Height is { } h && Math.Abs(w - width) < 2 && Math.Abs(h - height) < 2);
        ticket.PageMediaSize = media ?? new PageMediaSize(width, height);
        ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        // TeXeed performs imposition itself. Never apply the driver's N-up or page scaling a second time.
        ticket.PagesPerSheet = 1; ticket.PageScalingFactor = 100;
        ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
        var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
        var layout = PdfPrintLayout.FromPrinter(dialog, paper, false);
        caps = queue.GetPrintCapabilities(ticket);
        return new PdfPrinterProfile(name, ticket.GetXmlStream().ToArray(), layout,
            caps.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge), caps.DuplexingCapability.Contains(Duplexing.TwoSidedShortEdge), caps.MaxCopyCount ?? 999);
    });

    internal static byte[]? ShowProperties(nint owner, string name, byte[]? xml)
    {
        if (!OpenPrinter(name, out var printer, 0)) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var converter = new PrintTicketConverter(name, 1);
            var input = converter.ConvertPrintTicketToDevMode(ReadTicket(xml), BaseDevModeType.UserDefault);
            var length = DocumentProperties(owner, printer, name, 0, 0, 0);
            if (length < 1 || length > 2 * 1024 * 1024) throw new InvalidOperationException("打印机驱动未提供有效的属性数据。");
            var buffer = Marshal.AllocHGlobal(Math.Max(length, input.Length));
            try
            {
                Marshal.Copy(input, 0, buffer, input.Length);
                var result = DocumentProperties(owner, printer, name, buffer, buffer, 14); // IN | OUT | PROMPT
                if (result == 2) return null; // IDCANCEL
                if (result != 1) throw new InvalidOperationException("打印机驱动属性窗口打开失败。");
                var bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, length);
                return converter.ConvertDevModeToPrintTicket(bytes).GetXmlStream().ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(printer); }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out nint printer, nint defaults);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode)]
    private static extern int DocumentProperties(nint owner, nint printer, string name, nint output, nint input, int mode);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(nint printer);
}
