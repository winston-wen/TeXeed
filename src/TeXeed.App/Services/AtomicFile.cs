using System.IO;

namespace TeXeed.App.Services;

internal static class AtomicFile
{
    // Windows scanners can briefly open the destination without delete-sharing.
    // Keep the old file until replacement succeeds; never fall back to truncating it.
    internal static void Replace(string temporary, string destination)
    {
        int[] waits = [15, 35, 70, 120];
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, destination, true); return; }
            catch (Exception ex) when (attempt < waits.Length && (ex is IOException or UnauthorizedAccessException))
            { Thread.Sleep(waits[attempt]); }
        }
    }
}
