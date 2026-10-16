using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private const string MathFixture = """
        ## 从集合到映射

        设随机变量 \(X\) 是均匀随机的 **256 位串**。输出$Y$的熵取决于映射；参见公式 \eqref{mapping}。

        \[
        \mathcal X=\{0,1\}^{256}. \label{domain}
        \]

        $$ F_n:\mathcal X\longrightarrow\mathcal Y,\qquad F_n(x)=\operatorname{hash}(x\Vert 0^n). \tag{A}\label{mapping} $$

        > **注意**：固定函数的输出熵与随机函数模型下的平均输出熵应当区分。

        $$ \frac{1}{n}\sum_{i=1}^{n} x_i^2 + \sqrt{\frac{a}{b}} $$

        :::: columns
        ::: column
        ### 拼接
        - $x\mapsto x\Vert 0^n$ 是单射。
        - 不同输入仍然不同。
        :::
        ::: column
        ### 哈希
        - 不同输入可能碰撞。
        - 信息可能损失。
        :::
        ::::

        | 对象 | 定义 | 说明 |
        | --- | --- | --- |
        | 输入 | $X$ | 均匀随机 |
        | 输出 | $Y$ | 依赖具体映射 |

        `[代码里的 $x$ 与 \(y\)]` 原样保留。
        """;

    private static IEnumerable<T> AnnotationVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in AnnotationVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task CheckAiAnnotationsAsync(MainWindow window, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var view = new MarkdownView { Markdown = MathFixture, Margin = new Thickness(24) };
        var host = new Window { Owner = window, Width = 880, Height = 1100, Content = new ScrollViewer { Content = view }, Title = "只读块式回答 · 数学与分栏" };
        host.Show(); await Task.Delay(100); host.UpdateLayout();
        try
        {
            Capture(host, Path.Combine(output, "ai-math-debug.png"));
            Check(AnnotationVisuals<ChatFormula>(view).Count() >= 8, "Formula rendering fell back to literal TeX: " + AnnotationVisuals<ChatFormula>(view).Count());
            Check(view.Document.Blocks.OfType<Table>().Count() == 2, "Columns or pipe table missing");
            Check(view.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Any(l => new TextRange(l.ContentStart, l.ContentEnd).Text == "(A)"), "Forward equation reference not resolved");
            var copy = view.CopySource(view.Document.ContentStart, view.Document.ContentEnd);
            Check(copy.Contains("\\mathcal X") && copy.Contains("\\frac{1}{n}") && copy.Contains("代码里的 $x$"), "Copy lost mathematical source or changed code");
            var tail = (Paragraph)view.Document.Blocks.Last();
            var anchor = new AiAnswerAnchor("turn", "segment", 0, 0, "代码", MathFixture)
                { TextOffset = view.CopySource(view.Document.ContentStart, tail.ContentStart).Length };
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            { ThemeManager.Apply(theme); host.UpdateLayout(); Capture(host, Path.Combine(output, "ai-blocks-" + theme + ".png")); }
            host.Width = 420; await Task.Delay(100); host.UpdateLayout();
            Check(view.Document.Blocks.OfType<Table>().Count() == 1, "Narrow columns did not stack");
            Check(view.AnnotationPointer("tail", anchor)?.Paragraph == view.Document.Blocks.Last(), "Answer anchor moved after responsive column reflow");
            Capture(host, Path.Combine(output, "ai-blocks-narrow.png"));
            view.Markdown = @"\begin{equation}\frac{a}{b}\label{fraction}\end{equation} 参见 \eqref{fraction}.";
            host.UpdateLayout(); Check(AnnotationVisuals<ChatFormula>(view).Count() == 1, "Equation environment not rendered");
            view.Markdown = @"$$\begin{aligned}a&=b\\c&=d\end{aligned}$$";
            host.UpdateLayout(); Check(AnnotationVisuals<ChatFormula>(view).Count() == 1, "Aligned mathematics not rendered");
            foreach (var input in new[] { "$x", "\\[\\frac{a}", "$$x_", "```tex\n$x$\n\\[x\\]\n```", "\\(\\unknownmacro{x}\\)", "a \\$5 and $x$" })
            { view.Markdown = input; host.UpdateLayout(); Check(!view.CopySource(view.Document.ContentStart, view.Document.ContentEnd).Contains("TEXEED"), "Private math token leaked"); }
        }
        finally { host.Close(); ThemeManager.Apply("ristretto"); }

        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(ai, "_book")!;
        book.Entries.Clear(); book.Turns.Clear(); book.Active.NextAnnotationNumber = 1; Invoke(ai, "Rebind");
        var editor = (LatexEditor)window.FindName("LeftEditor");
        var project = (TexProject)Field(window, "_project")!;
        Invoke(window, "CollectSourceMark", editor, new SourceAiMark(0, 14, false)); window.UpdateLayout(); await Task.Delay(100);
        var sourceEntry = book.Entries.Last();
        Check(sourceEntry.Number == 1 && editor.IsVisible, "Source marking switched away from the source or has no number");
        var layers = (Dictionary<LatexEditor, AiAnnotationLayer>)Field(window, "_sourceAnnotations")!;
        var sourceLayer = layers[editor];
        var popup = (Popup)Field(sourceLayer, "_popup")!;
        Check(popup.IsOpen, "Source marking did not open a local annotation editor");
        var inputBox = AnnotationVisuals<TextBox>(popup.Child).Single();
        inputBox.Text = "这里为什么是单射？";
        Check(sourceEntry.Question == inputBox.Text, "Local annotation did not update shared draft");
        sourceEntry.Question = "编辑栏同步";
        Check(inputBox.Text == "编辑栏同步", "Composer edits did not update popup binding");
        Capture(window, Path.Combine(output, "ai-source-bubble.png"));
        popup.IsOpen = false;
        var original = editor.AnnotationPosition(sourceEntry, project.Root);
        editor.Document.Insert(0, "\n"); window.UpdateLayout();
        var moved = editor.AnnotationPosition(sourceEntry, project.Root);
        Check(original is not null && moved?.Y > original?.Y, "Source bubble did not follow text edits");
        editor.Undo();
        sourceLayer.Edit(sourceEntry); await Task.Delay(100); Check(popup.IsOpen, "Draft bubble cannot reopen the editor"); popup.IsOpen = false;
        await ai.SaveAsync();
        Check(AiStorage.Load(project.MainPath).Entries.Single().Number == 1, "Draft number did not persist");

        book.Turns.Add(new AiTurn("解释映射", MathFixture));
        Invoke(window, "OpenAiTab"); Invoke(ai, "RefreshTranscript");
        window.UpdateLayout(); await Task.Delay(100);
        var answer = AnnotationVisuals<MarkdownView>(ai).First(v => v.Markdown == MathFixture);
        var first = (Paragraph)answer.Document.Blocks.First(); first.BringIntoView(); window.UpdateLayout(); await Task.Delay(100); answer.Selection.Select(first.ContentStart, first.ContentEnd);
        ai.AnnotateAnswer(answer); window.UpdateLayout(); await Task.Delay(100);
        var answerEntry = book.Entries.Last();
        Check(answerEntry.Number == 2 && answerEntry.AnswerAnchor?.Quote.Contains("从集合到映射") == true, "AI answer selection was not anchored");
        var answerLayers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(ai, "_answerLayers")!;
        var answerPopup = (Popup)Field(answerLayers[answer], "_popup")!;
        Check(answerPopup.IsOpen, "Answer annotation did not open locally");
        answerEntry.Question = "解释这一步";
        answerPopup.IsOpen = false;
        Capture(window, Path.Combine(output, "ai-answer-bubble.png"));
        Check(AiResponses.MakeInput([answerEntry], []).ToJsonString().Contains(answerEntry.AnswerAnchor!.TurnId), "Answer quote was not sent with its identity");
        await ai.SaveAsync();
        Check(AiStorage.Load(project.MainPath).Entries.Last().AnswerAnchor == answerEntry.AnswerAnchor, "Answer annotation failed persistence");
        var oldId = book.Active.Id;
        await ai.NewConversationAsync(); window.UpdateLayout();
        Check(ai.AnnotationEntries.Count == 0, "Annotation leaked to another conversation");
        await ai.SelectConversationAsync(oldId); window.UpdateLayout();
        Check(ai.AnnotationEntries.Count == 2, "Conversation switch lost annotations");
        ai.RemoveAnnotation(answerEntry);
        var next = new AiEntry(); ai.AddEntry(next, false);
        Check(next.Number == 3, "Deleted number was reused");
        var tabs = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var preview = (PdfPreview)window.FindName("Preview");
        Invoke(window, "SelectWorkspaceTab", tabs.Owner(TabWorkspace.PreviewKey)!.Value, TabWorkspace.PreviewKey);
        var pdf = Path.Combine(output, "annotation.pdf"); WriteThemePdf(pdf);
        preview.SetCropOptions(false, false); await preview.LoadAsync(pdf); window.UpdateLayout(); await Task.Delay(150); window.UpdateLayout();
        var layouts = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().ToArray();
        var left = (double)layouts[0].GetType().GetProperty("Left")!.GetValue(layouts[0])!;
        var top = (double)layouts[0].GetType().GetProperty("Top")!.GetValue(layouts[0])!;
        typeof(PdfPreview).GetField("_aiSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(preview, new Rect(left + 30, top + 50, 100, 50));
        await preview.ConfirmAiSelectionAsync(); window.UpdateLayout(); await Task.Delay(100);
        var pdfEntry = book.Entries.Last();
        var pdfLayer = AdornerLayer.GetAdornerLayer(preview)!.GetAdorners(preview)!.OfType<AiAnnotationLayer>().Single();
        var pdfPopup = (Popup)Field(pdfLayer, "_popup")!;
        Check(pdfPopup.IsOpen && preview.IsVisible && preview.AnnotationPosition(pdfEntry) is not null, "PDF marking lost its original editor or position");
        pdfEntry.Question = "请解释这一区域"; pdfPopup.IsOpen = false;
        preview.SetZoom(preview.Zoom * 1.2); window.UpdateLayout(); await Task.Delay(100);
        Check(preview.AnnotationPosition(pdfEntry) is not null, "PDF zoom lost the annotation anchor");
        Capture(window, Path.Combine(output, "ai-pdf-bubble.png"));
        pdfEntry.State = "已完成"; pdfLayer.Refresh();
        Check(((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 0, "Completed PDF annotation still has a draft bubble");
        pdfEntry.State = "未完成，可重试"; pdfLayer.Refresh();
        Check(((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 1, "Retry lost the PDF draft bubble");
        Console.WriteLine("AI annotations: math vectors/copy/references, columns/resizing/themes, source popup/binding/anchors, answer quotes, persistence and isolation passed.");
    }
}
