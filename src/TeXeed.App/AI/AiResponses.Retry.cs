using System.Net.Http;

namespace TeXeed.App.AI;

internal sealed partial class AiResponses
{
    private static async ValueTask<string?> ReadResponseLineAsync(System.IO.StreamReader reader, CancellationTokenSource deadline)
    {
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        return await reader.ReadLineAsync(deadline.Token);
    }
    internal static bool IsNetworkFailure(Exception exception, CancellationToken cancellation) => !cancellation.IsCancellationRequested && exception switch
    {
        AiTransientFailureException => true,
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode is 408 or 425 or 500 or 502 or 503 or 504,
        System.IO.IOException => true,
        OperationCanceledException => true,
        _ => false
    };
}
