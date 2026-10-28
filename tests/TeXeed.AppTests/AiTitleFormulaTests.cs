using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private sealed record InkSample(Rect Bounds, double Baseline, string Text, int Weight, string Face);
    private static List<InkSample> GlyphInk(FrameworkElement element, Visual relative)
    {
        var result = new List<InkSample>();
        foreach (var visual in Descendants<Visual>(element).Prepend(element))
        {
            void Visit(Drawing drawing, Matrix transform)
            {
                if (drawing is DrawingGroup group)
                {
                    var next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
                    foreach (var child in group.Children) Visit(child, next);
                }
                else if (drawing is GlyphRunDrawing glyph)
                {
                    var run = glyph.GlyphRun; var bounds = run.ComputeInkBoundingBox(); if (bounds.IsEmpty || bounds.Height == 0) return;
                    bounds.Offset((Vector)run.BaselineOrigin);
                    var parent = visual.TransformToAncestor(relative);
                    result.Add(new(parent.TransformBounds(new MatrixTransform(transform).TransformBounds(bounds)), parent.Transform(transform.Transform(run.BaselineOrigin)).Y,
                        run.Characters is { } characters ? new string(characters.ToArray()) : "", run.GlyphTypeface.Weight.ToOpenTypeWeight(), Path.GetFileName(run.GlyphTypeface.FontUri.LocalPath)));
                }
            }
            if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Visit(drawing, Matrix.Identity);
        }
        return result;
    }
    private static async Task CheckAiTitleFormulaAsync(MainWindow owner, string output, string? baseline, bool enforce)
    {
        var view = new MarkdownView { ChatMode = true, TurnNumber = 7 };
        view.ConfigureFolding(new(), "title-font", () => { });
        var source = string.Join("\n\n", Enumerable.Range(1, 6).Select(level => new string('#', level) + " 标题中轴 Heading $x_i^2$\n\n正文第一行。  \n正文第二行。"));
        const string shortFormula = "E=mc^2";
        var longFormula = string.Join("+", Enumerable.Range(0, 70).Select(i => "a_{" + i + "}"));
        view.Markdown = source + "\n\n$$" + shortFormula + "$$\n\n$$" + longFormula + "$$";
        var host = new Window { Owner = owner, Content = new ScrollViewer { Content = view }, Width = 800, Height = 900, Title = "标题字形与公式布局 · 合成资料" };
        var titleResults = new List<object>(); var numberResults = new List<object>();
        host.Show();
        try
        {
            host.UpdateLayout(); await AwaitMathAsync(view); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var ink = GlyphInk(view, view).Where(i => i.Text.Contains("标题中轴")).OrderBy(i => i.Bounds.Top).ToArray();
            if (ink.Length != 6) throw new Exception("Did not render six Chinese heading samples: " + ink.Length);
            var headingFormulas = Descendants<ChatFormula>(view).Where(f => f.Source == "x_i^2").OrderBy(f => f.TranslatePoint(new Point(), view).Y).ToArray();
            for (var i = 0; i < 6; i++)
            {
                var formula = headingFormulas[i]; var box = formula.TransformToAncestor(view).TransformBounds(new Rect(formula.RenderSize));
                titleResults.Add(new { level = i + 1, center = ink[i].Bounds.Top + ink[i].Bounds.Height / 2, ink[i].Baseline, ink[i].Weight, ink[i].Face,
                    formulaY = box.Y, formulaWidth = box.Width, formulaHeight = box.Height });
                if (enforce && ink[i].Weight > 350) throw new Exception("Heading did not select a light typeface: " + ink[i].Face);
            }
            var display = Descendants<ChatFormula>(view).Single(f => f.Source == shortFormula);
            var rows = Descendants<ChatFormulaRow>(view).ToArray();
            var displayRow = rows.Single(r => Descendants<ChatFormula>(r).Contains(display));
            var body = view.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward).X;
            var indent = display.TranslatePoint(new Point(), view).X - body;
            if (enforce && Math.Abs(indent - 4 * view.FontSize) > .5) throw new Exception("Display formula indentation differs from four Chinese advances: " + indent);
            var longRow = rows.Single(r => Descendants<ChatFormula>(r).Any(f => f.Source == longFormula));
            var horizontal = Descendants<ScrollViewer>(longRow).Single();
            if (horizontal.ScrollableWidth <= 100) throw new Exception("Long equation did not overflow into its own horizontal viewport");
            Capture(host, Path.Combine(output, "titles-equations-left.png"));
            horizontal.ScrollToRightEnd(); host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (horizontal.HorizontalOffset < horizontal.ScrollableWidth - 1) throw new Exception("Long equation cannot reach its right end");
            Capture(host, Path.Combine(output, "titles-equations.png"));
            var sourceCopy = view.CopyForClipboard(view.Document.ContentStart, view.Document.ContentEnd);
            if (!sourceCopy.Contains(longFormula)) throw new Exception("Equation layout changed its copied TeX source");
            host.Width = 420; host.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (enforce && Math.Abs(display.TranslatePoint(new Point(), view).X - body - 4 * view.FontSize) > .5) throw new Exception("Narrow equation lost its four-character indent");
            if (horizontal.ScrollableWidth <= 100) throw new Exception("Narrow equation lost its local horizontal viewport");
            Capture(host, Path.Combine(output, "titles-equations-narrow.png"));
            host.Width = 800;

            foreach (var question in new[] { "我想知道 Rm 元素的表示。", "An English question about Rm.", "**粗体中文首行**\n\n下一段。", "中文与 `code` 混排首行。", "" })
            {
                var turn = new AiTurn(question, "合成回答。") { Prompt = question, SentAnnotations = question.Length == 0 ? [new() { Question = "圈点首行的中文说明。" }] : [] };
                var turnView = new AiTurnView { TurnNumber = 3, Turn = turn }; host.Content = turnView;
                host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var number = Descendants<TextBlock>(turnView).Single(t => t.Text == "[3]");
                var numberInk = GlyphInk(number, turnView).Select(i => i.Bounds).Aggregate(Rect.Empty, (all, rect) => { all.Union(rect); return all; });
                var questionInk = Descendants<MarkdownView>(turnView).Where(v => !v.ChatMode).SelectMany(v => GlyphInk(v, turnView)).ToArray();
                var firstBaseline = questionInk.Min(i => i.Baseline);
                var firstLine = questionInk.Where(i => Math.Abs(i.Baseline - firstBaseline) < 1).Select(i => i.Bounds).Aggregate(Rect.Empty, (all, rect) => { all.Union(rect); return all; });
                var delta = numberInk.Top + numberInk.Height / 2 - firstLine.Top - firstLine.Height / 2;
                numberResults.Add(new { question, delta });
                if (enforce && Math.Abs(delta) > .5) throw new Exception("Hanging number ink center differs from the actual first line: " + question + " " + delta);
                Capture(host, Path.Combine(output, "number-axis-" + numberResults.Count + ".png"));
            }
            var result = new { titles = titleResults, numbers = numberResults, displayIndent = indent };
            File.WriteAllText(Path.Combine(output, "title-layout.json"), JsonSerializer.Serialize(result));
            if (baseline is not null)
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(baseline));
                using var current = JsonDocument.Parse(JsonSerializer.Serialize(result));
                for (var i = 0; i < 6; i++)
                    foreach (var metric in new[] { "center", "formulaY", "formulaWidth", "formulaHeight" })
                        if (Math.Abs(previous.RootElement.GetProperty("titles")[i].GetProperty(metric).GetDouble() - current.RootElement.GetProperty("titles")[i].GetProperty(metric).GetDouble()) > .5)
                            throw new Exception("Font change shifted heading/formula geometry: " + metric);
            }
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        finally { host.Close(); }
    }
}
