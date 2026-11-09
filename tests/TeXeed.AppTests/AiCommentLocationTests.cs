using TeXeed.App.AI;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiCommentLocationAsync()
    {
        var source = "第一行\n公式 $x^2$\n末尾";
        var document = new AiSharedDocument();
        if (!document.TryInitialize(source, "模型")) throw new Exception("Document setup failed");
        var comment = new AiDocumentComment { Start = source.IndexOf("公式", StringComparison.Ordinal), Length = 8, Quote = "公式 $x^2$", AnchorRevision = document.Revision };
        var anchor = AiCommentLocator.SourceAnchor(document, comment, "document.tex");
        if (anchor is null || anchor.Line != 2 || anchor.Column != 1) throw new Exception("Incorrect source anchor");
        comment.Orphaned = true;
        if (AiCommentLocator.SourceAnchor(document, comment, "document.tex") is not null) throw new Exception("Orphan must not map");
        comment.Orphaned = false;
        comment.Quote = "不同的内容";
        if (AiCommentLocator.SourceAnchor(document, comment, "document.tex") is not null) throw new Exception("Mismatched quote must not map");
        comment.Quote = "公式 $x^2$";
        document.Comments.Add(comment);
        if (document.Snapshot().Comments[0].AnchorRevision != document.Revision) throw new Exception("Anchor revision was lost from snapshot");
        var project = new TexProject(".", "document.tex", [new ProjectFile("document.tex", "document.tex", ProjectFileKind.Tex, source)], []);
        var stale = await AiCommentLocator.LocateAsync(document, comment, "old revision", project, "missing.pdf", new AppSettings(), new SyncTexService());
        if (stale.Status != AiCommentLocationStatus.PreviewOutdated || stale.Location is not null) throw new Exception("Stale preview was mapped");
        var missing = await AiCommentLocator.LocateAsync(document, comment, document.Revision, project, "missing.pdf", new AppSettings(), new SyncTexService());
        if (missing.Status != AiCommentLocationStatus.Unavailable || missing.Location is not null) throw new Exception("Missing PDF was mapped");
        Console.WriteLine("Comment source anchors and stale preview protection verified.");
    }
}
