using System.IO;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiWorkspaceTabsAsync(MainWindow main, string output)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var directory = Path.Combine(output, "ai-workspace-tabs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "main.tex"); await File.WriteAllTextAsync(path, "\\documentclass{article}\n\\begin{document}project\\end{document}");
        await CallAsync(main, "OpenAsync", path);
        var store = (AiWorkspaceStore)Field(main, "_aiStore")!; var tabs = (TabWorkspace)Field(main, "_workspaceTabs")!;
        var a = store.Book.Active; a.Title = "A"; var b = store.Book.NewConversation(); b.Title = "B";
        const string text = "\\documentclass{article}\n\\usepackage{amsmath}\n\\begin{document}\n\\section{A question}\nHello world. This document is a short study note for a shared discussion.\n\nThe next paragraph sets out the assumptions behind the argument, and asks what happens when those assumptions change.\n\\begin{equation}\n1+2+\\cdots+n=\\frac{n(n+1)}{2}.\n\\end{equation}\n\nThe equation is valid for positive integers. The empty sum also covers the case n equals zero.\n\\end{document}";
        Check(a.SharedDocument.TryInitialize(text, "model") && b.SharedDocument.TryInitialize(text, "model"), "fixture initialization failed");
        await CallAsync(main, "OpenAiConversationAsync", a.Id);
        await CallAsync(main, "RenameExplorerFileAsync", path, "renamed.tex");
        Check(File.Exists(path) && !File.Exists(Path.Combine(directory, "renamed.tex")), "main rename disconnected AI session data");
        var panels = (Dictionary<string, AiPanel>)Field(main, "_aiPanels")!; var sources = (Dictionary<string, AiSessionSourceView>)Field(main, "_aiSources")!;
        var first = panels[a.Id]; var firstSource = sources[a.Id];
        ((TextBox)first.FindName("Composer")).Text = "A draft";
        firstSource.Editor.Document.Insert(firstSource.Editor.Text.IndexOf("Hello", StringComparison.Ordinal), "A ");
        var pos = firstSource.Editor.Text.IndexOf("Hello", StringComparison.Ordinal); firstSource.Editor.Select(pos, 5);
        Check(first.BeginSourceComment(firstSource.Revision, pos, 5), "source comment selection failed");
        await first.SaveAsync();
        await CallAsync(main, "OpenAiConversationAsync", b.Id);
        var second = panels[b.Id]; ((TextBox)second.FindName("Composer")).Text = "B draft";
        sources[b.Id].Editor.Document.Insert(sources[b.Id].Editor.Text.IndexOf("Hello", StringComparison.Ordinal), "B "); await second.SaveAsync();
        var bPosition = b.SharedDocument.Source.IndexOf("Hello", StringComparison.Ordinal);
        b.SharedDocument.Comments.Add(new AiDocumentComment { Start = bPosition, Length = 5, Quote = "Hello", Text = "Please review this point.", Author = "用户" });
        Invoke(second, "NotifyCommentsChanged");
        var pendingVersions = (System.Collections.IDictionary)Field(second, "_pendingDocumentVersions")!;
        var versionType = typeof(AiPanel).GetNestedType("PendingDocumentVersion", System.Reflection.BindingFlags.NonPublic)!;
        var badVersionKey = ("\0", b.Id); pendingVersions[badVersionKey] = Activator.CreateInstance(versionType, b.SharedDocument.Snapshot())!;
        var saveFailed = false; try { await CallAsync(main, "StopAiWorkspaceAsync"); } catch (Exception) { saveFailed = true; }
        pendingVersions.Remove(badVersionKey);
        Check(saveFailed && panels.ContainsKey(a.Id) && panels.ContainsKey(b.Id) && !(bool)Field(first, "_disposed")! && !(bool)Field(second, "_disposed")! && first.IsEnabled && second.IsEnabled, "later session save failure destroyed an earlier panel");
        var previews = (Dictionary<string, AiSessionPdfView>)Field(main, "_aiPreviews")!;
        previews[b.Id].SetSettings(new AppSettings { Engine = TexEngine.PdfLaTeX, EnginePath = @"C:\texlive\2026\bin\windows\pdflatex.exe", AutoCropPdf = false, CompressPdfWhitespace = false });
        await previews[b.Id].CompileAsync();
        Check(previews[b.Id].Displayed is not null, "fixture PDF did not compile");
        Check(Descendants<Button>(previews[b.Id]).Any(button => Equals(button.Content, "预览设置")), "session PDF lacks the existing preview settings entry");
        var projectPreview = (PdfPreview)main.FindName("Preview"); projectPreview.SetZoom(1.1); previews[a.Id].Preview.SetZoom(.8);
        previews[b.Id].Preview.SetZoom(1.5);
        var previewDialog = (PreviewSettingsWindow)Invoke(main, "CreateAiPreviewSettings", previews[b.Id].Preview)!;
        Check(ReferenceEquals(Field(previewDialog, "_preview"), previews[b.Id].Preview) && ((TextBox)previewDialog.FindName("ZoomInput")).Text.StartsWith("150", StringComparison.Ordinal), "session preview settings did not use its own current zoom and viewer");
        previewDialog.Show(); ((TextBox)previewDialog.FindName("ZoomInput")).Text = "180%"; await Task.Delay(50);
        Check(Math.Abs(previews[b.Id].Preview.Zoom - 1.8) < .001 && Math.Abs(previews[a.Id].Preview.Zoom - .8) < .001 && Math.Abs(projectPreview.Zoom - 1.1) < .001, "changing one preview zoom changed a different viewer");
        previewDialog.Close();
        Check(Math.Abs(previews[b.Id].Preview.Zoom - 1.5) < .001 && Math.Abs(previews[a.Id].Preview.Zoom - .8) < .001 && Math.Abs(projectPreview.Zoom - 1.1) < .001, "canceling preview settings changed another viewer's zoom");
        ((TextBlock)second.FindName("StatusText")).Text = "";
        previews[b.Id].Preview.FitWidth(); previews[b.Id].Preview.ShowLocation(new PdfLocation(1, 0, 0), highlight: false);
        main.UpdateLayout(); await Task.Delay(200);
        Capture(main, Path.Combine(output, "ai-workspace-tabs-wide.png"));
        var oldWidth = main.Width; main.Width = 900; main.UpdateLayout(); await Task.Delay(100); Capture(main, Path.Combine(output, "ai-workspace-tabs-narrow.png")); main.Width = oldWidth;
        await CallAsync(main, "OpenAiConversationAsync", a.Id); await CallAsync(main, "OpenAiConversationAsync", b.Id); main.UpdateLayout();
        Check(a.SharedDocument.Source.Contains("A Hello") && b.SharedDocument.Source.Contains("B Hello"), "source leaked across sessions");
        Check(a.CommentDrafts.Selection?.Quote == "Hello" && b.CommentDrafts.Selection is null, "comment draft leaked across sessions");
        Check(a.Draft == "A draft" && b.Draft == "B draft", "postscript draft leaked across sessions");
        Invoke(main, "CloseTabs", TabWorkspace.AiSourceKey(a.Id), TabCloseScope.This);
        Check(tabs.Owner(TabWorkspace.AiSourceKey(a.Id)) is null && ReferenceEquals(firstSource, sources[a.Id]), "closing one tab discarded its source view");
        await CallAsync(main, "OpenAiConversationAsync", a.Id);
        Check(ReferenceEquals(firstSource, sources[a.Id]) && a.Draft == "A draft" && a.CommentDrafts.Selection?.Quote == "Hello", "reopening lost session content");
        previews[a.Id].SetSettings(new AppSettings { Engine = TexEngine.PdfLaTeX, EnginePath = @"C:\texlive\2026\bin\windows\pdflatex.exe" });
        Invoke(main, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.AiConversationKey(a.Id))!.Value, TabWorkspace.AiConversationKey(a.Id));
        var projectPdfBefore = Field(main, "_pdfPath"); await CallAsync(main, "BuildFocusedDocumentAsync");
        Check(previews[a.Id].Displayed is not null && Equals(Field(main, "_pdfPath"), projectPdfBefore), "building from the comments tab compiled the project instead of the session");
        var searchStart = a.SharedDocument.Source.IndexOf("Hello", StringComparison.Ordinal);
        ((Action<int, int>)Field(first, "DocumentMatchRequested")!)(searchStart, 5);
        Check(tabs.FocusedTab == TabWorkspace.AiSourceKey(a.Id) && firstSource.Selection.Text == "Hello", "search navigated to the wrong session or selection");
        var shortcut = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, 0, System.Windows.Input.Key.G) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        await CallAsync(main, "HandleShortcutAsync", shortcut, System.Windows.Input.ModifierKeys.Control);
        var searchWindow = (Window?)Field(first, "_documentSearchWindow"); Check(shortcut.Handled && searchWindow?.IsVisible == true, "Ctrl+G in session source did not open the document search"); searchWindow!.Close();
        var old = a.SharedDocument.Revision; Check(a.SharedDocument.TryApply(old, a.SharedDocument.Source + " external", "model", "ai"), "external update failed");
        firstSource.Editor.Document.Insert(firstSource.Editor.Document.TextLength, " local"); Check(firstSource.HasConflict, "conflict was not detected");
        Invoke(main, "CloseTabs", TabWorkspace.AiPreviewKey(a.Id), TabCloseScope.This);
        Check(tabs.Owner(TabWorkspace.AiPreviewKey(a.Id)) is null && firstSource.PendingText.EndsWith(" local"), "closing PDF should preserve the conflicted source view");
        await CallAsync(main, "OpenAiConversationAsync", a.Id);
        Invoke(main, "CloseTabs", TabWorkspace.AiSourceKey(a.Id), TabCloseScope.This);
        Check(tabs.Owner(TabWorkspace.AiSourceKey(a.Id)) is not null && firstSource.PendingText.EndsWith(" local"), "closing conflict discarded source");
        Check(firstSource.ResolveConflictAfterCopy(_ => true), "conflict resolution failed");
        foreach (var key in new[] { TabWorkspace.AiSourceKey(a.Id), TabWorkspace.AiPreviewKey(a.Id), TabWorkspace.AiConversationKey(a.Id) })
        {
            if (key == TabWorkspace.AiConversationKey(a.Id))
            {
                var project = store.Project; store.Project = "\0";
                Invoke(main, "CloseTabs", key, TabCloseScope.This);
                for (var i = 0; i < 300 && (bool)Field(main, "_transitioning")!; i++) await Task.Delay(10);
                store.Project = project;
                Check(tabs.Owner(key) is not null && panels.ContainsKey(a.Id) && a.Draft == "A draft", "failed save closed or discarded the session");
            }
            Invoke(main, "CloseTabs", key, TabCloseScope.This);
            for (var i = 0; i < 300 && tabs.Owner(key) is not null; i++) await Task.Delay(10);
            Check(tabs.Owner(key) is null, "session tab failed to close: " + key);
        }
        await CallAsync(main, "OpenAiConversationAsync", a.Id);
        Check(((TextBox)panels[a.Id].FindName("Composer")).Text == "A draft" && a.CommentDrafts.Selection?.Quote == "Hello", "closing all tabs lost draft");
        var state = new ProjectReadingState { LeftTabs = [TabWorkspace.AiSourceKey(a.Id)], RightTabs = [TabWorkspace.PreviewKey, TabWorkspace.AiPreviewKey(a.Id), TabWorkspace.AiConversationKey(a.Id)], LeftActive = TabWorkspace.AiSourceKey(a.Id), RightActive = TabWorkspace.AiPreviewKey(a.Id), FocusedSide = TabSide.Left };
        await CallAsync(main, "RestoreReadingHistoryAsync", state);
        Check(tabs.Owner(TabWorkspace.AiSourceKey(a.Id)) == TabSide.Left && tabs.Owner(TabWorkspace.AiPreviewKey(a.Id)) == TabSide.Right && tabs.Active(TabSide.Right) == TabWorkspace.AiPreviewKey(a.Id), "reading history did not restore session tabs");
        Console.WriteLine("PASS MainWindow AI session tab isolation, close/reopen, conflict and layout restoration");
    }
}
