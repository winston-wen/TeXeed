using System.IO;
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
    private static async Task CheckAiFormulaReferenceAsync(MainWindow owner, string output)
    {
        const string quote = @"$$\operatorname{Round}_{q\to p}(y) = \left\lfloor \frac{[y]_q+4}{8} \right\rfloor\tag{1}$$";
        var original = new AiAnswerAnchor("turn", "segment", 0, quote.Length, quote, "revision");
        var preview = AiReferencePresentation.NormalizePreview(original);
        if (preview.PreviewKind != "formula" || preview.PreviewSource is null || !preview.PreviewSource.Contains("[y]") || preview.PreviewNumber != "1" || original.PreviewKind is not null)
            throw new Exception("Legacy standalone formula preview was not recovered without changing the original anchor");
        var prose = original with { Quote = "先看 $x$ 的定义" };
        if (AiReferencePresentation.NormalizePreview(prose).PreviewKind is not null) throw new Exception("Mixed prose was mistaken for a standalone formula");
        var entry = new AiEntry { AnswerAnchor = original };
        var turn = new AiTurn("历史中的原问题", "答复") { Prompt = "请出题", SentAnnotations = [entry] };
        var view = new AiTurnView { TurnNumber = 1, Turn = turn };
        var host = new Window { Owner = owner, Width = 800, Height = 500, Content = view };
        host.Show();
        try
        {
            await Task.WhenAll(view.Questions.Select(q => q.RenderTask));
            var question = view.Questions.Last();
            var list = (System.Windows.Documents.List)question.Document.Blocks.FirstBlock;
            var paragraph = (Paragraph)list.ListItems.FirstListItem.Blocks.FirstBlock;
            var link = paragraph.Inlines.OfType<Hyperlink>().Single();
            var text = new TextRange(question.Document.ContentStart, question.Document.ContentEnd).Text;
            if (text.Contains("](#snapshot-") || text.Contains("TEXEED_REFERENCE_") || text.Contains("\\left"))
                throw new Exception("A reference was parsed as broken Markdown or raw formula text");
            var expectedLegacy = $"1. [{AiTurnView.LinkLabel(entry)}](#snapshot-1)";
            if (question.Revision != AiStorage.Hash(expectedLegacy)) throw new Exception("Historical reference revision changed");
            var invoked = false; view.AnswerReferenceRequested = e => { invoked = ReferenceEquals(e, entry); return true; };
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            if (!invoked) throw new Exception("The formula reference no longer links to its origin");
            for (var i = 0; i < 200 && !link.Inlines.OfType<InlineUIContainer>().Any(); i++) await Task.Delay(50);
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
            if (link.Inlines.OfType<InlineUIContainer>().SingleOrDefault() is not { Child: ChatFormula visual } embedded || visual.Error is not null)
                throw new Exception("Formula SVG did not render in the reference link");
            if ((string)embedded.Tag != quote || question.CopySource(question.Document.ContentStart, question.Document.ContentEnd).Contains("TEXEED_REFERENCE_"))
                throw new Exception("Formula source was lost when copying the rendered reference");
            Capture(host, Path.Combine(output, "ai-formula-reference.png"));
            Console.WriteLine("Formula reference: old anchor, safe markup, SVG, source link, copy and revision passed.");
        }
        finally { host.Close(); }

        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FollowUpHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "formula-live.tex"));
        var models = (ComboBox)panel.Settings.FindName("Models");
        models.ItemsSource = new[] { new AiModel("fixture", "测试模型") }; models.SelectedIndex = 0;
        var liveHost = new Window { Owner = owner, Width = 800, Height = 600, Content = panel }; liveHost.Show();
        try
        {
            panel.AddEntry(new AiEntry { AnswerAnchor = original });
            ((TextBox)panel.FindName("Composer")).Text = "请出题";
            var operation = panel.SubmitAsync();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var live = (MarkdownView)panel.FindName("LiveQuestion");
            await live.RenderTask;
            var liveText = new TextRange(live.Document.ContentStart, live.Document.ContentEnd).Text;
            if (!liveText.Contains("请出题") || liveText.Contains("](#snapshot-") || liveText.Contains("TEXEED_REFERENCE_") || liveText.Contains("\\left"))
                throw new Exception("The live question exposed broken markup before the reply completed");
            var list = live.Document.Blocks.OfType<System.Windows.Documents.List>().Single();
            var link = ((Paragraph)list.ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
            for (var i = 0; i < 200 && !link.Inlines.OfType<InlineUIContainer>().Any(); i++) await Task.Delay(50);
            if (!link.Inlines.OfType<InlineUIContainer>().Any()) throw new Exception("Live reference did not render its formula SVG");
            liveHost.UpdateLayout(); Capture(liveHost, Path.Combine(output, "ai-formula-reference-live.png"));
            handler.Release.TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(20));
            var book = (AiNotebook)Field(panel, "_book")!;
            if (book.Turns.Count != 1 || book.Turns[0].SentAnnotations.Single().AnswerAnchor != original)
                throw new Exception("Completing the turn changed or lost its reference");
            await panel.StopAndSaveAsync();
            Console.WriteLine("Formula reference: real submit shows the same SVG while the model is pending and preserves the anchor.");
        }
        finally { handler.Release.TrySetResult(); liveHost.Close(); }
    }
}
