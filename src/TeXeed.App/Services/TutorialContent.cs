using System.Xml.Linq;
using TeXeed.App.Controls;

namespace TeXeed.App.Services;

internal static class TutorialContent
{
    private static readonly Dictionary<string, ChatSvg> Assets = [];
    internal static ChatSvg? Formula(string source, bool display)
    {
        var name = source.Trim() switch { "E = mc^2" => "energy", @"\sum_{k=1}^{n} k = \frac{n(n+1)}{2}" => "sum", _ => null };
        if (name is null) return null;
        if (Assets.TryGetValue(name, out var asset)) return asset;
        using var stream = typeof(TutorialContent).Assembly.GetManifestResourceStream($"TeXeed.App.Assets.Tutorial.{name}.svg")
            ?? throw new InvalidOperationException("教程公式资源缺失：" + name);
        return Assets[name] = ChatSvg.Read(XElement.Load(stream), "embedded:tutorial-" + name + ".svg");
    }
    internal const string Markdown = """
        # LaTeX 教程
        从第一份文档开始。

        ## 01  创建与排版
        点击“新建文档”，保存为 main.tex。中文文档可使用下面的示例，在“排版 → 设置”中选择 **XeLaTeX**，然后按 **Ctrl+S** 保存并编译。

        ```tex
        \documentclass{ctexart}
        \begin{document}
        \section{你好，LaTeX}
        这是我的第一份文档。
        \[ E = mc^2 \]
        \end{document}
        ```

        其中的公式显示为：

        \[ E = mc^2 \]

        首次编译文档前，请先安装 TeX Live 或 MiKTeX，并确保已安装 ctex 宏包。

        ## 02  章节与文字
        用 `\section{标题}` 创建章节，`\subsection{小标题}` 创建小节。空一行开始新段落；`\textbf{文字}` 表示粗体，`\emph{文字}` 表示强调。

        ## 03  数学公式
        行内公式放在 `$…$` 中，独立公式放在 `\[ … \]` 中。用 `^` 和 `_` 表示上下标，`\frac{a}{b}` 表示分式，`\sqrt{x}` 表示根号。

        ```tex
        \[ \sum_{k=1}^{n} k = \frac{n(n+1)}{2} \]
        ```

        \[ \sum_{k=1}^{n} k = \frac{n(n+1)}{2} \]

        ## 04  在 TeXeed 中继续写作
        - **Ctrl+S**：保存并编译
        - **Ctrl+Enter**：编译文档
        - **Ctrl+J**：从源码定位到 PDF
        - **Ctrl+左键点击 PDF**：返回对应源码
        - **Ctrl+Space**：命令补全
        """;
}
