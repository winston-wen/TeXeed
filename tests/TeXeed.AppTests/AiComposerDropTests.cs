using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiComposerDropAsync(MainWindow window, string output)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Until(Func<bool> ready, string message)
        {
            for (var i = 0; i < 200 && !ready(); i++) await Task.Delay(20);
            Check(ready(), message);
        }
        static DragEventArgs Drag(IDataObject data, DragDropKeyStates keys, DragDropEffects allowed, UIElement target, RoutedEvent routed)
        {
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
                [data, keys, allowed, target, new Point(4, 4)], null)!;
            args.RoutedEvent = routed; return args;
        }
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var project = (TexProject)Field(window, "_project")!;
        ai.ProjectRoot = () => project.Root;
        await ai.SetProjectAsync(project.MainPath);
        var book = (AiNotebook)Field(ai, "_book")!;
        book.Entries.Clear();
        var composer = (TextBox)ai.FindName("Composer");
        composer.Text = "已有草稿，继续编辑。"; composer.CaretIndex = composer.Text.Length;
        var draft = composer.Text;
        var projectFile = Path.Combine(project.Root, "drop-project.tex"); File.WriteAllText(projectFile, "project contents");
        var external = Path.Combine(output, "drop-external.txt"); File.WriteAllText(external, "external contents");
        var folder = Path.Combine(output, "drop-folder"); Directory.CreateDirectory(folder);
        var data = new DataObject(DataFormats.FileDrop, new[] { projectFile, folder, external });
        var over = Drag(data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, composer, DragDrop.PreviewDragOverEvent);
        composer.RaiseEvent(over);
        Check(over.Handled && over.Effects == DragDropEffects.Copy, "Explorer file drag was not accepted as copy");
        var drop = Drag(data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, composer, DragDrop.PreviewDropEvent);
        composer.RaiseEvent(drop);
        await Until(() => book.Entries.Count == 1, "Explorer file and directory drop was not added");
        var mixed = book.Entries.Single();
        Check(drop.Handled && mixed.Files.Count == 3 && mixed.Files[0].BlobId is not null && mixed.Files[0].ReferenceKind is null &&
            mixed.Files[1].ReferenceKind == "directory" && mixed.Files[2].BlobId is not null && composer.Text == draft,
            "Mixed Explorer drop lost source order, attachment semantics or draft text");
        await ai.SaveAsync();
        var saved = AiStorage.Load(project.MainPath);
        Check(saved.Draft == draft && saved.Entries.Single().Files.Select(file => file.ReferenceKind).SequenceEqual(new string?[] { null, "directory", null }),
            "Dropped attachments or draft did not survive save/reload");
        book.Entries.Clear();

        var tree = new DataObject("TeXeed.ProjectFile", projectFile);
        var treeOver = Drag(tree, DragDropKeyStates.LeftMouseButton, DragDropEffects.Link, composer, DragDrop.PreviewDragOverEvent);
        composer.RaiseEvent(treeOver);
        Check(treeOver.Handled && treeOver.Effects == DragDropEffects.Link, "Project tree Link drag was not accepted");
        var treeDrop = Drag(tree, DragDropKeyStates.LeftMouseButton, DragDropEffects.Link, composer, DragDrop.PreviewDropEvent);
        composer.RaiseEvent(treeDrop);
        await Until(() => book.Entries.Count == 1, "Project tree drop was not added");
        Check(treeDrop.Handled && book.Entries.Single().Files.Single().BlobId is not null && composer.Text == draft,
            "Ordinary project tree drop did not create a snapshot");
        book.Entries.Clear();
        var shifted = Drag(tree, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey, DragDropEffects.Link, composer, DragDrop.PreviewDropEvent);
        composer.RaiseEvent(shifted);
        await Until(() => book.Entries.Count == 1, "Shift project tree drop was not added");
        Check(shifted.Handled && book.Entries.Single().Files.Single().ReferenceKind == "file" &&
            book.Entries.Single().Files.Single().ReferencePath == projectFile && composer.Text == draft,
            "Shift project tree drop did not create a path reference");
        book.Entries.Clear();
        var outsideData = new DataObject(DataFormats.FileDrop, new[] { external });
        var outside = Drag(outsideData, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey, DragDropEffects.Copy, composer, DragDrop.PreviewDropEvent);
        composer.RaiseEvent(outside);
        await Until(() => book.Entries.Count == 1, "Shift external file drop was not added");
        Check(book.Entries.Single().Files.Single().BlobId is not null && book.Entries.Single().Files.Single().ReferenceKind is null,
            "Shift made an outside-project Explorer file a project reference");
        book.Entries.Clear();
        var textData = new DataObject(DataFormats.UnicodeText, "文字拖拽");
        Check(!AiPanel.TryComposerDropPaths(textData, out _), "Text drag was incorrectly classified as an attachment");
        var badFile = Path.Combine(output, "drop-unsupported.bin"); File.WriteAllBytes(badFile, [1, 2, 3]);
        var badData = new DataObject(DataFormats.FileDrop, new[] { badFile });
        var badDrop = Drag(badData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, composer, DragDrop.PreviewDropEvent);
        composer.RaiseEvent(badDrop);
        await Until(() => ((TextBlock)ai.FindName("StatusText")).Text.Contains("不支持的文件"), "Unsupported drop was not reported");
        Check(badDrop.Handled && book.Entries.Count == 0 && composer.Text == draft,
            "Unsupported drop cleared the draft or added a broken attachment");
        Console.WriteLine("PASS: native composer file/folder drop, project-tree Link, Shift reference and text-drag distinction.");
    }
}
