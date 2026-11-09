using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiSessionViewsAsync(Window owner, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var project = Path.Combine(output, "session-view-project", "main.tex");
        var one = "view-one-" + Guid.NewGuid().ToString("N");
        var two = "view-two-" + Guid.NewGuid().ToString("N");
        const string source = "\\documentclass{article}\n\\begin{document}\nHello.\n\\end{document}\n";
        var document = new AiSharedDocument(); var other = new AiSharedDocument();
        Check(document.TryInitialize(source, "model") && other.TryInitialize(source, "model"), "Could not initialize fixtures");
        var settings = new AppSettings { Engine = TexEngine.PdfLaTeX, AutoCropPdf = false };
        var editor = new AiSessionSourceView(document, AiDocumentRepository.GetDirectory(project, one), settings);
        var otherEditor = new AiSessionSourceView(other, AiDocumentRepository.GetDirectory(project, two), settings);
        using var preview = new AiSessionPdfView(project, one, document, settings);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "源码", Content = editor }); tabs.Items.Add(new TabItem { Header = "PDF", Content = preview });
        var dialog = new Window { Owner = owner, Width = 800, Height = 600, Content = tabs, ShowInTaskbar = false };
        try
        {
            dialog.Show(); await Task.Delay(100);
            var edits = 0; editor.DocumentEdited += (_, _) => edits++;
            var position = editor.Editor.Text.IndexOf("Hello.", StringComparison.Ordinal);
            editor.Editor.Select(position, 6);
            Check(editor.Selection.Text == "Hello." && editor.Selection.Start == position, "Source selection was not exposed");
            editor.Editor.Document.Replace(position, 6, "Hello world.");
            Check(document.Source.Contains("Hello world.") && edits == 1 && other.Source == source, "Source edit did not stay in its conversation");
            dialog.UpdateLayout(); editor.Editor.TextArea.TextView.EnsureVisualLines();
            System.Windows.Media.Typeface TypefaceAt(int offset)
            {
                dialog.UpdateLayout(); var view = editor.Editor.TextArea.TextView; view.EnsureVisualLines(); var line = view.GetVisualLine(editor.Editor.Document.GetLineByOffset(offset).LineNumber)!;
                return line.Elements.First(element => line.StartOffset + element.RelativeTextOffset <= offset && line.StartOffset + element.RelativeTextOffset + element.DocumentLength > offset).TextRunProperties.Typeface;
            }
            var aiTypeface = TypefaceAt(position); var userTypeface = TypefaceAt(position + 6);
            Check(aiTypeface.FontFamily != userTypeface.FontFamily || aiTypeface.Style != userTypeface.Style, "User and AI source text did not use distinct typefaces");
            var fallback = AiSourceAuthorTransformer.UserTypeface(aiTypeface, null);
            Check(fallback.FontFamily == aiTypeface.FontFamily && fallback.Style == FontStyles.Italic, "Missing bundled font did not use the italic fallback");
            Check(editor.Editor.GetGridColumn(position + 1) == editor.Editor.GetGridColumn(position) + 1, "Author typography changed fixed-grid caret placement");
            bool SameTypeface(System.Windows.Media.Typeface a, System.Windows.Media.Typeface b) => a.FontFamily == b.FontFamily && a.Style == b.Style;
            editor.Editor.Document.Remove(position + 6, 1); Check(SameTypeface(TypefaceAt(position + 6), userTypeface), "Deletion lost the user typeface");
            editor.Editor.Document.UndoStack.Undo(); Check(SameTypeface(TypefaceAt(position + 6), userTypeface), "Undo lost the user typeface");
            editor.Editor.Document.Insert(position + 5, "\n"); Check(SameTypeface(TypefaceAt(position + 7), userTypeface), "Line split lost the user typeface");
            editor.Editor.Document.Remove(position + 5, 1); Check(SameTypeface(TypefaceAt(position + 6), userTypeface), "Line merge lost the user typeface");
            Check(document.TryApply(document.Revision, document.Source.Replace("Hello", "Model", StringComparison.Ordinal), "model", "ai"), "Could not simulate model edit");
            Check(editor.RefreshFromDocument() && SameTypeface(TypefaceAt(position), aiTypeface) && SameTypeface(TypefaceAt(position + 6), userTypeface), "Model edit did not refresh author typography");
            editor.Editor.Document.Insert(position + 1, "\u0301"); _ = TypefaceAt(position); Check(document.Source.Contains("M\u0301odel"), "Mixed-author grapheme was damaged");
            editor.Editor.Document.Remove(position + 1, 1); _ = TypefaceAt(position);
            editor.Editor.Document.Replace(position, 5, "Hello");
            var comment = new AiDocumentComment { Start = position, Length = 12, Quote = "Hello world.", Text = "Review this." };
            document.Comments.Add(comment);
            await preview.CompileAsync();
            Check(preview.Displayed is not null && preview.Preview.PageCount > 0, "Session PDF did not load");
            tabs.SelectedIndex = 1; dialog.UpdateLayout();
            Descendants<Button>(preview).Single(b => Equals(b.Content, "只读评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.UpdateLayout();
            var pdfRow = Descendants<Button>(preview).Single(b => b.Content is TextBlock label && label.Text.Contains("Review this."));
            Check(((TextBlock)pdfRow.Content).TranslatePoint(new Point(), pdfRow).X < 1, "Session PDF comment text is not left aligned");
            await preview.LocateCommentAsync(comment);
            Check(preview.StatusText.Contains("近似位置"), "PDF comment did not navigate to an approximate position: " + preview.StatusText);
            var previous = preview.Displayed;
            var before = document.Revision;
            Check(document.TryApply(before, document.Source.Replace("Hello world.", "AI update."), "model", "ai"), "Could not make concurrent update");
            preview.MarkSourceChanged();
            await preview.LocateCommentAsync(comment);
            Check(preview.StatusText.Contains("过期"), "PDF comment did not report stale preview");
            editor.Editor.Document.Insert(editor.Editor.Document.TextLength, "local draft");
            Check(editor.HasConflict && editor.PendingText.Contains("local draft") && document.Source.Contains("AI update."), "Concurrent edit was overwritten");
            Check(!editor.RefreshFromDocument() && editor.PendingText.Contains("local draft"), "Refresh discarded conflicted input");
            Check(!editor.ResolveConflictAfterCopy(_ => false) && editor.HasConflict && editor.PendingText.Contains("local draft"), "Failed copy discarded conflicted input");
            var copied = "";
            Check(editor.ResolveConflictAfterCopy(text => { copied = text; return true; }) && copied.Contains("local draft") && !editor.HasConflict && editor.Editor.Text == document.Source, "Conflict resolution did not preserve the local text before reloading");
            Check(otherEditor.Editor.Text == source && !otherEditor.HasConflict, "Separate source view was affected");
            Check(document.TryApply(document.Revision, "\\documentclass{article}\n\\begin{document}\n\\brokencommand\n\\end{document}\n", "用户"), "Could not prepare failed build");
            await preview.CompileAsync();
            Check(ReferenceEquals(previous, preview.Displayed) && preview.Preview.PageCount > 0, "Failed session build removed the displayed PDF");
            using var reopened = new AiSessionPdfView(project, one, document, settings);
            tabs.Items.Add(new TabItem { Header = "恢复的 PDF", Content = reopened }); tabs.SelectedIndex = 2;
            await EventuallyAsync(() => Task.FromResult(reopened.Displayed is not null), "Last successful PDF did not restore into a new native view");
            Check(reopened.Preview.PageCount > 0 && reopened.StatusText.Contains("过期"), "Restored PDF did not report stale source");
            await reopened.LocateCommentAsync(comment);
            Check(reopened.StatusText.Contains("过期"), "Stale restored PDF allowed comment positioning");
            var brokenId = "broken-" + Guid.NewGuid().ToString("N"); var manifest = AiDocumentCompiler.ManifestPath(project, brokenId);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!); await File.WriteAllTextAsync(manifest, "{invalid}");
            using var brokenPreview = new AiSessionPdfView(project, brokenId, other, settings);
            tabs.Items.Add(new TabItem { Header = "损坏记录", Content = brokenPreview }); tabs.SelectedIndex = 3;
            await EventuallyAsync(() => Task.FromResult(brokenPreview.StatusText.Contains("无效")), "Corrupt preview record was not reported");
            Check(await File.ReadAllTextAsync(manifest) == "{invalid}", "Corrupt preview record was modified");
            var conversation = new AiConversation { Id = two, SharedDocument = other };
            var notebook = new AiNotebook { Conversations = [conversation], ActiveConversationId = two };
            using var panel = new AiPanel(); panel.Bind(new AiWorkspaceStore(project, notebook), two);
            var otherDirectory = AiDocumentRepository.GetDirectory(project, two); Directory.CreateDirectory(otherDirectory);
            await File.WriteAllBytesAsync(Path.Combine(otherDirectory, "fixture.bin"), RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            panel.NotifyExternalDocumentChanged();
            var saving = panel.SaveAsync();
            var journal = Path.Combine(otherDirectory, ".git", "texeed-save-journal.json");
            await EventuallyAsync(() => Task.FromResult(File.Exists(journal)), "Did not observe the in-flight version save");
            Check(other.TryApply(other.Revision, source + "\n% later edit", "用户"), "Could not prepare concurrent save edit");
            panel.NotifyExternalDocumentChanged();
            await saving; await panel.SaveAsync();
            var versions = await AiDocumentRepository.ListVersionsAsync(project, two);
            var saved = await AiDocumentRepository.ReadVersionAsync(project, two, versions[0].Commit);
            Check(saved.Source.EndsWith("% later edit", StringComparison.Ordinal), "Version queue dropped an edit made while saving");
            Console.WriteLine("Native session source, conflict preservation, selection, isolation, and PDF verified.");
        }
        finally { dialog.Close(); }
    }
}
