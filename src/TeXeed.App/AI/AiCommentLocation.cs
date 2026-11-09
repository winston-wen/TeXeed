using TeXeed.Core;

namespace TeXeed.App.AI;

internal enum AiCommentLocationStatus { Approximate, PreviewOutdated, Unavailable }

internal sealed record AiCommentLocation(AiCommentLocationStatus Status, PdfLocation? Location);

/// <summary>Maps an anchored source range to a SyncTeX location without claiming a text highlight.</summary>
internal static class AiCommentLocator
{
    internal static SourceLocation? SourceAnchor(AiSharedDocument document, AiDocumentComment comment, string sourcePath)
    {
        var source = document.Source;
        if (comment.Orphaned || comment.Start < 0 || comment.Length <= 0 || comment.Start > source.Length ||
            comment.Length > source.Length - comment.Start || string.IsNullOrEmpty(comment.Quote) ||
            !source.AsSpan(comment.Start, comment.Length).SequenceEqual(comment.Quote.AsSpan())) return null;
        var line = 1; var column = 1;
        for (var i = 0; i < comment.Start; i++)
        {
            if (source[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return new SourceLocation(sourcePath, line, column);
    }

    internal static async Task<AiCommentLocation> LocateAsync(AiSharedDocument document, AiDocumentComment comment,
        string compiledRevision, TexProject project, string pdfPath, AppSettings settings, SyncTexService sync,
        CancellationToken cancellationToken = default)
    {
        var snapshot = document.Snapshot();
        if (!string.Equals(snapshot.Revision, compiledRevision, StringComparison.Ordinal))
            return new(AiCommentLocationStatus.PreviewOutdated, null);
        var main = project.Files.FirstOrDefault(file => string.Equals(file.Path, project.MainPath, StringComparison.OrdinalIgnoreCase));
        if (main is null || main.Content != snapshot.Source)
            return new(AiCommentLocationStatus.Unavailable, null);
        var frozenComment = snapshot.Comments.FirstOrDefault(item => item.Id == comment.Id);
        if (frozenComment is null) return new(AiCommentLocationStatus.Unavailable, null);
        var anchor = SourceAnchor(snapshot, frozenComment, project.MainPath);
        if (anchor is null) return new(AiCommentLocationStatus.Unavailable, null);
        var location = await sync.ForwardViewportAsync(project, pdfPath, anchor, settings, cancellationToken);
        var current = document.Snapshot();
        if (!string.Equals(current.Revision, compiledRevision, StringComparison.Ordinal))
            return new(AiCommentLocationStatus.PreviewOutdated, null);
        var currentComment = current.Comments.FirstOrDefault(item => item.Id == frozenComment.Id);
        if (currentComment is null || currentComment.Start != frozenComment.Start || currentComment.Length != frozenComment.Length ||
            currentComment.Quote != frozenComment.Quote || currentComment.Orphaned != frozenComment.Orphaned)
            return new(AiCommentLocationStatus.Unavailable, null);
        return location is null ? new(AiCommentLocationStatus.Unavailable, null) : new(AiCommentLocationStatus.Approximate, location);
    }
}
