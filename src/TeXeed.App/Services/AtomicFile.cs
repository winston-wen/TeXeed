using System.IO;

namespace TeXeed.App.Services;

internal static class AtomicFile
{
    private static readonly int[] RetryDelays = [15, 35, 70, 120];

    // An open handle continues to see its own complete published file after a
    // rename. Allow that rename while reading; never open the writer's staging file.
    internal static string? ReadTextIfPresent(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException ex) when (attempt < RetryDelays.Length && OperatingSystem.IsWindows() &&
                (ex.HResult & 0xFFFF) is 32 or 33)
            { Thread.Sleep(RetryDelays[attempt]); }
        }
    }

    // Windows scanners can briefly open the destination without delete-sharing.
    // Keep the old file until replacement succeeds; never fall back to truncating it.
    internal static void Replace(string temporary, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, destination, true); return; }
            catch (Exception ex) when (attempt < RetryDelays.Length && (ex is IOException or UnauthorizedAccessException))
            { Thread.Sleep(RetryDelays[attempt]); }
        }
    }
}
