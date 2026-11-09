using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task MeasureAiNativeSessionPerfAsync(Window owner, string output)
    {
        var paragraphs = Enumerable.Range(1, 120).Select(i => $"第 {i} 段：这个结论需要检查已有假设及推导过程，记录仍未解决的问题。").ToArray();
        var source = "\\documentclass{article}\n\\begin{document}\n" + string.Join("\n\n", paragraphs) + "\n\\end{document}";
        var results = new List<object>();
        var settings = new AppSettings { Engine = TexEngine.XeLaTeX, AutoCropPdf = false };
        foreach (var count in new[] { 0, 60 })
        {
            AiConversation MakeConversation()
            {
                var doc = new AiSharedDocument(); doc.TryInitialize(source, "model");
                for (var i = 0; i < count; i++)
                {
                    var quote = $"第 {i + 1} 段";
                    doc.Comments.Add(new AiDocumentComment { Start = source.IndexOf(quote, StringComparison.Ordinal), Length = quote.Length, Quote = quote, Text = "请核对论证的前提和范围。", Replies = [new AiDocumentReply { Text = "进一步检查支持材料。" }] });
                }
                return new AiConversation { SharedDocument = doc };
            }
            var first = MakeConversation(); var second = MakeConversation();
            var path = Path.Combine(output, "perf-project", "main.tex");
            var editor = new AiSessionSourceView(first.SharedDocument, AiDocumentRepository.GetDirectory(path, first.Id), settings);
            var notebook = new AiNotebook { Conversations = [first, second], ActiveConversationId = first.Id };
            var store = new AiWorkspaceStore(path, notebook);
            using var panel = new AiPanel(); panel.Bind(store, first.Id);
            using var secondPanel = new AiPanel(); secondPanel.Bind(store, second.Id);
            var persist = false;
            editor.DocumentEdited += (_, _) => { if (persist) panel.NotifyExternalDocumentChanged(); };
            var sidebar = new AiCommentSidebar(); sidebar.Bind(first, null, () => { }, () => { });
            using var pdf = new AiSessionPdfView(path, first.Id, first.SharedDocument, settings);
            var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "源码", Content = editor }); tabs.Items.Add(new TabItem { Header = "PDF", Content = pdf });
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(sidebar); Grid.SetColumn(tabs, 1); grid.Children.Add(tabs);
            var host = new Window { Owner = owner, Width = 1000, Height = 800, ShowInTaskbar = false, Content = grid };
            host.Show();
            try
            {
                async Task<(double Sync, double Total, long Bytes)> Run(Action action)
                {
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                    action(); var sync = watch.Elapsed.TotalMilliseconds;
                    await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    return (sync, watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
                }
                async Task Measure(string name, Action action)
                {
                    var samples = new List<object>();
                    for (var i = 0; i < 9; i++)
                    {
                        var baseline = await Run(() => { }); var value = await Run(action);
                        if (i >= 2) samples.Add(new { value.Sync, value.Total, value.Bytes, idleMs = baseline.Total, idleBytes = baseline.Bytes,
                            adjustedMs = Math.Max(0, value.Total - baseline.Total), adjustedBytes = Math.Max(0, value.Bytes - baseline.Bytes) });
                    }
                    var list = (ListBox)typeof(AiCommentSidebar).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sidebar)!;
                    var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                    var sessionSidebar = (AiCommentSidebar)typeof(AiPanel).GetField("_commentSidebar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
                    var sessionList = (ListBox)typeof(AiCommentSidebar).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sessionSidebar)!;
                    var sessionRealized = Enumerable.Range(0, sessionList.Items.Count).Count(i => sessionList.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                    results.Add(new { count, action = name, samples, visibleControls = VisibleControls(host), clippedVisibleControls = VisibleControls(host, true), realizedCommentRows = realized, realizedSessionRows = sessionRealized });
                }
                await Measure("sidebar-refresh", () => sidebar.Refresh());
                var toggle = false;
                await Measure("source-input", () => { if (toggle) editor.Editor.Document.Remove(0, 1); else editor.Editor.Document.Insert(0, " "); toggle = !toggle; });
                persist = true;
                await Measure("source-input-with-save-queue", () => { if (toggle) editor.Editor.Document.Remove(0, 1); else editor.Editor.Document.Insert(0, " "); toggle = !toggle; });
                await Measure("source-selection", () => { editor.Editor.Select(0, 6); editor.Editor.Select(0, 0); });
                var selected = false;
                await Measure("sidebar-switch", () => { selected = !selected; sidebar.Bind(selected ? second : first, null, () => { }, () => { }); });
                await pdf.CompileAsync();
                if (pdf.Displayed is null) throw new InvalidOperationException("Performance PDF fixture failed to compile");
                tabs.SelectedIndex = 1; await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var zoom = false;
                await Measure("pdf-zoom-layout", () => { zoom = !zoom; pdf.Preview.SetZoom(zoom ? 1.1 : 1.0); });
                tabs.SelectedIndex = 0; sidebar.Visibility = Visibility.Collapsed;
                grid.Children.Add(panel); grid.Children.Add(secondPanel); panel.Visibility = Visibility.Visible; secondPanel.Visibility = Visibility.Collapsed;
                panel.SharedDocumentChanged += (_, _) => { editor.RefreshFromDocument(); pdf.MarkSourceChanged(); };
                await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var otherPanel = false;
                await Measure("session-panel-switch", () => { otherPanel = !otherPanel; panel.Visibility = otherPanel ? Visibility.Collapsed : Visibility.Visible; secondPanel.Visibility = otherPanel ? Visibility.Visible : Visibility.Collapsed; });
                panel.Visibility = Visibility.Visible; secondPanel.Visibility = Visibility.Collapsed;
                await Measure("source-input-workspace", () => { if (toggle) editor.Editor.Document.Remove(0, 1); else editor.Editor.Document.Insert(0, " "); toggle = !toggle; });
            }
            finally { host.Close(); }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "native-session-performance.json"), JsonSerializer.Serialize(new {
            fixture = "WPF 1000x800, 120 paragraphs, 0/60 comments each with one reply; PDF via xelatex",
            method = "two warmups and seven measurements; each action paired with an empty ApplicationIdle queue; UI-thread allocation only",
            limitations = "PDF zoom measures UI work through ApplicationIdle, not completion of asynchronous rasterization. Native fixture differs from historical WebView fixture.", results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Native session performance samples saved to " + Path.Combine(output, "native-session-performance.json"));
    }

    private static int VisibleControls(FrameworkElement root, bool clipAncestors = false)
    {
        var viewport = new Rect(0, 0, root.ActualWidth, root.ActualHeight); var count = 0;
        void Visit(DependencyObject node)
        {
            if (node is Control control && control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0)
            {
                try
                {
                    var bounds = Rect.Intersect(control.TransformToAncestor(root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight)), viewport);
                    if (clipAncestors)
                        for (var ancestor = VisualTreeHelper.GetParent(control); ancestor is not null && !ReferenceEquals(ancestor, root) && !bounds.IsEmpty; ancestor = VisualTreeHelper.GetParent(ancestor))
                            if (ancestor is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter))
                                bounds = Rect.Intersect(bounds, element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)));
                    if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) count++;
                }
                catch (InvalidOperationException) { }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(root); return count;
    }
}
