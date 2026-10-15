using System.Printing;
using System.Windows.Documents;
using System.Windows.Documents.Serialization;
using System.Windows.Xps;

namespace TeXeed.App.Services;

internal static class PdfPrintJob
{
    internal static async Task WriteAsync(XpsDocumentWriter writer, DocumentPaginator paginator, PrintTicket? ticket = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WritingCompletedEventHandler completed = (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else if (e.Cancelled) completion.TrySetCanceled();
            else completion.TrySetResult();
        };
        WritingCancelledEventHandler cancelled = (_, _) => completion.TrySetCanceled();
        writer.WritingCompleted += completed; writer.WritingCancelled += cancelled;
        try { writer.WriteAsync(paginator, ticket); await completion.Task; }
        finally { writer.WritingCompleted -= completed; writer.WritingCancelled -= cancelled; }
    }
}
