using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckProjectCommentStoreAsync(string output)
    {
        var root = Path.Combine(output, "project-comment-fixture"); Directory.CreateDirectory(root);
        var main = Path.Combine(root, "main.tex"); var other = Path.Combine(root, "chapter.tex");
        var store = ProjectCommentStore.Load(main, root);
        var doc = store.GetOrCreate(main, "one\ntwo");
        doc.SharedDocument.Comments.Add(new AiDocumentComment { Start = 4, Length = 3, Quote = "two", Text = "why", AnchorRevision = doc.SharedDocument.Revision });
        doc.CommentDrafts.NewComment = "draft";
        var otherDoc = store.GetOrCreate(other, "other");
        if (ReferenceEquals(doc, otherDoc) || otherDoc.SharedDocument.Comments.Count != 0) throw new Exception("Project comment files mixed.");
        await store.SaveAsync();
        var restored = ProjectCommentStore.Load(main, root);
        var loaded = restored.GetOrCreate(main, "one\ntwo");
        if (loaded.SharedDocument.Comments.Single().Text != "why" || loaded.CommentDrafts.NewComment != "draft") throw new Exception("Project comments or draft lost on reload.");
        restored.GetOrCreate(main, "prefix one\ntwo");
        if (loaded.SharedDocument.Comments.Single().Start != 11) throw new Exception("Project source edit failed to move anchor.");
        if (!restored.HasAnyData || !restored.HasDataFor(main) || restored.HasDataFor(other)) throw new Exception("Project rename protection did not identify comments.");
        var concurrent = ProjectCommentStore.Load(main, root);
        await restored.SaveAsync();
        var conflict = false;
        try { await concurrent.SaveAsync(); } catch (IOException) { conflict = true; }
        if (!conflict) throw new Exception("Concurrent comment store write overwrote newer data.");
        var outside = false;
        try { restored.GetOrCreate(Path.Combine(root, "..", "outside.tex"), "x"); } catch (InvalidOperationException) { outside = true; }
        if (!outside) throw new Exception("Project comment store accepted an outside path.");
        Console.WriteLine("Project comment store: isolation, persistence, anchor update, rename protection and conflict checks passed.");
    }
}
