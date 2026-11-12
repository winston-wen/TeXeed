using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static int[] ExpectedLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; starts.Add(i + 1); }
            else if (text[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }
    private static async Task CheckAiFormLineNumbersAsync(MainWindow owner, string output)
    {
        var edit = new TextBox { AcceptsReturn = true, Text = string.Join("\r\n", Enumerable.Range(1, 200).Select(n => "行" + n)) };
        var index = new TextBoxLineNumbers { ClipToAncestorViewport = true }; index.Attach(edit);
        var random = new Random(250);
        var inserts = new[] { "a", "中", "\r\n", "\n", "\r", "", "👩🏽‍💻", "xyz\nq" };
        for (var step = 0; step < 250; step++)
        {
            var offset = random.Next(edit.Text.Length + 1); var remove = random.Next(Math.Min(3, edit.Text.Length - offset) + 1);
            edit.Select(offset, remove); edit.SelectedText = inserts[random.Next(inserts.Length)];
            if (!index.LineStarts.SequenceEqual(ExpectedLineStarts(edit.Text))) throw new Exception("Incremental line indexing diverged at edit " + step);
        }
        using var panel = new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(Path.Combine(output, "line-viewport.tex"));
        var book = (AiNotebook)Field(panel, "_book")!;
        var source = AiForms.WithSubmission(new AiTurn("长表单", "# 练习\n\n{{textarea:long}}"), null); book.Turns.Add(source);
        var original = string.Join("\n", Enumerable.Range(1, 200).Select(n => $"{n:D3} " + new string('长', n % 7 == 0 ? 90 : 8)));
        AiForms.SetDraft(source, "textarea", "long", original);
        var host = new Window { Owner = owner, Width = 620, Height = 760, Content = panel }; host.Show();
        try
        {
            Invoke(panel, "RefreshTranscript"); host.UpdateLayout(); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var view = ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!)[source.Id].Answers.Single(); await view.RenderTask;
            host.UpdateLayout(); await Task.Delay(80); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            var box = Descendants<TextBox>(view).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "long");
            var numbers = Descendants<TextBoxLineNumbers>(view).Single();
            var scroll = (ScrollViewer)Field(panel, "MessagesScroll")!;
            scroll.ApplyTemplate(); var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            async Task CheckVisible(string name)
            {
                host.UpdateLayout(); await Task.Delay(80); await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
                var painted = numbers.PaintedNumbers.ToArray();
                if (painted.Length is < 1 or > 50) throw new Exception(name + " drew a nonvisible or empty set of lines: " + painted.Length);
                foreach (var number in painted)
                {
                    var textRect = box.GetRectFromCharacterIndex(numbers.LineStarts[number - 1]);
                    var visibleRect = box.TransformToVisual(viewport).TransformBounds(textRect);
                    if (visibleRect.Bottom <= -1 || visibleRect.Top >= viewport.ActualHeight + 1) throw new Exception(name + " drew offscreen line " + number);
                }
            }
            scroll.ScrollToTop(); await CheckVisible("top");
            if (numbers.PaintedNumbers.Min() > 3) throw new Exception("Top line numbers did not appear");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); await CheckVisible("middle");
            if (numbers.PaintedNumbers.Min() < 20) throw new Exception("Scrolling did not advance the visible line numbers");
            scroll.ScrollToEnd(); await CheckVisible("bottom");
            if (numbers.PaintedNumbers.Max() < 190) throw new Exception("Bottom line numbers did not follow the viewport");
            var before = box.Text; box.Select(box.Text.Length, 0); box.SelectedText = "X";
            host.UpdateLayout(); await CheckVisible("edited bottom");
            if (box.Text != before + "X" || numbers.LogicalLineCount != 200 || !numbers.LineStarts.SequenceEqual(ExpectedLineStarts(box.Text)))
                throw new Exception("Editing lost text or corrupted line indexes");
            host.Width = 380; host.UpdateLayout(); await CheckVisible("narrow");
            if (box.Text != before + "X") throw new Exception("Resizing changed the form draft");
            Capture(host, Path.Combine(output, "ai-form-lines-visible.png"));
            Console.WriteLine("PASS: incremental 200-line indexing, viewport-only line numbers, wrap, scrolling, resize and draft preservation.");
        }
        finally { host.Close(); }
    }
}
