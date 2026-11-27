using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static async Task CheckCjkFormulaTypographyAsync(Window owner, string output, bool realTex)
    {
        const string display = @"s_k=\sum_{j=1}^5(\alpha_{k,j}-\beta_{k,j}),\quad 0\leq k<N,\qquad \alpha_{k,j},\beta_{k,j}\overset{\$}{\gets}\{0,1\}\quad\text{（所有位相互独立）}";
        const string inline = @"\Pr(\text{所有位相互独立})=p_{独立}";
        var cache = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg"));
        foreach (var (name, tex, isDisplay) in new[] { ("display", display, true), ("inline", inline, false) })
        {
            var file = Path.Combine(cache.FullName, ChatTexRenderer.CacheFileName(tex, isDisplay));
            if (!realTex)
            {
                using var embedded = typeof(Program).Assembly.GetManifestResourceStream($"TeXeed.AppTests.Fixtures.formula-cjk.{name}.svg")
                    ?? throw new FileNotFoundException("Missing compiled formula sample", name);
                using var destination = File.Create(file);
                embedded.CopyTo(destination);
            }
            var picture = await ChatTexRenderer.RenderAsync(tex, isDisplay);
            if (picture.CjkFont is not ("Source Han Serif SC" or "Noto Serif CJK SC" or "SimSun" or "FandolSong-Regular.otf") ||
                picture.Geometry.Bounds.IsEmpty)
                throw new Exception($"No Chinese serif outlines in {name}: {picture.CjkFont}");
            Console.WriteLine($"{name}: {picture.CjkFont}; {picture.Path}");
        }

        var theme = ThemeManager.Current.Id;
        ThemeManager.Apply("ristretto");
        var answer = new MarkdownView { ChatMode = true,
            Markdown = "按位独立的情形，记作 $" + inline + "$。\n\n$$" + display + @"\tag{2}$$" };
        var host = new Window { Owner = owner, Content = new Border { Child = answer, Padding = new Thickness(18) },
            Width = 1100, SizeToContent = SizeToContent.Height, Left = -3000, Top = -3000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.SetResourceReference(Window.BackgroundProperty, "PanelBackground");
        host.Show();
        try
        {
            await answer.RenderTask;
            await AwaitMathAsync(answer);
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            var formulas = Descendants<ChatFormula>(answer).ToArray();
            if (!formulas.Any(f => f.Source == display) || !formulas.Any(f => f.Source == inline) ||
                formulas.Any(f => f.Error is not null || f.Asset?.CjkFont is null))
                throw new Exception("The reading surface did not display the Chinese TeX assets");
            Capture(host, Path.Combine(output, "formula-cjk.png"), 2);
        }
        finally { host.Close(); ThemeManager.Apply(theme); }
    }
}
