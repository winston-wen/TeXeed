using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckOutlineDecorationsAsync(MainWindow window, string output)
    {
        var previousPath = ((TexProject)Field(window, "_project")!).MainPath;
        var folder = Path.Combine(output, "outline-decorations"); Directory.CreateDirectory(folder);
        var main = Path.Combine(folder, "main.tex");
        File.WriteAllText(main, """
            \section{总结}
            \subsection{核对安全承诺}
            % TODO: 检查承诺
            \subsection{盘点通信和计算成本 $\sum_{k=0}^{114}\frac{(-1)^{2k+1}}{(2k+1)!}$}
            公式之后的正文
            \todo{核对求和公式}
            \subsubsection{长标题：用于验证横向滚动后仍然能够完整查看每个字和准确点击跳转到源码}
            % FIXME: 核对细节
            \section{公式边界}
            \subsection{高公式 $\frac{\frac{a}{b}}{\frac{c}{\frac{d}{e}}}$}
            \subsection{矩阵 $\begin{pmatrix}a&b\\c&d\end{pmatrix}$ 和根号 $\sqrt{x^2+1}$}
            \subsection{圆括号公式 \(x_i^2\) 和方括号公式 \[\frac{1}{2}\]}
            \subsection{未知公式 $\unknownCommand{x}$}
            """);
        var editor = (LatexEditor)window.FindName("Editor");
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        var tree = (TreeView)outline.FindName("Tree");
        var sidebar = (FrameworkElement)window.FindName("SidebarPanel");
        var column = (ColumnDefinition)window.FindName("SidebarColumn");
        var oldWidth = column.Width;
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        try
        {
            await CallAsync(window, "OpenAsync", main); column.Width = new GridLength(320);
            if ((string)Field(window, "_sidebar")! != "outline" || !(bool)Field(window, "_workspaceExpanded")!)
                Descendants<Button>((FrameworkElement)window.FindName("WorkspaceRail")).Single(button => Equals(button.Tag, "outline")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.NavigateTo(new SourceLocation(main, 8)); await Settle();
            var root = (OutlineNode)tree.Items[0];
            var formulaNode = root.Children[1]; var leaf = formulaNode.Children[0];
            Require(leaf.HasCaret && !root.HasCaret && !formulaNode.HasCaret, "Caret pen did not select the deepest visible heading");
            Require(root.Children[0].HasTodo && formulaNode.HasTodo && leaf.HasTodo && !root.HasTodo,
                "TODO dots did not map comments, commands and FIXME to their owning headings");
            formulaNode.IsExpanded = false; await Settle();
            Require(formulaNode.HasCaret && formulaNode.HasTodo && !leaf.HasCaret && !leaf.HasTodo, "Collapsed branch did not aggregate pen and TODO dot");
            root.IsExpanded = false; await Settle();
            Require(root.HasCaret && root.HasTodo && !formulaNode.HasCaret && !formulaNode.HasTodo,
                "Root collapse left descendant markers visible");
            root.IsExpanded = true; formulaNode.IsExpanded = true; await Settle();
            editor.NavigateTo(new SourceLocation(main, 5)); await Settle();
            Require(formulaNode.HasCaret && !leaf.HasCaret, "Pen did not move independently of viewport highlighting");

            var formulas = Descendants<OutlineFormula>(outline).Where(formula => formula.IsVisible).ToArray();
            Require(formulas.Length == 7 && formulas.Count(formula => formula.IsFormulaRendered) == 6,
                $"Expected six vector formulas and one readable fallback, found {formulas.Length}/{formulas.Count(f => f.IsFormulaRendered)}");
            Require(formulas.All(formula => formula.ActualHeight <= OutlineFormula.MaximumHeight + .1), "Tall formula escaped the height cap");
            Require(Descendants<TreeViewItem>(tree).All(item => ((Border)item.Template.FindName("Row", item)).ActualHeight <= 30.1),
                "Formula inflated outline row height");
            Require(formulas.All(formula => formula.GetValue(TextElement.ForegroundProperty) is SolidColorBrush brush && brush.Color != Colors.Black),
                "Vector formula lost inherited theme color");
            CaptureOutline(sidebar, Path.Combine(output, "outline-markers-math.png"));
            var scroll = Descendants<ScrollViewer>(tree).First();
            Require(scroll.ScrollableWidth < 1 && Descendants<OutlineTitleViewport>(tree).Any(t => t.IsClipped), "Long title did not stay inside bounded viewport");
            Capture(window, Path.Combine(output, "outline-vector-clipping-window.png"));

            editor.Document.Text = editor.Text.Replace("TODO", "DONE").Replace("FIXME", "DONE").Replace("\\todo{核对求和公式}", "核对完毕");
            editor.NavigateTo(new SourceLocation(main, 5));
            await CallAsync(window, "SaveAsync"); await Settle();
            Require(!root.HasTodo && root.Children.All(node => !node.HasTodo) && !leaf.HasTodo, "Resolved TODO dots remained stale");
            Require(formulaNode.HasCaret, "Saving edits lost the caret marker");
            Console.WriteLine("PASS: caret pen, TODO aggregation, native vector math, bounded formula rows and clipped title viewports");
        }
        finally { column.Width = oldWidth; await CallAsync(window, "OpenAsync", previousPath); }
    }
}
