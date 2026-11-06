using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TeXeed.App.AI;

internal static partial class Program
{
    private static async Task CheckAiSharedDocumentAsync(string output)
    {
        void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
        var document = new AiSharedDocument();
        Check(document.TryInitialize("甲中乙😀丁", "model-alpha"), "Shared document initial answer was not saved");
        document.Comments.Add(new AiDocumentComment { Start = 1, Length = 1, Quote = "中", Text = "检查这里", Author = "用户", RoundId = "round-1", Replies = [new AiDocumentReply { Author = "model-alpha", Text = "需要条件" }] });
        var original = document.Snapshot();
        var revision = document.Revision;
        Check(document.TryApplyEdits(revision, [new AiDocumentEdit(0, 1, "首"), new AiDocumentEdit(5, 1, "末")], "model-beta", "ai"), "Non-overlapping edits were rejected");
        Check(document.Source == "首中乙😀末" && document.Comments.Single().Start == 1 && !document.Comments.Single().Orphaned, "Disjoint edits corrupted the untouched comment");
        Check(document.AuthorSpans.Any(span => span.Author == "model-alpha" && span.Start <= 1 && span.Start + span.Length > 1), "Unchanged content lost its original author");
        Check(document.AuthorSpans.Count(span => span.Author == "model-beta") == 2 && document.AuthorSpans.Where(span => span.Author == "model-beta").All(span => span.Role == "ai"), "Disjoint model edits lost author identity");
        Check(!document.TryApply(revision, "过期内容", "用户"), "Stale revision overwrote the document");
        var beforeInvalid = document.Snapshot();
        Check(!document.TryApplyEdits(document.Revision, [new AiDocumentEdit(1, 2, "x"), new AiDocumentEdit(2, 1, "y")], "用户"), "Overlapping edits were accepted");
        Check(!document.TryApplyEdits(document.Revision, [new AiDocumentEdit(4, 0, "x")], "用户"), "An edit split a surrogate pair");
        Check(!document.TryApplyEdits(document.Revision, [new AiDocumentEdit(0, 0, "\ud800")], "用户"), "An unpaired surrogate was accepted");
        Check(document.Source == beforeInvalid.Source && document.Comments.Single().Quote == "中", "Rejected edits changed source or comments");
        Check(document.TryApply(document.Revision, "前" + document.Source, "用户"), "Student insertion failed");
        Check(document.Comments.Single().Start == 2 && document.AuthorSpans.First().Author == "用户" && document.AuthorSpans.First().Role == "user", "Insertion did not preserve the comment or user author");
        Check(document.TryApplyEdits(document.Revision, [new AiDocumentEdit(2, 1, "")], "用户"), "Student deletion failed");
        Check(document.Comments.Single().Orphaned && document.Comments.Single().Text == "检查这里", "Deleted anchor lost its comment");
        Check(original.Source == "甲中乙😀丁" && original.Comments.Single().Replies.Single().Text == "需要条件", "Snapshot changed with the live document");
        var restoreTarget = document.Snapshot();
        var staleRestore = document.Snapshot();
        document.Comments.Single().Text = "并发评论";
        Check(!document.TryRestore(staleRestore, original) && document.Comments.Single().Text == "并发评论", "Restore overwrote a concurrent comment edit");
        Check(document.TryRestore(document.Snapshot(), restoreTarget) && document.Source == restoreTarget.Source && document.Comments.Single().Orphaned, "Restore did not recover source and comments");

        var book = new AiNotebook(); var conversation = book.NewConversation(); conversation.SharedDocument = document;
        var snapshot = book.Snapshot(); document.Comments.Single().Text = "修改后的批注";
        Check(snapshot.Active.SharedDocument.Comments.Single().Text == "检查这里", "Notebook snapshot shared mutable comments");
        var project = Path.Combine(output, "source-project", "main.tex"); Directory.CreateDirectory(Path.GetDirectoryName(project)!); File.WriteAllText(project, "PROJECT-UNCHANGED");
        var directory = AiDocumentRepository.GetDirectory(project, conversation.Id);
        Check(!directory.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Conversation Git repository is inside the user's TeX project");
        var first = await AiDocumentRepository.SaveVersionAsync(project, conversation.Id, original, "model-alpha", "初稿");
        var second = await AiDocumentRepository.SaveVersionAsync(project, conversation.Id, document, "用户名", "用户修改");
        Check(first != second && (first.Length is 40 or 64) && (second.Length is 40 or 64), "Git versions were not created");
        var versions = await AiDocumentRepository.ListVersionsAsync(project, conversation.Id);
        Check(versions.Count >= 2 && versions[0].Commit == second && versions[0].Author == "用户名" && versions[1].Author == "model-alpha", "Version list lost order or author names");
        var loaded = await AiDocumentRepository.ReadVersionAsync(project, conversation.Id, first);
        Check(loaded.Source == original.Source && loaded.Comments.Single().Replies.Single().Author == "model-alpha", "Git version did not preserve source and comments");
        Check(File.ReadAllText(project) == "PROJECT-UNCHANGED", "Conversation version modified the TeX project");
        var stored = Path.Combine(directory, "document.tex");
        await File.WriteAllTextAsync(stored, "EXTERNAL-UNSAVED", new UTF8Encoding(false));
        var blocked = false;
        try { await AiDocumentRepository.SaveVersionAsync(project, conversation.Id, document, "用户", "不可覆盖外部修改"); }
        catch (IOException) { blocked = true; }
        Check(blocked && await File.ReadAllTextAsync(stored) == "EXTERNAL-UNSAVED", "Dirty Git worktree was overwritten");
        Console.WriteLine("PASS shared LaTeX revision, author ranges, comments, Git history and dirty-worktree protection");
    }
}
