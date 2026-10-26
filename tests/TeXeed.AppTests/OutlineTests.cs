using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckOutlineAsync(MainWindow window, string output)
    {
        var settingsField = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousSettings = (AppSettings)settingsField.GetValue(window)!;
        var previousPath = ((TexProject)Field(window, "_project")!).MainPath;
        var column = (ColumnDefinition)window.FindName("SidebarColumn");
        var previousWidth = column.Width;
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var tree = (TreeView)outline.FindName("Tree");
        var sidebar = (FrameworkElement)window.FindName("SidebarPanel");
        var editor = (LatexEditor)window.FindName("Editor");
        var directory = Path.Combine(output, "outline-project"); Directory.CreateDirectory(directory);
        var main = Path.Combine(directory, "main.tex");
        File.WriteAllText(main, """
            \documentclass{article}
            \begin{document}
            \section{数列示例概览}
            \subsection{Base XY 的绘图参数}
            \subsection{Plot 的坐标参数}
            \subsection{Extended XY 的绘图参数}
            \subsection{示例表}
            \section{PlotCanvas 扩展 XY 绘图}
            \subsection{参数}
            \subsection{绘图}
            \subsubsection{Renderer 绘制辅助线 h，以及页面右侧注释区}
            \subsubsection{两组曲线用 Plot-Series 标记}
            \subsubsection{Renderer 更新图形，进行网格线检查}
            \subsubsection{Drawer 计算采样点坐标}
            \subsubsection{Drawer 校验坐标}
            \section{页面布局}
            \paragraph{跨级标题}
            \subparagraph{细节}
            \input{child}
            \end{document}
            """);
        File.WriteAllText(Path.Combine(directory, "child.tex"), "\\subsection{子文件中的独立标题}\n");
        void RequireOutline(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Settle()
        {
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        TreeViewItem Item(ItemsControl parent, OutlineNode node) => (TreeViewItem)parent.ItemContainerGenerator.ContainerFromItem(node)!;
        void Toggle(TreeViewItem item)
        {
            var button = (ToggleButton)item.Template.FindName("Expander", item);
            ((IToggleProvider)new ToggleButtonAutomationPeer(button)).Toggle();
        }
        try
        {
            settingsField.SetValue(window, previousSettings with { LivePreview = false });
            await CallAsync(window, "OpenAsync", main);
            if ((string)Field(window, "_sidebar")! != "outline" || !(bool)Field(window, "_workspaceExpanded")!)
                Descendants<Button>((FrameworkElement)window.FindName("WorkspaceRail")).Single(b => Equals(b.Tag, "outline")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            column.Width = new GridLength(290); await Settle();
            var roots = tree.Items.Cast<OutlineNode>().ToArray();
            RequireOutline(roots.Length == 4 && roots[0].Children.Count == 4 && roots[1].Children[1].Children.Count == 5,
                "Outline lost section hierarchy or attached a different file to the preceding section");
            RequireOutline(roots[2].Children.Count == 1 && roots[2].Children[0].Children.Count == 1, "Skipped heading levels did not form a compact hierarchy");
            var parent = Item(tree, roots[1]);
            var child = Item(parent, roots[1].Children[1]);
            var grandchild = Item(child, roots[1].Children[1].Children[0]);
            RequireOutline(Math.Abs(child.TranslatePoint(new Point(), tree).X - parent.TranslatePoint(new Point(), tree).X - 20) < 1
                && Math.Abs(grandchild.TranslatePoint(new Point(), tree).X - child.TranslatePoint(new Point(), tree).X - 20) < 1, "Outline levels do not align to consistent indentation");
            var guide = (Border)parent.Template.FindName("IndentGuide", parent);
            RequireOutline(guide.IsVisible && guide.ActualWidth == 1 && guide.ActualHeight >= 7 * 28, "Gray guide is absent or does not span the expanded subtree");
            RequireOutline(Descendants<TextBlock>(tree).All(block => !block.Text.StartsWith("main.tex:")), "Repeated source location still occupies a second row");
            foreach (var target in new[] { parent, child, grandchild })
            {
                var row = (Border)target.Template.FindName("Row", target);
                var heading = (Button)target.Template.FindName("HeadingButton", target);
                foreach (var point in new[] { new Point(2, 2), new Point(40, 2), new Point(40, row.ActualHeight - 2),
                    new Point(10, row.ActualHeight / 2), new Point(heading.ActualWidth - 2, row.ActualHeight / 2) })
                {
                    var hit = row.InputHitTest(point) as DependencyObject;
                    while (hit is not null && hit is not ButtonBase)
                        hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
                    RequireOutline(ReferenceEquals(hit, heading), $"Row position {point} is not owned by its heading button.");
                }
                var clicks = 0;
                EventHandler<SourceLocation> observe = (_, _) => clicks++;
                outline.NavigateRequested += observe;
                try
                {
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        editor.Focus();
                        DoubleClickHeading(window, heading);
                        await Settle();
                        RequireOutline(clicks == repeat + 1 && editor.CurrentLocation?.Line == ((OutlineNode)target.Header).Entry.Line,
                            "A heading button invocation was dropped or navigated more than once.");
                    }
                }
                finally { outline.NavigateRequested -= observe; }
            }
            Console.WriteLine("PASS: full heading rows own padding, badges and trailing space; repeated double clicks navigate exactly once after editor focus");
            CaptureOutline(sidebar, Path.Combine(output, "outline-expanded.png"));

            var caret = editor.CaretOffset;
            Toggle(parent); await Settle();
            RequireOutline(!roots[1].IsExpanded && !child.IsVisible && editor.CaretOffset == caret, "Collapsing a title navigated the source or left its children visible");
            CaptureOutline(sidebar, Path.Combine(output, "outline-collapsed.png"));
            var line = roots[1].Entry.Line;
            editor.Document.Insert(0, "\n\n");
            await CallAsync(window, "SaveAsync"); await Settle();
            RequireOutline(ReferenceEquals(roots[1], tree.Items[1]) && !roots[1].IsExpanded && roots[1].Entry.Line == line + 2,
                "Saving or moving heading lines reset collapsed state or left stale navigation");
            RequireOutline(ReferenceEquals(parent, Item(tree, roots[1])), "Ordinary text edits rebuilt the outline's item containers");
            var titleOffset = editor.Text.IndexOf("PlotCanvas 扩展 XY 绘图", StringComparison.Ordinal);
            editor.Document.Insert(titleOffset + "PlotCanvas 扩展 XY 绘图".Length, "（修订）");
            await CallAsync(window, "SaveAsync"); await Settle();
            RequireOutline(ReferenceEquals(roots[1], tree.Items[1]) && !roots[1].IsExpanded && roots[1].Title.EndsWith("（修订）"),
                "Renaming a heading in place reset its collapsed state");
            foreach (var mode in new[] { "search", "files", "outline" })
                Descendants<Button>((FrameworkElement)window.FindName("WorkspaceRail")).Single(b => Equals(b.Tag, mode)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            RequireOutline(!roots[1].IsExpanded && outline.Visibility == Visibility.Visible, "Switching sidebar modes lost collapsed state");
            Console.WriteLine("PASS: outline hierarchy, consistent gray guides, compact rows and collapsed state survive edits, saves and sidebar switching");

            var beforeKeys = editor.CaretOffset;
            parent.Focus(); parent.IsSelected = true;
            parent.RaiseEvent(CreateKey(window, Key.Right, Keyboard.KeyDownEvent)); await Settle();
            RequireOutline(parent.IsExpanded, "Right arrow did not expand a heading");
            parent.RaiseEvent(CreateKey(window, Key.Left, Keyboard.KeyDownEvent)); await Settle();
            RequireOutline(!parent.IsExpanded && editor.CaretOffset == beforeKeys, "Left arrow did not collapse without navigating");
            parent.RaiseEvent(CreateKey(window, Key.Enter, Keyboard.PreviewKeyDownEvent));
            RequireOutline(editor.CurrentLocation!.Line == roots[1].Entry.Line, "Enter did not navigate to the refreshed heading line");
            Toggle(parent); await Settle();
            child = Item(parent, roots[1].Children[1]);
            var clickedNode = roots[1].Children[1].Children[0];
            grandchild = Item(child, clickedNode);
            var title = Descendants<OutlineTitleViewport>(grandchild).First(block => block.Latex == clickedNode.Latex);
            DoubleClickHeading(window, (Button)grandchild.Template.FindName("HeadingButton", grandchild));
            await Settle();
            RequireOutline(editor.CurrentLocation!.Line == clickedNode.Entry.Line && title.ToolTip.ToString()!.Contains("main.tex:"),
                "Heading clicks or source-location tooltips do not target the correct source line");
            Console.WriteLine("PASS: outline expander, native arrow-key navigation, Enter and title clicks use independent fold/navigation actions");

            column.Width = new GridLength(242); await Settle();
            RequireOutline(Descendants<ScrollViewer>(tree).First().ScrollableWidth < 1 && Descendants<OutlineTitleViewport>(tree).Any(t => t.IsClipped), "Long outline titles must be clipped without pushing the expander offscreen");
            CaptureOutline(sidebar, Path.Combine(output, "outline-narrow.png"));
            Capture(window, Path.Combine(output, "outline-narrow-window.png"));
            await CheckOutlineInteractionsAsync(window, output);
            column.Width = new GridLength(390); outline.LayoutTransform = new ScaleTransform(1.5, 1.5); await Settle();
            CaptureOutline(sidebar, Path.Combine(output, "outline-scaled.png"));
            RequireOutline(Descendants<ToggleButton>(tree).Where(b => b.IsVisible).All(b => b.ActualWidth >= 20 && b.ActualHeight >= 28), "Scaled outline clipped its expander hit areas");
            Console.WriteLine("PASS: outline renders at narrow widths and 150% scale with bounded title viewports");

            outline.LayoutTransform = Transform.Identity;
            editor.Document.Text = "\\section{Parent}\n\\subsection{A}\n" + string.Concat(Enumerable.Repeat("body\n", 120))
                + "\\subsection{B}\n" + string.Concat(Enumerable.Repeat("body\n", 5))
                + "\\subsection{C}\n" + string.Concat(Enumerable.Repeat("body\n", 120)) + "\\section{Other}\n\\input{child}\n";
            await CallAsync(window, "SaveAsync");
            var trackedParent = tree.Items.Cast<OutlineNode>().First();
            var trackedChildren = trackedParent.Children.ToArray();
            editor.NavigateTo(new SourceLocation(main, 60), center: true); await Settle();
            RequireOutline(trackedChildren[0].IsViewportHighlighted && !trackedParent.IsViewportHighlighted
                && !trackedChildren[1].IsViewportHighlighted, "Body viewport did not highlight only its deepest visible section");
            editor.NavigateTo(new SourceLocation(main, 126), center: true); await Settle();
            RequireOutline(trackedChildren[1].IsViewportHighlighted && trackedChildren[2].IsViewportHighlighted
                && !trackedParent.IsViewportHighlighted, "Multiple viewport headings did not highlight together without their ancestor");
            CaptureOutline(sidebar, Path.Combine(output, "outline-viewport-highlights.png"));
            trackedParent.IsExpanded = false; await Settle();
            RequireOutline(trackedParent.IsViewportHighlighted && trackedChildren.All(node => !node.IsViewportHighlighted)
                && !trackedParent.IsExpanded, "Collapsed branch did not retain folding and transfer highlighting to the parent");
            editor.NavigateTo(new SourceLocation(main, 60), center: true); await Settle();
            RequireOutline(!trackedParent.IsExpanded && trackedParent.IsViewportHighlighted, "Editor scrolling automatically expanded the outline");
            Invoke(window, "ActivateFile", Path.Combine(directory, "child.tex")); await Settle();
            RequireOutline(!trackedParent.IsViewportHighlighted && tree.Items.Cast<OutlineNode>().Last().IsViewportHighlighted,
                $"Switching source files left stale outline highlighting: range={editor.VisibleSourceRange}; nodes={string.Join(", ", tree.Items.Cast<OutlineNode>().Select(n => $"{n.Title}:{n.IsViewportHighlighted}"))}");
            Console.WriteLine("PASS: outline follows visible source ranges, highlights multiple deepest headings, and preserves collapsed branches");
        }
        finally
        {
            outline.LayoutTransform = Transform.Identity; column.Width = previousWidth;
            await CallAsync(window, "OpenAsync", previousPath);
            settingsField.SetValue(window, previousSettings);
        }
    }

    private static void CaptureOutline(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2), (int)Math.Ceiling(element.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
