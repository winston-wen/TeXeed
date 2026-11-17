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
        var literalMath = original with { Quote = "$x$", PreviewParts = [new AiSelectionPart("text", "$x$")] };
        if (AiReferencePresentation.NormalizePreview(literalMath).PreviewKind is not null || AiReferencePresentation.MayContainMath(literalMath))
            throw new Exception("A literal text selection was inferred to be a formula");
        var emptyParts = literalMath with { PreviewParts = [] };
        if (AiReferencePresentation.NormalizePreview(emptyParts).PreviewKind is not null || AiReferencePresentation.MayContainMath(emptyParts))
            throw new Exception("An explicitly empty structured selection was inferred to be a formula");
        var structuredFormula = literalMath with { PreviewParts = [new AiSelectionPart("formula", "x")] };
        if (!AiReferencePresentation.MayContainMath(structuredFormula) || !AiReferencePresentation.Excerpt(structuredFormula).Single().Formula)
            throw new Exception("A genuine structured formula selection was lost");
        const string aligned = @"\begin{aligned}x&=1\\y&=2\end{aligned}";
        var tagged = aligned + @"\tag{Q}";
        var displayAnchor = original with { Quote = "前 $$" + tagged + "$$ 后", PreviewParts = [new("text", "前 "), new("formula", aligned) { Display = true, CopyTex = tagged }, new("text", " 后")] };
        var display = AiReferencePresentation.Excerpt(displayAnchor)[1];
        if (!display.Formula || !display.Display || display.CopyTex != tagged) throw new Exception("Structured block formula lost its rendering mode or copy source");
        var persistedDisplay = System.Text.Json.JsonSerializer.Deserialize<AiAnswerAnchor>(System.Text.Json.JsonSerializer.Serialize(displayAnchor))!;
        var persistedPart = persistedDisplay.PreviewParts![1];
        if (persistedPart.Display != true || persistedPart.CopyTex != tagged) throw new Exception("Formula selection metadata did not survive JSON persistence");
        var oldDisplay = displayAnchor with { PreviewParts = [new("text", "前 "), new("formula", aligned), new("text", " 后")] };
        var recoveredDisplay = AiReferencePresentation.Excerpt(oldDisplay)[1];
        if (!recoveredDisplay.Display || recoveredDisplay.CopyTex != tagged) throw new Exception("Historical structured block formula was not recovered from the full quote");
        var literalAndFormula = displayAnchor with { PreviewParts = [new("text", "$x$"), new("formula", aligned) { Display = true }] };
        if (AiReferencePresentation.Excerpt(literalAndFormula)[0].Formula) throw new Exception("Structured literal dollars were parsed as a formula");
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
            if (list.Padding.Left != 0 || link.TextDecorations is { Count: > 0 })
                throw new Exception("Sent reference retained list indentation or a link underline");
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

        var alignmentView = new AiTurnView { TurnNumber = 1, Turn = new AiTurn("问题", "答复") { Prompt = "问题", SentAnnotations = [entry, new AiEntry { AnswerAnchor = prose }] } };
        var alignmentHost = new Window { Owner = owner, Width = 800, Height = 500, Content = alignmentView }; alignmentHost.Show();
        try
        {
            await Task.WhenAll(alignmentView.Questions.Select(q => q.RenderTask));
            alignmentHost.UpdateLayout();
            var promptView = alignmentView.Questions.First(); var referencesView = alignmentView.Questions.Last();
            var references = (System.Windows.Documents.List)referencesView.Document.Blocks.FirstBlock;
            var links = references.ListItems.Select(item => ((Paragraph)item.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single()).ToArray();
            if (links.Length != 2 || ((Run)links[0].Inlines.FirstInline).Text != "1. " || ((Run)links[1].Inlines.FirstInline).Text != "2. " || references.Padding.Left != 0 || links.Any(link => link.TextDecorations is { Count: > 0 }))
                throw new Exception("Multiple sent references lost their numbers or retained indentation/underlines");
            var promptRect = promptView.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
            var referenceRect = links[0].Inlines.FirstInline.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var promptX = promptView.TranslatePoint(promptRect.TopLeft, alignmentView).X;
            var referenceX = referencesView.TranslatePoint(referenceRect.TopLeft, alignmentView).X;
            if (Math.Abs(promptX - referenceX) > 2) throw new Exception($"Sent references are not aligned with the question: {promptX} / {referenceX}");
        }
        finally { alignmentHost.Close(); }

        const string longFormula = @"\operatorname{Adv}^{\mathrm{dMLWR}}_{D} = \left|\Pr[D(A,s)=1]-\Pr[D(A,u)=1]\right|+\Pr[D(A,t)=0]";
        var quoteText = @"形式化地，对于一个输出 $0$ 或 $1$ 的区分算法 $D$，定义其优势 $$" + longFormula + "$$";
        var mixedParts = new List<AiSelectionPart> { new("text", "形式化地，对于一个输出 "), new("formula", "0"), new("text", " 或 "), new("formula", "1"), new("text", " 的区分算法 "), new("formula", "D"), new("text", "，定义其优势 "), new("formula", longFormula) };
        foreach (var mixed in new[] { original with { Quote = quoteText, PreviewParts = mixedParts }, original with { Quote = quoteText, PreviewParts = null } })
        {
            var mixedEntry = new AiEntry { AnswerAnchor = mixed };
            var mixedTurn = new AiTurn("解释", "答复") { Prompt = "请解释", SentAnnotations = [mixedEntry] };
            var mixedView = new AiTurnView { TurnNumber = 1, Turn = mixedTurn };
            var mixedHost = new Window { Owner = owner, Width = 1000, Height = 500, Content = mixedView }; mixedHost.Show();
            try
            {
                await Task.WhenAll(mixedView.Questions.Select(q => q.RenderTask));
                var mixedQuestion = mixedView.Questions.Last();
                var mixedList = (System.Windows.Documents.List)mixedQuestion.Document.Blocks.FirstBlock;
                var mixedLink = ((Paragraph)mixedList.ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
                for (var i = 0; i < 300 && mixedLink.Inlines.OfType<InlineUIContainer>().Count() < 4; i++) await Task.Delay(50);
                mixedHost.UpdateLayout();
                var embeds = mixedLink.Inlines.OfType<InlineUIContainer>().ToArray();
                if (embeds.Length != 4 || embeds.Any(inline => inline.Child is not ChatFormula and not Grid))
                    throw new Exception("Mixed source citation did not render each inline and block formula");
                var final = embeds[^1];
                if (final.Child is not Grid { ClipToBounds: true, OpacityMask: LinearGradientBrush } clipped || clipped.Margin.Right != 3 || clipped.Children.OfType<ChatFormula>().Single().Source != longFormula || !mixedLink.Inlines.OfType<Run>().Any(run => run.Text.Contains('…')))
                    throw new Exception("A long trailing formula did not show its rendered prefix and ellipsis");
                var copied = mixedQuestion.CopyForClipboard(mixedQuestion.Document.ContentStart, mixedQuestion.Document.ContentEnd);
                if (!copied.Contains(quoteText)) throw new Exception("Copying the source citation lost or truncated its original formula");
                var clicked = false; mixedView.AnswerReferenceRequested = item => { clicked = ReferenceEquals(item, mixedEntry); return true; };
                mixedLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                if (!clicked) throw new Exception("Mixed source citation no longer returns to its origin");
                Capture(mixedHost, Path.Combine(output, mixed.PreviewParts is null ? "ai-mixed-legacy-reference.png" : "ai-mixed-reference.png"));
            }
            finally { mixedHost.Close(); }
        }
        Console.WriteLine("Formula reference: mixed current/legacy excerpts preserve text, complete copy, navigation and clipped long formula.");

        var manyParts = Enumerable.Range(0, 100).Select(index => new AiSelectionPart("formula", "x_{" + index + "}")).ToList();
        var manyQuote = string.Join(" ", manyParts.Select(part => "$" + part.Text + "$"));
        var manyEntry = new AiEntry { AnswerAnchor = original with { Quote = manyQuote, PreviewParts = manyParts } };
        var requested = new List<string>();
        var syntheticSvg = new ChatSvg(new RectangleGeometry(new Rect(0, 0, 20, 12)), new Rect(0, 0, 20, 12), 9, "fixture");
        var selectable = new MarkdownView { ChatMode = true, FormulaAsset = (_, _) => syntheticSvg, Markdown = "文字 $x$。\n\n$$\n" + tagged + "\n$$" };
        var selectableHost = new Window { Owner = owner, Width = 800, Height = 400, Content = selectable }; selectableHost.Show();
        try
        {
            await selectable.RenderTask;
            for (var i = 0; i < 100 && Descendants<ChatFormula>(selectable).Count() < 2; i++) await Task.Delay(20);
            selectable.Selection.Select(selectable.Document.ContentStart, selectable.Document.ContentEnd);
            var captured = selectable.SelectedReferenceParts().Where(part => part.Kind == "formula").ToArray();
            if (captured.Length != 2 || captured[0].Display != false || captured[1].Display != true || captured[1].CopyTex?.Contains(@"\tag{Q}") != true)
                throw new Exception("Selecting mixed inline and block formulas lost their modes or original TeX");
        }
        finally { selectableHost.Close(); }
        var many = new MarkdownView { SnapshotEntries = [manyEntry], FormulaAsset = (tex, _) => { requested.Add(tex); return syntheticSvg; }, Markdown = AiReferencePresentation.Markup([manyEntry]) };
        var batchNotifications = 0;
        many.AddHandler(MarkdownView.AsyncLayoutChangingEvent, new RoutedEventHandler((_, _) => batchNotifications++));
        var manyHost = new Window { Owner = owner, Width = 800, Height = 400, Content = many }; manyHost.Show();
        try
        {
            await many.RenderTask;
            for (var i = 0; i < 30 && Descendants<ChatFormula>(many).Count() < 4; i++) await Task.Delay(20);
            var link = ((Paragraph)((System.Windows.Documents.List)many.Document.Blocks.FirstBlock).ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
            if (!requested.SequenceEqual(new[] { "x_{0}", "x_{1}", "x_{2}", "x_{99}" }) || !link.Inlines.OfType<Run>().Any(run => run.Text == "…"))
                throw new Exception("Large citations must bound formula previews and retain the last formula");
            if (batchNotifications != 1) throw new Exception($"Ready citation formulas should be applied in one layout batch; got {batchNotifications}");
            if (!many.CopyForClipboard(many.Document.ContentStart, many.Document.ContentEnd).Contains(manyQuote))
                throw new Exception("Large citation copy lost formulas omitted from the display");
        }
        finally { manyHost.Close(); }
        Console.WriteLine("Formula reference: large excerpts bound formula rendering and preserve complete copy.");

        // Hold the renderer workers so the second formula cannot finish while the
        // first formula supplied by the fixture is displayed and copied.
        var workers = (SemaphoreSlim)typeof(ChatTexRenderer).GetField("Workers", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var acquired = 0;
        Window? pendingHost = null;
        try
        {
            await workers.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30)); acquired++;
            await workers.WaitAsync().WaitAsync(TimeSpan.FromSeconds(30)); acquired++;
            var slow = "x_{" + Guid.NewGuid().ToString("N")[..12] + "}";
            var slowCopy = slow + @"\tag{9}";
            var pendingQuote = "前 $0$、中 $$" + slowCopy + "$$ 后";
            var pendingEntry = new AiEntry { AnswerAnchor = original with { Quote = pendingQuote, PreviewParts = [new("text", "前 "), new("formula", "0") { Display = false }, new("text", "、中 "), new("formula", slow) { Display = true, CopyTex = slowCopy }, new("text", " 后")] } };
            var pending = new MarkdownView { SnapshotEntries = [pendingEntry], FormulaAsset = (tex, _) => tex == "0" ? syntheticSvg : null, Markdown = AiReferencePresentation.Markup([pendingEntry]) };
            pendingHost = new Window { Owner = owner, Width = 800, Height = 400, Content = pending }; pendingHost.Show();
            await pending.RenderTask;
            var pendingLink = ((Paragraph)((System.Windows.Documents.List)pending.Document.Blocks.FirstBlock).ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
            for (var i = 0; i < 100 && !pendingLink.Inlines.OfType<InlineUIContainer>().Any(); i++) await Task.Delay(20);
            if (pendingLink.Inlines.OfType<InlineUIContainer>().Count() != 1) throw new Exception("A fast citation formula waited for a slower formula");
            var placeholder = pendingLink.Inlines.OfType<Run>().Single(run => run.Text == "公式");
            if (pending.CopyForClipboard(placeholder.ContentStart, placeholder.ContentEnd) != "$$" + slowCopy + "$$") throw new Exception("Copying a pending formula lost its complete TeX");
            if (pending.CopyForClipboard(placeholder.ContentStart, placeholder.ContentStart.GetPositionAtOffset(1)!) != "公") throw new Exception("A partial placeholder selection unexpectedly expanded");
            if (pending.CopyForClipboard(pendingLink.ContentStart, pendingLink.ContentEnd) != pendingQuote) throw new Exception("The full pending citation lost its original source");
        }
        finally { pendingHost?.Close(); if (acquired != 0) workers.Release(acquired); }
        var bad = @"\input{blocked}";
        var badEntry = new AiEntry { AnswerAnchor = original with { Quote = "前 $" + bad + "$ 后", PreviewParts = [new("text", "前 "), new("formula", bad) { Display = false }, new("text", " 后")] } };
        var failed = new MarkdownView { SnapshotEntries = [badEntry], Markdown = AiReferencePresentation.Markup([badEntry]) };
        var failedHost = new Window { Owner = owner, Width = 800, Height = 400, Content = failed }; failedHost.Show();
        try
        {
            await failed.RenderTask;
            var failedLink = ((Paragraph)((System.Windows.Documents.List)failed.Document.Blocks.FirstBlock).ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
            await Task.Delay(200);
            var placeholder = failedLink.Inlines.OfType<Run>().Single(run => run.Text == "公式");
            if (failed.CopyForClipboard(placeholder.ContentStart, placeholder.ContentEnd) != "$" + bad + "$") throw new Exception("Copying a failed formula lost its TeX");
        }
        finally { failedHost.Close(); }
        Console.WriteLine("Formula reference: display metadata, legacy recovery, pending and failed copy, and independent fast rendering passed.");

        var literalEntry = new AiEntry { AnswerAnchor = literalMath };
        var literalQuestion = new MarkdownView { SnapshotEntries = [literalEntry], Markdown = AiReferencePresentation.Markup([literalEntry]) };
        var literalLink = ((Paragraph)((System.Windows.Documents.List)literalQuestion.Document.Blocks.FirstBlock).ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
        if (literalLink.Inlines.OfType<InlineUIContainer>().Any() || !new TextRange(literalLink.ContentStart, literalLink.ContentEnd).Text.Contains("$x$"))
            throw new Exception("A literal dollar expression did not remain literal in its citation");

        const string comment = "请详细核对这一条推导与数据。";
        var commentedEntry = new AiEntry { AnswerAnchor = original with { Quote = quoteText, PreviewParts = mixedParts }, Question = comment };
        var commented = new MarkdownView { SnapshotEntries = [commentedEntry], FormulaAsset = (_, _) => syntheticSvg, Markdown = AiReferencePresentation.Markup([commentedEntry]) };
        var commentedLink = ((Paragraph)((System.Windows.Documents.List)commented.Document.Blocks.FirstBlock).ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
        var fullCopy = commented.CopyForClipboard(commentedLink.ContentStart, commentedLink.ContentEnd);
        if (fullCopy != quoteText + " · " + comment || !commented.CopyForClipboard(commented.Document.ContentStart, commented.Document.ContentEnd).Contains(fullCopy))
            throw new Exception("Copying a complete commented citation lost the original source or comment");
        var textRun = commentedLink.Inlines.OfType<Run>().First();
        var partialEnd = textRun.ContentStart.GetPositionAtOffset(3)!;
        if (commented.CopyForClipboard(textRun.ContentStart, partialEnd) != textRun.Text[..3])
            throw new Exception("Copying part of a citation unexpectedly expanded to the full source or comment");
        var lastTextRun = commentedLink.Inlines.OfType<Run>().Last();
        if (commented.CopyForClipboard(textRun.ContentStart, lastTextRun.ContentEnd) != fullCopy)
            throw new Exception("Selecting all visible glyphs in a citation did not copy the full source and comment");
        var surrounding = new Run(" 引文之后的文字");
        ((Paragraph)commentedLink.Parent).Inlines.Add(surrounding);
        if (!commented.CopyForClipboard(textRun.ContentStart, surrounding.ContentEnd).Contains(fullCopy + " 引文之后的文字"))
            throw new Exception("Copying a complete citation with following content lost the trailing selection");
        Console.WriteLine("Formula reference: structured literal dollars remain text; complete commented copy preserves the original and partial copy stays partial.");

        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new FollowUpHandler();
        using var panel = new AiPanel(new AiConnection(handler));
        await panel.SetProjectAsync(Path.Combine(output, "formula-live-" + Guid.NewGuid().ToString("N") + ".tex"));
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
            if (list.Padding.Left != 0 || link.TextDecorations is { Count: > 0 })
                throw new Exception("Live sent reference retained list indentation or a link underline");
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
