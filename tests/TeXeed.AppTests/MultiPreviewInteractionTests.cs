using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckMultiPreviewInteractionsAsync(MainWindow window, string a, string b)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var keyA = TabWorkspace.PreviewKey; var keyB = TabWorkspace.PreviewKeyFor(b);
        Invoke(window, "CreatePreviewDock", keyA, Dock.Top);
        await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, a, StringComparison.OrdinalIgnoreCase)
            && !(bool)Field(window, "_switchingEntryPreview")!, "Could not activate first preview in the new area");
        await CallAsync(window, "BuildAsync");
        var pdfPath = (string)Field(window, "_pdfPath")!;
        var aView = (PdfPreview)window.FindName("Preview");
        var bView = (PdfPreview)Field(Invoke(window, "EntryState", b)!, "View")!;
        await WaitUntilAsync(() => aView.IsVisible && bView.IsVisible && bView.PageCount == 2, "Both PDF views must be visible for the compile-lock test");
        var compiler = (CompileService)Field(window, "_compiler")!;
        var gate = (SemaphoreSlim)typeof(CompileService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(compiler)!;
        await gate.WaitAsync();
        Task? blockedCompile = null;
        try
        {
            blockedCompile = CallAsync(window, "BuildAsync");
            await WaitUntilAsync(() => (bool)Field(window, "_building")! && Field(window, "_compileTask") is not null,
                "The first compilation did not reach its controlled wait");
            bView.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(string.Equals(Field(window, "_activePreviewEntry") as string, a, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Field(window, "_queuedBuildPreview") as string, b, StringComparison.OrdinalIgnoreCase),
                "Clicking the other PDF during compilation did not queue its activation safely");
        }
        finally { gate.Release(); }
        await blockedCompile!;
        await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, b, StringComparison.OrdinalIgnoreCase)
            && !(bool)Field(window, "_switchingEntryPreview")!, "Queued preview was not activated after compilation");
        await CallAsync(window, "BuildAsync");
        Check(string.Equals(Field(window, "_lastCompileEntryPath") as string, b, StringComparison.OrdinalIgnoreCase),
            "The first compilation after selecting the second PDF used the wrong entry");
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(keyA)!.Value, keyA);
        await WaitUntilAsync(() => ReferenceEquals(window.FindName("Preview"), aView) && !(bool)Field(window, "_switchingEntryPreview")!,
            "Could not reactivate first preview after the compile-lock test");
        var aDocument = Field(aView, "_document")!;
        var originalFingerprint = (string)aDocument.GetType().GetProperty("Fingerprint")!.GetValue(aDocument)!;
        var settings = (AppSettings)Field(window, "_settings")!;
        var project = (TexProject)Field(window, "_previewProject")!;
        await CallAsync(window, "ReloadExternalAsync");
        await WaitUntilAsync(() => !(bool)Field(window, "_externalChangePending")! && !(bool)Field(window, "_refreshing")!, "Pending file events blocked inverse navigation");
        var point = await ((SyncTexService)Field(window, "_sync")!).ForwardAsync(project, pdfPath, new SourceLocation(a, 4), settings);
        Check(point is not null, "First PDF has no source mapping");
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(keyB)!.Value, keyB);
        await WaitUntilAsync(() => string.Equals(Field(window, "_activePreviewEntry") as string, b, StringComparison.OrdinalIgnoreCase)
            && !(bool)Field(window, "_switchingEntryPreview")!, "Could not activate second preview");
        Check(ReferenceEquals(window.FindName("Preview"), bView), "Second preview instance changed unexpectedly");
        Check(aView.IsVisible && bView.IsVisible && !ReferenceEquals(aView, bView), "Independent previews are not simultaneously visible");
        aView.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        var inverse = (EventHandler<PdfLocation>)typeof(PdfPreview).GetField("InverseRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(aView)!;
        inverse(aView, point!);
        await WaitUntilAsync(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("已定位到 main.tex"),
            "First inverse request on inactive preview did not reach its source");
        Check(((LatexEditor)window.FindName("Editor")).CurrentFilePath == a && ReferenceEquals(window.FindName("Preview"), aView),
            "Inverse navigation targeted the other preview");

        Invoke(window, "SelectWorkspaceTab", tabs.Owner(keyB)!.Value, keyB);
        await WaitUntilAsync(() => ReferenceEquals(window.FindName("Preview"), bView) && !(bool)Field(window, "_switchingEntryPreview")!, "Could not return to second preview");
        aView.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { aView, true }, null)!;
        opening.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent;
        aView.RaiseEvent(opening);
        if (!opening.Handled) aView.ContextMenu.IsOpen = true;
        await WaitUntilAsync(() => ReferenceEquals(window.FindName("Preview"), aView) && !(bool)Field(window, "_switchingEntryPreview")! && aView.ContextMenu.IsOpen,
            "First context menu on inactive preview did not open after activation");
        var captured = default(AiEntry);
        aView.AiSelectionConfirmed += (_, entry) => captured = entry;
        aView.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "AI 圈点")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        aView.ContextMenu.IsOpen = false;
        Check(aView.IsAiSelecting && !bView.IsAiSelecting, "AI marking started on the wrong PDF");
        var firstLayout = ((System.Collections.IEnumerable)Field(aView, "_layouts")!).Cast<object>().First();
        var top = (double)firstLayout.GetType().GetProperty("Top")!.GetValue(firstLayout)!;
        var left = (double)firstLayout.GetType().GetProperty("Left")!.GetValue(firstLayout)!;
        typeof(PdfPreview).GetField("_aiSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(aView, new Rect(left + 30, top + 50, 100, 50));
        await aView.ConfirmAiSelectionAsync();
        Check(captured is not null && captured.PdfRevision == originalFingerprint && aView.AnnotationPosition(captured) is not null && bView.AnnotationPosition(captured) is null,
            "AI annotation did not retain the selected PDF identity");
        Invoke(window, "SetAiMarking", false);

        var editor = (LatexEditor)window.FindName("Editor");
        Check(editor.CurrentFilePath == a, "Source editor changed before same-path rebuild");
        editor.Document.Insert(editor.Text.IndexOf("\\end{document}", StringComparison.Ordinal), "More text for the revised PDF.\\par\n");
        await CallAsync(window, "BuildAsync");
        Check(string.Equals((string?)Field(window, "_pdfPath"), pdfPath, StringComparison.OrdinalIgnoreCase), "Rebuild unexpectedly changed the output path");
        var revisedDocument = Field(aView, "_document")!;
        var revisedFingerprint = (string)revisedDocument.GetType().GetProperty("Fingerprint")!.GetValue(revisedDocument)!;
        Check(revisedFingerprint != originalFingerprint && bView.PageCount == 2, "Same-path rebuild failed to refresh its own PDF or altered the other preview");
        Invoke(window, "MoveWorkspaceTab", keyA, TabSide.Right, int.MaxValue, false);
        await WaitUntilAsync(() => tabs.Owner(keyA) == TabSide.Right, "Could not return first preview to its original area");
        Invoke(window, "SelectWorkspaceTab", TabSide.Right, keyB);
        await WaitUntilAsync(() => ReferenceEquals(window.FindName("Preview"), bView) && !(bool)Field(window, "_switchingEntryPreview")!, "Could not restore second preview after interaction tests");
        Console.WriteLine("PASS: inactive PDF inverse request, context menu and AI marking identity; same-path rebuild updates only its own view");
    }
}
