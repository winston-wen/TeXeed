using System.IO;
using System.Windows;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckAiAttachmentGroupingAsync(Window owner, string output)
    {
        var checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(output, "grouped-fixture");
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(first, "sub")); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "one.txt"), "one");
        File.WriteAllText(Path.Combine(first, "sub", "two.txt"), "two");
        File.WriteAllText(Path.Combine(second, "three.txt"), "three");
        var standalone = Path.Combine(root, "loose.txt"); File.WriteAllText(standalone, "loose");
        var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        var window = new Window { Owner = owner, Content = panel, Width = 850, Height = 600 }; window.Show();
        try
        {
            var project = Path.Combine(output, "grouped.tex"); await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            Check(await panel.AddAttachmentsAsync([first, standalone, second]), "Could not add mixed files and folders");
            var entry = book.Entries.Single(); var groups = entry.AttachmentItems;
            Check(entry.Files.Count == 4 && groups.Count == 3, "Folder contents were not grouped");
            Check(groups.Count(g => g.IsFolder) == 2 && groups.Any(g => !g.IsFolder && g.DisplayPath == "loose.txt"), "Mixed folder and file grouping failed");
            Check(groups[0].DisplayPath == first + "\\" && groups[0].Files.Count == 2 && groups[2].DisplayPath == second + "\\", "Folder path is not displayed with trailing separator");
            Check(entry.Files.All(file => !string.IsNullOrWhiteSpace(file.Base64)), "Grouping changed attachment payloads");
            await panel.SaveAsync();
            var restored = AiStorage.Load(project).Entries.Single();
            Check(restored.AttachmentItems.Select(g => g.DisplayPath).SequenceEqual(groups.Select(g => g.DisplayPath)) && restored.Files.Count == 4, "Folder paths or payloads did not survive reload");
            var legacy = new AiEntry { Files = [new("old/a.txt", "text/plain", "YQ=="), new("old/sub/b.txt", "text/plain", "Yg=="), new("standalone.txt", "text/plain", "Yw==")] };
            Check(legacy.AttachmentItems.Count == 2 && legacy.AttachmentItems[0].DisplayPath == "old/" && legacy.AttachmentItems[0].Files.Count == 2, "Legacy relative paths did not group conservatively");
            var mixed = new AiEntry { Question = "保留评论", AnswerAnchor = new("turn", "segment", 0, 1, "文", "revision"), Files = [new("old/a.txt", "text/plain", "YQ==")] };
            Check(mixed.HasReference && mixed.HasAttachments && mixed.ReferenceSelected && mixed.AttachmentSelected, "Legacy mixed entry did not preserve both categories");
            mixed.ReferenceSelected = false; var copy = mixed.Snapshot();
            Check(!copy.ReferenceSelected && copy.AttachmentSelected && copy.HasReference && copy.HasAttachments, "Independent choices were not preserved by snapshot");
            panel.RemoveAttachmentItem(entry, groups[0]);
            Check(entry.Files.Count == 2 && entry.AttachmentItems.All(g => g.DisplayPath != first + "\\"), "Removing a folder removed incorrect files");
            Check(!entry.Title.Contains("one.txt", StringComparison.Ordinal) && entry.Title == "附件：2 项", "Removing a folder left an obsolete title");
            Check(entry.Files.Any(file => file.Name == "loose.txt") && entry.Files.Any(file => file.FolderPath == second), "Removing folder lost unrelated payloads");
            await panel.SaveAsync();
            Check(AiStorage.Load(project).Entries.Single().Files.Count == 2, "Removed folder returned after reload");
            Console.WriteLine($"AI attachment grouping: {checks} checks passed.");
        }
        finally { window.Content = null; panel.Dispose(); window.Close(); }
    }
}
