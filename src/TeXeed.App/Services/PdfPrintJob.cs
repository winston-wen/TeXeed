using System.Printing;
using System.Windows.Documents;
using System.Windows.Documents.Serialization;
using System.Windows.Xps;
using System.Windows.Xps.Serialization;

namespace TeXeed.App.Services;

internal static class PdfPrintJob
{
    internal static async Task WriteAsync(XpsDocumentWriter writer, DocumentPaginator paginator, PrintTicket? ticket = null, IReadOnlyList<PrintTicket>? pageTickets = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WritingCompletedEventHandler completed = (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else if (e.Cancelled) completion.TrySetCanceled();
            else completion.TrySetResult();
        };
        WritingCancelledEventHandler cancelled = (_, _) => completion.TrySetCanceled();
        WritingPrintTicketRequiredEventHandler required = (_, e) =>
        {
            if (pageTickets is not null && e.CurrentPrintTicketLevel == PrintTicketLevel.FixedPagePrintTicket && e.Sequence >= 1 && e.Sequence <= pageTickets.Count)
                e.CurrentPrintTicket = pageTickets[e.Sequence - 1];
        };
        writer.WritingCompleted += completed; writer.WritingCancelled += cancelled;
        writer.WritingPrintTicketRequired += required;
        try { writer.WriteAsync(paginator, ticket); await completion.Task; }
        finally { writer.WritingCompleted -= completed; writer.WritingCancelled -= cancelled; writer.WritingPrintTicketRequired -= required; }
    }
}
