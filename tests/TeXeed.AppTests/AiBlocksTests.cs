using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private const string MathFixture = """
        ## 合成例题：骰子点数与映射

        设随机变量 \(X\) 是一枚 **六面骰子** 的点数。变换后的$Y$由映射确定；参见公式 \eqref{mapping}。

        \[
        \mathcal X=\{1,2,3,4,5,6\}. \label{domain}
        \]

        $$ F_n:\mathcal X\longrightarrow\mathcal Y,\qquad F_n(x)=\operatorname{min}(x+n,6). \tag{A}\label{mapping} $$

        > **提示**：先计算平移后的点数，再将超过六的结果截断。

        $$ \frac{1}{n}\sum_{i=1}^{n} x_i^2 + \sqrt{\frac{a}{b}} $$

        :::: columns
        ::: column
        ### 平移
        - $x\mapsto x+n$ 是单射。
        - 点数之间的差值保持不变。
        :::
        ::: column
        ### 截断
        - 超过六的点数统一记为六。
        - 不同点数可能得到同一个结果。
        :::
        ::::

        | 对象 | 定义 | 说明 |
        | --- | --- | --- |
        | 输入 | $X$ | 每个点数概率相同 |
        | 输出 | $Y$ | 平移后截断 |

        `[代码里的 $x$ 与 \(y\)]` 原样保留。
        """;

    private static async Task CheckAiBlocksAsync(MainWindow window, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var view = new MarkdownView { Markdown = MathFixture, Margin = new Thickness(24) };
        var host = new Window { Owner = window, Width = 880, Height = 1100, Content = new ScrollViewer { Content = view }, Title = "只读块式回答 · 数学与分栏" };
        host.Show(); await Task.Delay(100); host.UpdateLayout();
        try
        {
            await AwaitMathAsync(view);
            Capture(host, Path.Combine(output, "ai-math-debug.png"));
            Check(AnnotationVisuals<ChatFormula>(view).Count() >= 8, "Formula rendering fell back to literal TeX: " + AnnotationVisuals<ChatFormula>(view).Count());
            Check(view.Document.Blocks.OfType<Table>().Count() == 2, "Columns or pipe table missing");
            Check(view.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Any(l => new TextRange(l.ContentStart, l.ContentEnd).Text == "(A)"), "Forward equation reference not resolved");
            var copy = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            Check(copy.Contains("\\mathcal X") && copy.Contains("\\frac{1}{n}") && copy.Contains("代码里的 $x$"), "Copy lost mathematical source or changed code");
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            { ThemeManager.Apply(theme); host.UpdateLayout(); Capture(host, Path.Combine(output, "ai-blocks-" + theme + ".png")); }
            host.Width = 420; await Task.Delay(100); host.UpdateLayout();
            await AwaitMathAsync(view);
            Check(view.Document.Blocks.OfType<Table>().Count() == 1, "Narrow columns did not stack");
            Capture(host, Path.Combine(output, "ai-blocks-narrow.png"));
            view.Markdown = @"\begin{equation}\frac{a}{b}\label{fraction}\end{equation} 参见 \eqref{fraction}.";
            await AwaitMathAsync(view);
            host.UpdateLayout(); Check(AnnotationVisuals<ChatFormula>(view).Count() == 1, "Equation environment not rendered");
            view.Markdown = @"$$\begin{aligned}a&=b\\c&=d\end{aligned}$$";
            await AwaitMathAsync(view);
            host.UpdateLayout(); Check(AnnotationVisuals<ChatFormula>(view).Count() == 1, "Aligned mathematics not rendered");
            foreach (var input in new[] { "$x", "\\[\\frac{a}", "$$x_", "```tex\n$x$\n\\[x\\]\n```", "\\(\\unknownmacro{x}\\)", "a \\$5 and $x$" })
            { view.Markdown = input; host.UpdateLayout(); Check(!view.CopySource(view.Document.ContentStart, view.Document.ContentEnd).Contains("TEXEED"), "Private math token leaked"); }
        }
        finally { host.Close(); ThemeManager.Apply("ristretto"); }

    }
}
