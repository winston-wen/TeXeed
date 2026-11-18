using System.IO;
using System.Windows;
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
        File.WriteAllText(main, "\\documentclass{article}\n\\begin{document}\nMain\n\\end{document}\n");
        File.WriteAllText(other, "\\documentclass{article}\n\\begin{document}\nOther\n\\end{document}\n");
        await CallAsync(window, "OpenAsync", main);
        await CallAsync(window, "OpenProjectEntryAsync", other, false, false, true);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var model = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var otherKey = TabWorkspace.PreviewKeyFor(other);
        if (model.Owner(TabWorkspace.PreviewKey) is null || model.Owner(otherKey) is null)
            throw new Exception("The two preview tabs were not opened");

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
        await CloseMainByTabButton(window, model);
        await CloseAndObserveAsync(window, "closed main preview");
        var reopened = new MainWindow(main) { Width = 1100, Height = 800, Left = -3000, Top = -3000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        reopened.Show();
        try
        {
            await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")!, "Project did not reopen");
            var restored = (TabWorkspace)Field(reopened, "_workspaceTabs")!;
            if (restored.Owner(TabWorkspace.PreviewKey) is not null || restored.Owner(otherKey) is null)
                throw new Exception("Restart did not preserve the closed main preview and the other open preview");
            await ReopenByLayoutMenu(reopened, restored);
        }
        finally { await CloseAndObserveAsync(reopened, "reopened main preview"); }
        Console.WriteLine("PASS: primary preview tab close, menu reopen with another preview open, and closed state after restart");
    }
}
