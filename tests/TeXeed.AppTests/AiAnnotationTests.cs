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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
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
            await AwaitMathAsync(view);
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
            await AwaitMathAsync(view);
            Check(view.Document.Blocks.OfType<Table>().Count() == 1, "Narrow columns did not stack");
            Check(view.AnnotationPointer("tail", anchor)?.Paragraph == view.Document.Blocks.Last(), "Answer anchor moved after responsive column reflow");
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

        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var book = (AiNotebook)Field(ai, "_book")!;
        book.Entries.Clear(); book.Turns.Clear(); Invoke(ai, "Rebind");
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
        inputBox.Focus();
        var annotationKey = CreateKey(window, System.Windows.Input.Key.Enter, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        annotationKey.Source = inputBox;
        await CallAsync(window, "HandleShortcutAsync", annotationKey, System.Windows.Input.ModifierKeys.Control);
        Check(!annotationKey.Handled, "Annotation Ctrl+Enter was consumed as compile");
        inputBox.Text = "这里为什么是单射？";
        Check(sourceEntry.Question == inputBox.Text, "Local annotation did not update shared draft");
        sourceEntry.Question = "编辑栏同步";
        Check(inputBox.Text == "编辑栏同步", "Composer edits did not update popup binding");
        Capture(window, Path.Combine(output, "ai-source-bubble.png"));
        sourceEntry.Selected = false;
        var sourceConfirm = CreateKey(window, System.Windows.Input.Key.Enter, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        sourceLayer.HandleEditorKey(sourceConfirm, System.Windows.Input.ModifierKeys.Control);
        Check(sourceConfirm.Handled && !popup.IsOpen && sourceEntry.Selected && book.Entries.Contains(sourceEntry) && book.Turns.Count == 0 && !ai.HasActiveOperation,
            "Source pin Ctrl+Enter did not stage without submitting the conversation");
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
        var first = (Paragraph)((Section)answer.Document.Blocks.First()).Blocks.First(); first.BringIntoView(); window.UpdateLayout(); await Task.Delay(100); answer.Selection.Select(first.ContentStart, first.ContentEnd);
        ai.AnnotateAnswer(answer); window.UpdateLayout(); await Task.Delay(100);
        var answerEntry = book.Entries.Last();
        Check(answerEntry.Number == 2 && answerEntry.AnswerAnchor?.Quote.Contains("合成例题：骰子点数与映射") == true, "AI answer selection was not anchored");
        var answerLayers = (Dictionary<MarkdownView, AiAnnotationLayer>)Field(ai, "_answerLayers")!;
        var answerPopup = (Popup)Field(answerLayers[answer], "_popup")!;
        Check(answerPopup.IsOpen, "Answer annotation did not open locally");
        var answerInput = AnnotationVisuals<TextBox>(answerPopup.Child).Single();
        Check(!answerInput.IsReadOnly, "Answer annotation input inherited a read-only state");
        var pixels = new byte[100 * 60 * 4];
        for (var y = 0; y < 60; y++) for (var x = 0; x < 100; x++)
        { var offset = (y * 100 + x) * 4; pixels[offset] = (byte)(40 + x); pixels[offset + 1] = (byte)(70 + y); pixels[offset + 2] = 180; pixels[offset + 3] = 255; }
        var annotationBitmap = System.Windows.Media.Imaging.BitmapSource.Create(100, 60, 96, 96, PixelFormats.Bgra32, null, pixels, 400);
        Check(AiAnnotationImages.Add(answerEntry, annotationBitmap, out _), "Cannot add a picture to the answer annotation");
        answerPopup.Child.UpdateLayout(); await Task.Delay(100);
        Check(AnnotationVisuals<Image>(answerPopup.Child).Any(i => i.Source is not null), "Pasted image did not appear in the annotation editor");
        Capture(window, Path.Combine(output, "ai-answer-annotation-image.png"));
        for (var i = 0; i < 4; i++) Check(AiAnnotationImages.Add(answerEntry, annotationBitmap, out _), "Cannot create image reuse fixture");
        var originalThumbnails = AnnotationVisuals<Image>(answerPopup.Child).Where(i => i.Source is not null).ToArray();
        Check(originalThumbnails.Length == 5, "Annotation image fixture did not render five thumbnails");
        Check(AiAnnotationImages.Add(answerEntry, annotationBitmap, out _), "Cannot append an annotation image");
        var afterAdd = AnnotationVisuals<Image>(answerPopup.Child).Where(i => i.Source is not null).ToArray();
        Check(afterAdd.Length == 6 && originalThumbnails.Select((image, index) => ReferenceEquals(image, afterAdd[index])).All(x => x), "Adding an image recreated unchanged thumbnails");
        var fourthImage = answerEntry.Images[3];
        var fourthRow = (DockPanel)originalThumbnails[3].Parent;
        ((Button)fourthRow.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var afterRemove = AnnotationVisuals<Image>(answerPopup.Child).Where(i => i.Source is not null).ToArray();
        Check(answerEntry.Images.Count == 5 && !answerEntry.Images.Any(image => ReferenceEquals(image, fourthImage)) &&
            afterRemove.Length == 5 && ReferenceEquals(afterRemove[0], originalThumbnails[0]) && ReferenceEquals(afterRemove[1], originalThumbnails[1]) &&
            ReferenceEquals(afterRemove[2], originalThumbnails[2]) && ReferenceEquals(afterRemove[3], originalThumbnails[4]) && ReferenceEquals(afterRemove[4], afterAdd[5]),
            "Removing a duplicate image removed the wrong item or recreated unchanged thumbnails");
        while (answerEntry.Images.Count > 1) answerEntry.Images.RemoveAt(1);
        if (Environment.GetEnvironmentVariable("TEXEED_INTERACTIVE_INPUT") == "1")
        {
            window.Activate(); await Task.Delay(80);
            var cursorBefore = new NativePoint(); GetCursorPos(out cursorBefore);
            var originalLanguage = System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage;
            try
            {
                System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                var point = answerInput.PointToScreen(new Point(Math.Min(30, answerInput.ActualWidth / 2), 18));
                Check(SetCursorPos((int)point.X, (int)point.Y), "Unable to position mouse over answer annotation");
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(100);
                Console.WriteLine($"Answer annotation interaction: popup={answerPopup.IsOpen}, input={answerInput.ActualWidth:F0}x{answerInput.ActualHeight:F0}, focus={answerInput.IsKeyboardFocusWithin}, readOnly={answerInput.IsReadOnly}, foreground={GetForegroundWindow()}, focusWindow={GetFocus()}");
                Check(answerInput.IsKeyboardFocusWithin, "Clicking the answer annotation input did not focus its editor");
                keybd_event(0x41, 0, 0, UIntPtr.Zero); keybd_event(0x41, 0, 2, UIntPtr.Zero);
                await Task.Delay(100);
                Console.WriteLine($"Answer annotation physical typing: input={System.Text.Json.JsonSerializer.Serialize(answerInput.Text)}, draft={System.Text.Json.JsonSerializer.Serialize(answerEntry.Question)}, focus={answerInput.IsKeyboardFocusWithin}, foreground={GetForegroundWindow()}, focusWindow={GetFocus()}");
                Check(answerInput.Text.Equals("a", StringComparison.OrdinalIgnoreCase) && answerEntry.Question == answerInput.Text, "Physical typing did not update the answer annotation draft");
            }
            finally { System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage = originalLanguage; SetCursorPos(cursorBefore.X, cursorBefore.Y); }
        }
        answerEntry.Question = "解释这一步";
        var answerConfirm = CreateKey(window, System.Windows.Input.Key.Enter, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        answerLayers[answer].HandleEditorKey(answerConfirm, System.Windows.Input.ModifierKeys.Control);
        Check(answerConfirm.Handled && !answerPopup.IsOpen && book.Entries.Contains(answerEntry) && book.Turns.Count == 1 && !ai.HasActiveOperation,
            "Answer pin Ctrl+Enter submitted the conversation");
        Capture(window, Path.Combine(output, "ai-answer-bubble.png"));
        Check(AiResponses.MakeInput([answerEntry], []).ToJsonString().Contains(answerEntry.AnswerAnchor!.TurnId), "Answer quote was not sent with its identity");
        await ai.SaveAsync();
        var savedAnswerEntry = AiStorage.Load(project.MainPath).Entries.Last();
        Check(savedAnswerEntry.AnswerAnchor == answerEntry.AnswerAnchor && savedAnswerEntry.Images.Count == 1, "Answer annotation or image failed persistence");
        var oldId = book.Active.Id;
        await CallAsync(window, "OpenNewAiConversationAsync"); window.UpdateLayout();
        Check(((AiPanel)Field(window, "_aiPanel")!).AnnotationEntries.Count == 0, "Annotation leaked to another conversation");
        await CallAsync(window, "OpenAiConversationAsync", oldId); window.UpdateLayout();
        Check(ReferenceEquals(Field(window, "_aiPanel"), ai) && ai.AnnotationEntries.Count == 2, "Conversation switch lost annotations");
        ai.RemoveAnnotation(answerEntry);
        var next = new AiEntry(); ai.AddEntry(next, false);
        Check(next.Number == 2, "Pending annotations were not renumbered");
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
        pdfEntry.Question = "请解释这一区域";
        preview.SetZoom(preview.Zoom * 1.2); window.UpdateLayout();
        Check(!pdfPopup.IsOpen && pdfEntry.Question == "请解释这一区域" && ai.AnnotationEntries.Contains(pdfEntry), "PDF zoom must close the editor and keep its editable annotation");
        await WaitForPreviewRenderedAsync(window);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Check(preview.AnnotationPosition(pdfEntry) is not null, "PDF zoom lost the annotation anchor");
        Capture(window, Path.Combine(output, "ai-pdf-bubble.png"));
        pdfEntry.State = "已完成"; pdfLayer.Refresh();
        Check(((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 0, "Completed PDF annotation still has a draft bubble");
        pdfEntry.State = "未完成，可重试"; pdfLayer.Refresh();
        Check(((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 1, "Retry lost the PDF draft bubble");
        pdfLayer.Edit(pdfEntry);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(pdfPopup.IsOpen, "PDF pin cannot reopen after zoom");
        var pdfConfirm = CreateKey(window, System.Windows.Input.Key.Enter, System.Windows.Input.Keyboard.PreviewKeyDownEvent);
        pdfLayer.HandleEditorKey(pdfConfirm, System.Windows.Input.ModifierKeys.Control);
        Check(pdfConfirm.Handled && !pdfPopup.IsOpen && book.Entries.Contains(pdfEntry) && book.Turns.Count == 1 && !ai.HasActiveOperation,
            "PDF pin Ctrl+Enter submitted the conversation");
        pdfLayer.Edit(pdfEntry); await Task.Delay(100);
        var previewSide = tabs.Owner(TabWorkspace.PreviewKey)!.Value;
        Invoke(window, "MoveWorkspaceTab", project.MainPath, previewSide, 0, true);
        Invoke(window, "SelectWorkspaceTab", previewSide, project.MainPath); window.UpdateLayout(); await Task.Delay(100);
        Check(!preview.IsVisible && !pdfLayer.IsVisible && ((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 0,
            "Hidden PDF annotation leaked into the source tab");
        Check(!pdfPopup.IsOpen && ai.AnnotationEntries.Contains(pdfEntry), "Switching document panes left a popup or removed its editable annotation");
        Invoke(window, "SelectWorkspaceTab", previewSide, TabWorkspace.PreviewKey); window.UpdateLayout(); await Task.Delay(100);
        Check(pdfLayer.IsVisible && ((System.Collections.ICollection)Field(pdfLayer, "_positions")!).Count == 1, "Returning to PDF lost its annotation");
        Console.WriteLine("AI annotations: math vectors/copy/references, columns/resizing/themes, source popup/binding/anchors, answer quotes, persistence and isolation passed.");
    }
}
