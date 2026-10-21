using System.IO;

namespace TeXeed.App.Services;

internal sealed record DirectoryEntry(string Path, string RelativePath, bool IsDirectory);
internal static class ProjectDirectoryCatalog
{
    // Metadata only: include hidden files, generated output and empty directories.
    // Display junctions/symlinks too, but do not traverse outside the project.
    internal static DirectoryEntry[] Read(string root, CancellationToken cancellation)
    {
        var entries = new List<DirectoryEntry>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new(info.FullName, System.IO.Path.GetRelativePath(root, info.FullName), isDirectory));
                    if (isDirectory && (info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(info.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Other readable directories remain available. */ }
        }
        return entries.ToArray();
    }
}
