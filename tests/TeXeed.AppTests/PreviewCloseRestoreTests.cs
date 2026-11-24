using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPreviewCloseRestoreAsync(MainWindow window, string output)
    {
        var folder = Path.Combine(output, "close-main-preview"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex"); var other = Path.Combine(folder, "other.tex");
        var chapter = Path.Combine(folder, "chapter.tex"); var shared = Path.Combine(folder, "shared.tex");
        File.WriteAllText(chapter, "First entry only."); File.WriteAllText(shared, "Shared.");
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\nMain \\input{chapter} \\input{shared}\n\\end{document}\n");
        File.WriteAllText(other, "\\documentclass{article}\n\\begin{document}\nOther \\input{shared}\n\\end{document}\n");
        await CallAsync(window, "OpenAsync", main);
        await CallAsync(window, "OpenProjectEntryAsync", other, false, false, true);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var model = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var otherKey = TabWorkspace.PreviewKeyFor(other);
        if (model.Owner(TabWorkspace.PreviewKey) is null || model.Owner(otherKey) is null)
            throw new Exception("The two preview tabs were not opened");

        async Task ShowByEye(MainWindow target, TabWorkspace tabs, string source, string entry, string key, bool open)
        {
            Eye(target, source, entry, open).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => tabs.FocusedTab == key && Equals(Field(target, "_activePreviewEntry"), entry) &&
                !(bool)Field(target, "_switchingEntryPreview")! && ((PdfPreview)target.FindName("Preview")).IsVisible,
                "Source eye did not show the intended entry");
            Eye(target, source, entry, true);
        }
        async Task CloseMainByTabButton(MainWindow target, TabWorkspace tabs)
        {
            var side = tabs.Owner(TabWorkspace.PreviewKey) ?? throw new Exception("Main preview is not open");
            var strip = (EditorTabStrip)target.FindName(side == TabSide.Left ? "FileTabs" : "RightFileTabs");
            var views = (Dictionary<string, Border>)Field(strip, "_views")!;
            var row = views[TabWorkspace.PreviewKey];
            var close = (Button)((Grid)row.Child).Children[1];
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => tabs.Owner(TabWorkspace.PreviewKey) is null, "Main preview close button did not close its tab");
            if (tabs.Owner(otherKey) is null) throw new Exception("Closing main preview removed the other preview");
        }
        async Task ReopenByLayoutMenu(MainWindow target, TabWorkspace tabs)
        {
            var menu = (Menu)target.FindName("MainMenu");
            var typeset = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "排版(_B)"));
            var show = typeset.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "显示项目主文件预览"));
            show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => tabs.Owner(TabWorkspace.PreviewKey) is not null && !(bool)Field(target, "_switchingEntryPreview")!, "The layout menu did not reopen main preview");
            if (tabs.Owner(otherKey) is null) throw new Exception("Reopening main preview removed the other preview");
        }

        await CloseMainByTabButton(window, model);
        await ReopenByLayoutMenu(window, model);
        Invoke(window, "ActivateFile", shared); Invoke(window, "ActivateFile", chapter);
        await ShowByEye(window, model, other, other, otherKey, true);
        Eye(window, shared, other, true);
        await ShowByEye(window, model, chapter, main, TabWorkspace.PreviewKey, true);
        Eye(window, shared, main, true);
        await CloseMainByTabButton(window, model);
        Invoke(window, "CloseTabs", otherKey, TabCloseScope.This);
        await WaitUntilAsync(() => model.Owner(otherKey) is null, "Last preview did not close");
        Eye(window, chapter, main, false); Eye(window, other, other, false);
        Capture(window, Path.Combine(output, "source-tab-eyes-closed.png"));
        await ShowByEye(window, model, other, other, otherKey, false);
        Invoke(window, "CreatePreviewDock", otherKey, Dock.Bottom);
        await ShowByEye(window, model, chapter, main, TabWorkspace.PreviewKey, false);
        var otherDock = model.Owner(otherKey);
        await ShowByEye(window, model, other, other, otherKey, true);
        if (otherDock is null || !model.ExtraGroups.Contains(otherDock.Value) || model.Owner(otherKey) != otherDock)
            throw new Exception("Selecting an already-open preview moved it out of its split");
        await CloseMainByTabButton(window, model);
        Capture(window, Path.Combine(output, "source-tab-eyes-mixed.png"));
        await CloseAndObserveAsync(window, "closed main preview");
        var reopened = new MainWindow(main) { Width = 1100, Height = 800, Left = -3000, Top = -3000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        reopened.Show();
        try
        {
            await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")!, "Project did not reopen");
            var restored = (TabWorkspace)Field(reopened, "_workspaceTabs")!;
            if (restored.Owner(TabWorkspace.PreviewKey) is not null || restored.Owner(otherKey) is null)
                throw new Exception("Restart did not preserve the closed main preview and the other open preview");
            Eye(reopened, main, main, false); Eye(reopened, other, other, true);
            await ShowByEye(reopened, restored, chapter, main, TabWorkspace.PreviewKey, false);
            await ReopenByLayoutMenu(reopened, restored);
        }
        finally { await CloseAndObserveAsync(reopened, "reopened main preview"); }
        Console.WriteLine("PASS: primary and dependency source tabs open, reveal and restore the correct preview, including split, shared-source and restart states");
    }
    private static Button Eye(MainWindow target, string source, string entry, bool open)
    {
        var tabs = (TabWorkspace)Field(target, "_workspaceTabs")!;
        var strip = (EditorTabStrip)Invoke(target, "GroupStrip", tabs.Owner(source)!.Value)!;
        var row = ((Dictionary<string, Border>)Field(strip, "_views")!)[source];
        var button = Descendants<Button>(row).Single(b => b.Content is System.Windows.Shapes.Path && Equals(b.Tag, entry));
        if (AutomationProperties.GetName(button) != (open ? "显示已打开的预览" : "打开预览") + " · " + Path.GetFileName(entry) ||
            !ReferenceEquals(((System.Windows.Shapes.Path)button.Content).Data, strip.Resources[open ? "OpenPreviewEye" : "ClosedPreviewEye"]))
            throw new Exception($"Source preview button did not reflect the open state for {source} -> {entry}");
        return button;
    }
}
