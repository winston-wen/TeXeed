using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckConversationEquationReferencesAsync(Window owner, string output, string single, string multi, bool windowMouse)
    {
        using var connection = new AiConnection(new AiFakeHandler());
        using var panel = new AiPanel(connection);
        var host = new Window { Owner = owner, Content = panel, Width = 720, Height = 640,
            Left = windowMouse ? 40 : -12000, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = windowMouse, ShowInTaskbar = false };
        var project = Path.Combine(output, "cross-turn-equations.tex");
        var source = "# 定义\n\n$$" + single + @"\label{eq:decode}$$" + "\n\n## 展开\n\n$$" + multi + "$$";
        var target = new AiTurn("定义", source) { Collapsed = true, FoldGroup = "target-group" };
        var origin = new AiTurn("引用", @"由公式\eqref{2:eq:decode}、\eqref{2:2}和\eqref{2:eq-2}说明。缺失\eqref{66:1}。本轮\eqref{40:example}。" +
            "\n\n$$" + single + @"\label{example}$$");
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            host.Show(); await panel.SetProjectAsync(project);
            var book = (AiNotebook)Field(panel, "_book")!;
            book.Turns.Clear();
            book.Turns.Add(new("第一轮", "正文") { Collapsed = true, FoldGroup = "target-group" });
            book.Turns.Add(target);
            var segment = target.Id + "-answer";
            foreach (var heading in MarkdownView.BookmarkFragments(source).Where(f => f.Kind == AiBookmarkKind.Heading))
                target.CollapsedSections[segment + ":" + heading.Position] = true;
            while (book.Turns.Count < 39) book.Turns.Add(new("隔开目标", "很远的正文\n\n中间若干轮次"));
            book.Turns.Add(origin);
            Invoke(panel, "RefreshTranscript");
            var scroll = (ScrollViewer)panel.FindName("MessagesScroll");
            async Task<MarkdownView> OpenSource()
            {
                Require(await panel.NavigateSameConversationTurnAsync(origin.Id), "Could not navigate to the referencing turn");
                await SettleFoldLayoutAsync(host);
                var view = Descendants<AiTurnView>(panel).Single(t => t.Turn.Id == origin.Id).Answers.Single();
                await view.RenderTask; await SettleFoldLayoutAsync(host);
                return view;
            }
            Hyperlink Link(MarkdownView view, string caption) =>
                ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().First(l => ((Run)l.Inlines.FirstInline!).Text == caption);
            var view = await OpenSource();
            Require(Link(view, "(1)") is not null && ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>()
                .Count(l => ((Run)l.Inlines.FirstInline!).Text == "(2:2)") == 2, "Incorrect aliases or redundant same-turn prefix");
            var missing = Link(view, "(66:1 ?)");
            ClickEquation(missing);
            Require(Field(view, "_equationPreview") is Popup { Child: Border { Child: TextBlock { Text: "未找到引用的公式" } } }, "Out-of-session reference resolved");
            foreach (var entry in new[] { (Caption: "(2:1)", Tex: single), (Caption: "(2:2)", Tex: multi) })
            {
                view = await OpenSource(); var link = Link(view, entry.Caption);
                var offset = scroll.VerticalOffset;
                var y = view.TranslatePoint(link.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft, scroll).Y;
                var wasCollapsed = target.Collapsed;
                var wasRealized = Descendants<AiTurnView>(panel).Any(v => v.Turn.Id == target.Id);
                Require(!wasCollapsed || !wasRealized, "The compacted target should be virtualized");
                Require(y >= 0 && y < scroll.ViewportHeight, $"Reference is not in reading viewport: {y}, {scroll.ViewportHeight}");
                if (windowMouse) await SendEquationClicksAsync(host, view, link, 1);
                else ClickEquation(link);
                await SettleFoldLayoutAsync(host);
                var preview = Field(view, "_equationPreview") as Popup;
                Require(preview is { IsOpen: true }, "Missing cross-turn popup " + entry.Caption);
                var formula = Descendants<ChatFormula>(preview!.Child).Single();
                await formula.RenderTask;
                var previewSurface = (FrameworkElement)preview.Child;
                SaveLiteralVisual(previewSurface, Path.Combine(output, entry.Tex == single ? "cross-preview-single.png" : "cross-preview-aligned.png"));
                var glyphs = EquationNumberGlyphs(Descendants<ChatEquationNumber>(previewSurface).Single(), entry.Caption);
                Require(formula.Source == entry.Tex && File.Exists(formula.SvgPath) && target.Collapsed == wasCollapsed &&
                    Descendants<AiTurnView>(panel).Any(v => v.Turn.Id == target.Id) == wasRealized && Math.Abs(scroll.VerticalOffset - offset) < 1 &&
                    Math.Abs(view.TranslatePoint(link.ContentStart.GetCharacterRect(LogicalDirection.Forward).TopLeft, scroll).Y - y) < 1,
                    "Preview changed the transcript, unfolded the target or displayed the wrong equation " + entry.Caption);
                if (windowMouse)
                {
                    await Task.Delay((int)GetDoubleClickTime() + 40);
                    await SendEquationClicksAsync(host, view, link, 2);
                }
                else DoubleClickEquation(link);
                await WaitUntilAsync(() => Descendants<AiTurnView>(panel).Any(t => t.Turn.Id == target.Id &&
                    Descendants<ChatFormula>(t).Any(f => f.IsVisible && f.Source == entry.Tex)), "Double-click missed the target turn");
                await SettleFoldLayoutAsync(host);
                var targetView = Descendants<AiTurnView>(panel).Single(t => t.Turn.Id == target.Id);
                var shown = Descendants<ChatFormula>(targetView).Single(f => f.Source == entry.Tex && f.IsVisible);
                var top = shown.TranslatePoint(new Point(), scroll).Y;
                CheckEquationNumberFace(glyphs, shown, entry.Tex == single ? "(1)" : "(2)");
                Require(!preview.IsOpen && !target.Collapsed && book.Turns[0].Collapsed && top >= -1 &&
                    top + shown.ActualHeight <= scroll.ViewportHeight + 1,
                    $"Double click missed {entry.Caption}: y={top}, viewport={scroll.ViewportHeight}.");
                Capture(host, Path.Combine(output, entry.Tex == single ? "cross-turn-single.png" : "cross-turn-aligned.png"));
            }

            view = await OpenSource();
            var draft = panel.BeginRevision(target);
            var insertion = JsonSerializer.SerializeToElement(new[] { new { row = draft.Rows[0].Id, position = "before",
                paragraphs = new[] { new { level = 0, parts = new[] { new { kind = "text", value = "$$" + multi + "$$" } } } } } });
            await panel.CommitRevisionAsync(target.Id, draft.Version, AiPanel.ApplyRevisionChanges(draft, insertion), "ai", "renumber");
            await SettleFoldLayoutAsync(host);
            Require(Link(view, "(2:2)") == ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().First(),
                "Visible symbolic link retained the old number after revision");
            var numbers = ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().Take(3)
                .Select(link => ((Run)link.Inlines.FirstInline!).Text);
            Require(numbers.SequenceEqual(["(2:2)", "(2:3)", "(2:3)"]), "Visible numerical aliases switched to a different equation");
            var readerDraft = panel.BeginRevision(origin);
            var revisedReader = readerDraft.Rows.Select((r, i) => i == 0 ? r with { Text = r.Text + "已校对。" } : r).ToList();
            await panel.CommitRevisionAsync(origin.Id, readerDraft.Version, revisedReader, "ai");
            var readerText = book.Turns[^1].Answer;
            await panel.SaveAsync(); await panel.SetProjectAsync(project);
            view = await OpenSource();
            var renewed = ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().First();
            Require(((Run)renewed.Inlines.FirstInline!).Text == "(2:2)", "Reopened conversation retained the old number");
            ClickEquation(renewed); await SettleFoldLayoutAsync(host);
            Require(Field(view, "_equationPreview") is Popup { Child: { } reopened } &&
                Descendants<ChatFormula>(reopened).Single().Source == single, "Reopened label lost its exact target");
            ClickEquation(Link(view, "(2:3)")); await SettleFoldLayoutAsync(host);
            Require(Field(view, "_equationPreview") is Popup { Child: { } numerical } &&
                Descendants<ChatFormula>(numerical).Single().Source == multi, "Reopened numeric reference preview retargeted");
            book = (AiNotebook)Field(panel, "_book")!;
            await panel.DeleteTurnAsync(book.Turns[0].Id);
            await panel.SetProjectAsync(project);
            view = await OpenSource();
            Require(Link(view, "(1:2)") == ((Paragraph)view.Document.Blocks.FirstBlock!).Inlines.OfType<Hyperlink>().First() &&
                view.Markdown == readerText, "Deleting a preceding turn rewrote the source or changed the referenced object after reopening");
            var savedReader = ((AiNotebook)Field(panel, "_book")!).Turns.Single(t => t.Id == origin.Id);
            var readerNumber = ((AiNotebook)Field(panel, "_book")!).Turns.FindIndex(t => t.Id == origin.Id) + 1;
            Invoke(panel, "ShowRevisionHistory", savedReader, "original", readerNumber, null!);
            var historical = host.OwnedWindows.OfType<Window>().Single(w => w.Title.Contains("编辑历史"));
            try
            {
                var historyView = Descendants<MarkdownView>(historical).Single();
                await historyView.RenderTask; await SettleFoldLayoutAsync(historical);
                var frozen = Link(historyView, "(2:3)");
                ClickEquation(frozen); await SettleFoldLayoutAsync(historical);
                Require(Field(historyView, "_equationPreview") is Popup { Child: { } historicalPreview } &&
                    Descendants<ChatFormula>(historicalPreview).Single().Source == multi,
                    "Historical reader reinterpreted a saved number against today's turn or formula ordering");
            }
            finally { historical.Close(); }
            await panel.DeleteTurnAsync(target.Id);
            view = await OpenSource();
            var orphan = Link(view, "(2:eq:decode ?)");
            ClickEquation(orphan);
            Require(Field(view, "_equationPreview") is Popup { Child: Border { Child: TextBlock { Text: "未找到引用的公式" } } },
                "Deleting the target incorrectly retargeted its surviving reference");
            var duplicate = target with { Answer = source + "\n\n$$" + multi + @"\label{eq:decode}$$" };
            Require(AiEquationIndex.Resolve(duplicate, "eq:decode") is null &&
                AiEquationIndex.Read("literal \u0060" + @"\eqref{2:eq:decode}" + "\u0060").Count == 0,
                "Duplicate labels or code samples produced a spurious match");
            Console.WriteLine("PASS: cross-turn labels and numeric aliases preview offline without scroll, navigate folded virtualized single/multiline targets, refresh after revisions and survive reload.");
        }
        finally { host.Close(); }
    }
}
