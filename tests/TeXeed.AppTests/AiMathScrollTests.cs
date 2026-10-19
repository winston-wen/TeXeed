using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task AwaitMathAsync(FrameworkElement root)
    {
        root.UpdateLayout();
        await Task.WhenAll(AnnotationVisuals<MarkdownView>(root).Select(v => v.RenderTask)).WaitAsync(TimeSpan.FromSeconds(30));
        root.UpdateLayout();
        var formulas = AnnotationVisuals<ChatFormula>(root).ToArray();
        await Task.WhenAll(formulas.Select(f => f.RenderTask)).WaitAsync(TimeSpan.FromSeconds(90));
        foreach (var formula in formulas)
            if (formula.Error is not null || formula.SvgPath is null)
                throw new Exception("LaTeX SVG failed: " + formula.Source + "\n" + formula.Error);
        root.UpdateLayout();
    }
    private static readonly string[] ExpectationFormulas =
    [
        @"\mathbb E_F\!\left[H(F(X))\right]=\log_2 N-\mathbb E[K\log_2 K],\qquad K\sim\operatorname{Binomial}\!\left(N,\frac1N\right).",
        @"\mathbb E_F\!\left[H(F(X))\right]\approx\log_2 N-\mathbb E_{K\sim\operatorname{Poisson}(1)}[K\log_2 K].",
        @"\mathbb E[K\log_2 K]=\sum_{k=2}^{\infty}\frac{e^{-1}}{k!}\,k\log_2 k\approx0.827."
    ];
    private static async Task CheckAiMathScrollAsync(MainWindow window, string output)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var ai = (AiPanel)Field(window, "_aiPanel")!;
        var book = (TeXeed.App.AI.AiNotebook)Field(ai, "_book")!;
        book.Turns.Clear(); book.Entries.Clear();
        var content = "## 原文里的期望是怎么来的？\n\n现在再让函数 $F$ 按照前面的随机模型选取，对固定函数的输出熵取平均：\n\n" +
            string.Join("\n\n这里区分固定函数与随机函数模型下的平均输出熵。\n\n", ExpectationFormulas.Select((formula, i) => "$$" + formula + @"\tag{" + (15 + i) + "}$$"));
        book.Turns.Add(new("解释这三个期望公式", content)
        {
            Segments = [new() { Id = "math", Kind = "final", Text = content },
                new() { Id = "activity", Kind = "activity", Text = "展开的计算过程", Details = string.Join("\n", Enumerable.Range(0, 100).Select(i => "计算步骤 " + i)) },
                new() { Id = "tail", Kind = "final", Text = string.Concat(Enumerable.Repeat("后续解释段落，可继续滚动。\n\n", 40)) }]
        });
        Invoke(window, "OpenAiTab"); Invoke(ai, "RefreshTranscript"); window.UpdateLayout(); await Task.Delay(150);
        var scroll = (ScrollViewer)ai.FindName("MessagesScroll"); scroll.ScrollToTop(); window.UpdateLayout();
        var view = AnnotationVisuals<MarkdownView>(ai).First(v => v.Markdown == content);
        await AwaitMathAsync(view);
        scroll.ScrollToTop(); window.UpdateLayout();
        foreach (var formula in ExpectationFormulas)
            Check(AnnotationVisuals<ChatFormula>(view).Any(f => f.Source == formula), "Screenshot formula fell back to source: " + formula);
        Check(view.CopySource(view.Document.ContentStart, view.Document.ContentEnd).Contains(ExpectationFormulas[0]), "Copy replaced original TeX with normalized glyphs");
        foreach (var formula in new[] { @"\mathbb{ABCDEFGHIJKLMNOPQRSTUVWXYZ}", @"\mathbb{abcdefghijklmnopqrstuvwxyz0123456789}", @"\mathbb{E}_F[X]", @"\mathbb E_F[X]" })
        {
            var rendered = new ChatFormula(formula, true); await rendered.RenderTask;
            Check(rendered.Error is null && File.ReadAllText(rendered.SvgPath!).Contains("<path"), "Actual TeX SVG was not generated: " + rendered.Error);
            rendered.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(rendered.DesiredSize.Width > 10 && double.IsFinite(rendered.DesiredSize.Width), "Double-struck alphabet did not render");
        }
        var cachedA = ChatTexRenderer.RenderAsync(ExpectationFormulas[0], true);
        var cachedB = ChatTexRenderer.RenderAsync(ExpectationFormulas[0], true);
        Check(ReferenceEquals(cachedA, cachedB), "Identical math did not coalesce into one cached task");
        var inline = new ChatFormula(@"\frac{a}{b}+x_i^2", false);
        var display = new ChatFormula(@"\frac{a}{b}+x_i^2", true);
        await Task.WhenAll(inline.RenderTask, display.RenderTask);
        inline.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        display.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check(inline.Error is null && display.Error is null && display.DesiredSize.Height > inline.DesiredSize.Height, "Text/display TeX styles did not differ");
        Check(TextBlock.GetBaselineOffset(inline) > 0 && TextBlock.GetBaselineOffset(inline) < inline.DesiredSize.Height, "Inline TeX baseline is outside the box");
        foreach (var source in new[] { @"\input{outside}", @"\csname input\endcsname{x}", @"^^5cinput{x}", @"\begin{document}x\end{document}", @"\special{ps: anything}", @"\write18{echo test}", @"\unknownmacro{x}", @"\frac{a}" })
        {
            var bad = new ChatFormula(source, false); await bad.RenderTask;
            Check(bad.Error is not null && bad.SvgPath is null && bad.Source == source, "Invalid or unsafe formula did not preserve source: " + source);
        }
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            TeXeed.App.Services.ThemeManager.Apply(theme); window.UpdateLayout();
            Check(AnnotationVisuals<ChatFormula>(view).All(f => Equals(f.GetValue(System.Windows.Documents.TextElement.ForegroundProperty), view.TryFindResource("Ink"))), "Equation numbers changed the formula ink color");
            Capture(window, Path.Combine(output, "expectation-formulas-" + theme + ".png"));
        }
        var fullView = new MarkdownView { Markdown = content + "\n\n行内基线：\\(g_i+\\frac{a}{b}+\\sqrt{x^2}\\) 与中文、Latin、上下标排在同一段。", Margin = new Thickness(28) };
        var fullHost = new Window { Owner = window, Width = 1080, Height = 820, Content = new ScrollViewer { Content = fullView }, Title = "LaTeX → SVG" };
        fullHost.Show();
        try
        {
            await AwaitMathAsync(fullView);
            foreach (var theme in new[] { "ristretto", "github-light-default" })
            {
                TeXeed.App.Services.ThemeManager.Apply(theme); fullHost.UpdateLayout();
                Capture(fullHost, Path.Combine(output, "latex-svg-full-" + theme + ".png"));
            }
        }
        finally { fullHost.Close(); }
        TeXeed.App.Services.ThemeManager.Apply("ristretto");
        async Task Wheel(UIElement target, int delta)
        {
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            window.UpdateLayout(); await Task.Delay(30); window.UpdateLayout();
        }
        Check(scroll.ScrollableHeight > 500, "Wheel fixture has no conversation overflow");
        var question = AnnotationVisuals<MarkdownView>(ai).First(t => t.Markdown == "解释这三个期望公式");
        foreach (var target in new UIElement[] { question, view, AnnotationVisuals<ChatFormula>(view).First() })
        {
            scroll.ScrollToVerticalOffset(200); window.UpdateLayout(); var before = scroll.VerticalOffset;
            await Wheel(target, -120); Check(scroll.VerticalOffset > before, "Wheel down was swallowed by " + target.GetType().Name);
            await Wheel(target, 120); Check(scroll.VerticalOffset <= before + 1, "Wheel up was swallowed by " + target.GetType().Name);
        }
        var activity = AnnotationVisuals<Expander>(ai).First(x => x.DataContext is TeXeed.App.AI.AiSegment { Id: "activity" });
        activity.IsExpanded = true; window.UpdateLayout();
        var details = AnnotationVisuals<TextBox>(activity).Single(); details.BringIntoView(); window.UpdateLayout(); await Task.Delay(50);
        var inner = AnnotationVisuals<ScrollViewer>(details).First();
        var outerOffset = scroll.VerticalOffset;
        await Wheel(details, -120);
        Check(inner.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - outerOffset) < 1, "Scrollable activity lost its own wheel scrolling");
        inner.ScrollToBottom(); window.UpdateLayout(); await Wheel(details, -120);
        Check(scroll.VerticalOffset > outerOffset, "Wheel failed to continue into conversation at activity boundary");
        view.Markdown = "$$" + string.Concat(Enumerable.Repeat(@"\mathbb E[X]+", 50)) + "0$$";
        await AwaitMathAsync(view);
        window.UpdateLayout();
        var wide = AnnotationVisuals<ScrollViewer>(view).First(sv => sv.ScrollableWidth > 0);
        var verticalOffset = scroll.VerticalOffset;
        Check(AiPanel.ScrollWheel(wide, -120, true), "Long formula cannot scroll horizontally"); window.UpdateLayout();
        Check(wide.HorizontalOffset > 0 && Math.Abs(scroll.VerticalOffset - verticalOffset) < 1, "Horizontal formula scrolling moved the conversation");
        scroll.ScrollToVerticalOffset(200); window.UpdateLayout(); var overFormula = scroll.VerticalOffset;
        await Wheel(AnnotationVisuals<ChatFormula>(view).First(), -120);
        Check(scroll.VerticalOffset > overFormula, "Long formula swallowed ordinary vertical wheel");
        Console.WriteLine("AI math/scroll: real LaTeX SVG for all three screenshot formulas and alphabets; inline/display styles, baseline, cache, safe source fallback, source copy, both themes, wheel over text/questions/formulas, nested activity boundaries and horizontal formula scrolling passed.");
    }
}
