using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static async Task CheckProjectCommentsUiAsync(MainWindow window, string output)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var root = Path.Combine(output, "project-comments-ui"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "main.tex"); var otherPath = Path.Combine(root, "other.tex");
        var source = "\\documentclass{article}\n\\begin{document}\nHello world.\n\\end{document}\n";
        await File.WriteAllTextAsync(path, source); await File.WriteAllTextAsync(otherPath, source);
        Console.WriteLine("Project comments UI: opening fixture.");
        await CallAsync(window, "SwitchProjectAsync", path, false);
        Console.WriteLine("Project comments UI: fixture opened.");
        var editor = (LatexEditor)window.FindName("LeftEditor");
        Check(editor.ContextMenu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "查看项目评论")), "No project comment reopening entry.");
        var position = editor.Text.IndexOf("Hello world.", StringComparison.Ordinal); editor.Select(position, 12);
        Invoke(window, "OpenProjectComment", editor); window.UpdateLayout();
        var sidebar = (AiCommentSidebar)Field(window, "_projectCommentSidebar")!;
        Descendants<TextBox>(sidebar).Single().Text = "项目文字的评论";
        Descendants<Button>(sidebar).Single(b => Equals(b.Content, "添加评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var store = (ProjectCommentStore)Field(window, "_projectComments")!;
        var record = store.Files.Single().Data; var comment = record.SharedDocument.Comments.Single();
        Check(comment.Quote == "Hello world." && comment.AnchorRevision == record.SharedDocument.Revision, "Project source selection not anchored.");
        var panel = (AiPanel)Field(window, "_aiPanel")!;
        Check(panel.Conversation.SharedDocument.Comments.Count == 0, "Project comment entered a session document.");
        Invoke(window, "HideProjectCommentsSidebar"); Invoke(window, "ShowAiSidebar", new object?[] { null }!);
        editor.Select(0, 0); Invoke(window, "ShowProjectComments", editor); window.UpdateLayout();
        Check(Descendants<Button>(sidebar).Any(b => Equals(b.Tag, comment.Id)), "Existing project comments cannot reopen without selection.");
        var commentButton = Descendants<Button>(sidebar).Single(b => Equals(b.Tag, comment.Id));
        Check(((TextBlock)commentButton.Content).TranslatePoint(new Point(), commentButton).X < 1, "Project comment text is not left aligned.");
        commentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Descendants<TextBox>(sidebar).Last().Text = "待发回复草稿";
        await CallAsync(window, "FlushProjectCommentsAsync");
        var persisted = ProjectCommentStore.Load(path, root).Files.Single().Data;
        Check(persisted.SharedDocument.Comments.Single().Text == "项目文字的评论" && persisted.CommentDrafts.Replies[comment.Id] == "待发回复草稿", "Project comment or reply draft not persisted.");
        var settings = (AppSettings)Field(window, "_settings")!;
        typeof(MainWindow).GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, settings with { Engine = TexEngine.PdfLaTeX, LivePreview = false, AutoCropPdf = false });
        Console.WriteLine("Project comments UI: compiling fixture.");
        await CallAsync(window, "BuildAsync", true, CancellationToken.None, false, null!);
        Console.WriteLine("Project comments UI: compile returned.");
        Check(Field(window, "_pdfPath") is string, "Project fixture failed to compile a real PDF.");
        Descendants<Button>(window).Single(b => Equals(b.Content, "项目只读评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        var pdfRows = (AiReadOnlyCommentList)Field(window, "_projectPdfComments")!;
        var pdfComment = Descendants<Button>(pdfRows).Single();
        Check(((TextBlock)pdfComment.Content).TranslatePoint(new Point(), pdfComment).X < 1, "Project PDF comment text is not left aligned.");
        await CallAsync(window, "LocateProjectCommentAsync", path, record, comment);
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("近似位置"), "Project PDF did not locate the source comment approximately.");
        await WaitForPreviewRenderedAsync(window);
        Capture(window, Path.Combine(output, "project-comments-native.png"));
        editor.Document.Insert(0, "% changed\n");
        Check(((TextBlock)window.FindName("PdfStatus")).Text.Contains("过期"), "Project PDF did not signal stale source.");
        await CallAsync(window, "LocateProjectCommentAsync", path, record, comment);
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("过期"), "Stale project PDF accepted comment navigation.");
        await CallAsync(window, "BuildAsync", true, CancellationToken.None, false, null!);
        var storePath = (string)Field(store, "_path")!;
        Descendants<TextBox>(sidebar).Last().Text = "失败时仍保留草稿";
        using (var locked = new FileStream(storePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var closeError = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            closeError.Tick += (_, _) =>
            {
                var dialog = IntPtr.Zero;
                while ((dialog = FindWindowEx(IntPtr.Zero, dialog, "#32770", "TeXeed")) != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(dialog, out var processId);
                    if (processId == (uint)Environment.ProcessId) { PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); break; }
                }
            };
            closeError.Start();
            Console.WriteLine("Project comments UI: checking locked save and switch.");
            try { await CallAsync(window, "SwitchProjectAsync", otherPath, false); } finally { closeError.Stop(); }
            Check(((TexProject)Field(window, "_project")!).MainPath == path && record.CommentDrafts.Replies[comment.Id] == "失败时仍保留草稿", "Failed comment save switched project or lost draft.");
        }
        await CallAsync(window, "FlushProjectCommentsAsync");
        Console.WriteLine("Project comments UI: source selection, reopening, drafts, real PDF SyncTeX, stale preview, isolation and failed-save protection passed.");
    }
}
