using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAiRelationsSurfaceAsync(MainWindow window, string output)
    {
        static void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
        }

        var book = new AiNotebook();
        var conversation = book.Active;
        conversation.Title = "原会话";
        book.Conversations.Add(new AiConversation { Title = "追问", ParentConversationId = conversation.Id });
        var chat = TabWorkspace.AiConversationKey(conversation.Id);
        var relations = TabWorkspace.AiRelationsKey(conversation.Id);
        string ConversationOnlyProject(string name)
        {
            var folder = Path.Combine(output, name);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "main.tex");
            File.WriteAllText(path, "\\documentclass{article}\n\\begin{document}Cached PDF\\end{document}\n");
            WritePrintPdf(Path.ChangeExtension(path, ".pdf"));
            AiStorage.Save(path, book);
            ProjectReadingStore.Save(path, new ProjectReadingState { LeftTabs = [chat], LeftActive = chat });
            return path;
        }

        var main = ConversationOnlyProject("relations-project");
        await CallAsync(window, "OpenAsync", main);
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var preview = (PdfPreview)window.FindName("InitialPreview");
        Check(preview.PageCount == 3 && tabs.Active(TabSide.Left) == chat && !tabs.Has(TabSide.Right),
            "The cold-start fixture must have a loaded PDF and a conversation as its only tab");

        void OpenRelations(MainWindow host)
        {
            var panel = Descendants<AiPanel>(host).Single(p => p.IsVisible);
            var button = Descendants<Button>(panel).Single(b => b.IsVisible && Equals(b.Content, "会话关系"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
        }

        void CheckRelations(MainWindow host, string screenshot)
        {
            var body = (Grid)host.FindName("RightGroupBody");
            var graph = Descendants<AiConversationGraph>(body).Single(g => g.IsVisible);
            Capture(host, Path.Combine(output, screenshot));
            var surfaces = body.Children.OfType<FrameworkElement>().Where(v => v.IsVisible).ToArray();
            Check(surfaces.Length == 1 && surfaces[0].IsAncestorOf(graph),
                "The active relations tab shares its pane with another visible surface: " +
                string.Join(", ", surfaces.Select(s => s.Name.Length > 0 ? s.Name : s.GetType().Name)));
            var node = graph.Layout.Nodes.First().Bounds;
            var hit = host.InputHitTest(graph.TranslatePoint(new Point(node.Left + node.Width / 2, node.Top + node.Height / 2), host));
            Check(hit == graph, "The visible relationship node cannot receive a click");
        }

        static Task WaitForPdfPage(PdfPreview view)
        {
            var scroll = (ScrollViewer)Field(view, "_scroll")!;
            return WaitUntilAsync(() =>
            {
                if (!view.IsVisible || (bool)Field(view, "_rendering")! || ((DispatcherTimer)Field(view, "_renderTimer")!).IsEnabled) return false;
                var viewport = new Rect(new Point(), scroll.RenderSize);
                return Descendants<Image>(view).Any(i => i.IsVisible && i.Source is BitmapSource &&
                    i.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), i.RenderSize)).IntersectsWith(viewport));
            }, "The selected PDF did not render a page inside its viewport");
        }

        async Task CheckPdf(MainWindow host, bool docked)
        {
            var model = (TabWorkspace)Field(host, "_workspaceTabs")!;
            var view = (PdfPreview)host.FindName("InitialPreview");
            var container = (Grid)host.FindName("PreviewContent");
            var status = (Border)host.FindName("PreviewStatusBar");
            await WaitForPdfPage(view);
            var pane = (Grid)container.Parent;
            Check((pane == host.FindName("RightGroupBody")) == !docked &&
                pane.Children.OfType<FrameworkElement>().Where(v => v.IsVisible).SequenceEqual([container]) &&
                model.Owner(TabWorkspace.PreviewKey) is { } side && model.Active(side) == TabWorkspace.PreviewKey &&
                status.IsVisible && view.PageCount == 3, "The PDF and its status bar did not occupy their selected pane");
            var hit = host.InputHitTest(view.TranslatePoint(new Point(view.ActualWidth / 2, view.ActualHeight / 2), host));
            Check(hit is Visual target && (target == view || view.IsAncestorOf(target)), "The visible PDF cannot receive a click");
        }

        void ShowMainPreview(MainWindow host)
        {
            var menu = (Menu)host.FindName("MainMenu");
            var typeset = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "排版(_B)"));
            typeset.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "显示项目主文件预览"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            host.UpdateLayout();
        }

        OpenRelations(window);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (preview.IsVisible) await WaitForPdfPage(preview);
        CheckRelations(window, "relations-cold-start.png");
        Check(!preview.IsVisible, "The closed primary preview is still visible");
        ShowMainPreview(window);
        await CheckPdf(window, docked: false);
        OpenRelations(window);
        CheckRelations(window, "relations-after-switch.png");
        Invoke(window, "CreatePreviewDock", TabWorkspace.PreviewKey, Dock.Bottom);
        window.UpdateLayout();
        await CheckPdf(window, docked: true);
        CheckRelations(window, "relations-separate-preview.png");
        await CloseAndObserveAsync(window, "save separate PDF and relationship panes");

        var reopened = new MainWindow(main) { Width = 1440, Height = 900, Left = -3000, Top = -3000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        reopened.Show();
        try
        {
            await WaitUntilAsync(() => Field(reopened, "_project") is TexProject && !(bool)Field(reopened, "_transitioning")!,
                "The project did not reopen");
            await CheckPdf(reopened, docked: true);
            CheckRelations(reopened, "relations-restored.png");
            var restored = (TabWorkspace)Field(reopened, "_workspaceTabs")!;
            Invoke(reopened, "CloseTabs", TabWorkspace.PreviewKey, TabCloseScope.This);
            await WaitUntilAsync(() => restored.Owner(TabWorkspace.PreviewKey) is null, "Closing the docked PDF failed");
            Check(restored.Active(TabSide.Right) == relations, "Closing the PDF changed the adjacent relations tab");
            ShowMainPreview(reopened);
            await CheckPdf(reopened, docked: false);
            await CallAsync(reopened, "OpenAsync", ConversationOnlyProject("second-project"));
            OpenRelations(reopened);
            CheckRelations(reopened, "relations-switched-project.png");
        }
        finally { await CloseAndObserveAsync(reopened, "close reopened relationship workspace"); }
        Console.WriteLine("PASS: cold-start relationship page excludes cached PDF and status bar; switching, detached PDF, input and restart");
    }
}
