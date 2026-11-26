using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void SeedRevisionFormula(string tex, bool display = true)
    {
        var key = ChatTexRenderer.CacheFileName(tex, display);
        using var rendered = typeof(Program).Assembly.GetManifestResourceStream("TeXeed.AppTests.Fixtures.revision." + key)
            ?? throw new FileNotFoundException("Missing compiled SVG for " + tex, key);
        var cache = Directory.CreateDirectory(Path.Combine(AiStorage.Home, "math-svg"));
        using var cached = File.Create(Path.Combine(cache.FullName, key));
        rendered.CopyTo(cached);
    }

    private static async Task CheckSnapshotHistoryAsync(MainWindow owner, string output, bool measureOnly = false, bool realTex = false)
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        const string tex = @"w'-w_0=\delta";
        const string revisedTex = @"\delta_0=w'-w_0=\delta_1";
        if (!measureOnly && !realTex)
        {
            SeedRevisionFormula(tex);
            SeedRevisionFormula(revisedTex);
        }
        var source = string.Concat(Enumerable.Repeat("先看两个差值。", measureOnly ? 1 : 200)) +
            "\n\n$$" + tex + @"\label{difference}\tag{6}$$" + "\n\n这是后文。";
        var project = Path.Combine(output, "snapshot-history.tex");
        var store = new AiWorkspaceStore(project, new AiNotebook());
        var conversation = store.Book.Active;
        conversation.Permission = AiPermission.FullAccess;
        for (var i = 1; i <= 8; i++) conversation.Turns.Add(new("先前的问题", "先前的回答。") { Collapsed = true });
        var sourceTurn = new AiTurn("第九轮的两个差值有什么关系？", source);
        conversation.Turns.Add(sourceTurn);
        for (var i = 10; i <= 15; i++) conversation.Turns.Add(new("其他问题", "其他回答。") { Collapsed = true });
        var anchor = new AiAnswerAnchor(sourceTurn.Id, sourceTurn.Id + "-answer", 0, 20, "$$" + tex + "$$", AiStorage.Hash(source))
            { PreviewKind = "formula", PreviewSource = tex, PreviewNumber = "6" };
        var entry = AiSessionExcerptReferences.Create(conversation.Id, anchor, null, null);
        entry.Question = "改公式 6，加入我设立的差值。";
        var reply = new AiTurn("批注", @"请看公式 \eqref{9:difference}。") { Prompt = "", SentAnnotations = [entry] };
        conversation.Turns.Add(reply);
        using var panel = new AiPanel();
        panel.Bind(store, conversation.Id);
        var host = new Window { Owner = owner, Content = panel, Width = 840, Height = 800, Left = -2700, Top = -2400, ShowActivated = false };
        host.Show();
        async Task Settle()
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        static IEnumerable<Hyperlink> Links(FlowDocument doc)
        {
            for (var p = doc.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                    p.GetAdjacentElement(LogicalDirection.Forward) is Hyperlink link) yield return link;
        }
        try
        {
            await panel.NavigateSameConversationTurnAsync(reply.Id);
            await Settle();
            var turns = (Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!;
            MarkdownView Question() => turns[reply.Id].Questions.Single();
            Hyperlink? Marker() => Links(Question().Document).SingleOrDefault(link => Equals(link.ToolTip, "查看批注时的版本"));
            async Task Edit(int row, string text)
            {
                var draft = panel.BeginRevision(conversation.Turns[8]);
                var rows = AiRevisionText.Clone(draft.Rows);
                rows[row] = rows[row] with { Text = text };
                await panel.CommitRevisionAsync(sourceTurn.Id, draft.Version, rows, "ai");
                await Settle();
            }
            Require(Marker() is null, "Unmodified quote incorrectly marked as stale");
            await Edit(2, "只调整最后这句话。");
            Require(Marker() is null, "An unrelated sentence in the same segment marked the equation as changed");
            var measurements = new List<object>();
            for (var i = 0; i < (measureOnly ? 7 : 1); i++)
            {
                var next = i % 2 == 0 ? source.Replace(tex, revisedTex) : source;
                var clock = Stopwatch.StartNew();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                await Edit(1, next.Split("\n\n")[1]);
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                measurements.Add(new { milliseconds = clock.Elapsed.TotalMilliseconds, bytes,
                    visibleControls = Descendants<Control>(host).Count(c => c.IsVisible), historyLinks = Marker() is null ? 0 : 1 });
            }
            File.WriteAllText(Path.Combine(output, "snapshot-history.json"), JsonSerializer.Serialize(measurements));
            if (measureOnly) return;
            var marker = Marker() ?? throw new Exception("Missing update link beside the revised equation annotation");
            var q = Question();
            var copied = q.CopyForClipboard(q.Document.ContentStart, q.Document.ContentEnd);
            Require(copied.Contains(tex) && !copied.Contains("原文已更新") && !copied.Contains(@"\delta_0"), "Copy leaked updated math or the history affordance into the old quote");
            Capture(host, Path.Combine(output, "snapshot-history.png"));
            marker.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); await Settle();
            var history = Application.Current.Windows.OfType<Window>().Single(w => w.Title.Contains("第 9 轮 · 编辑历史"));
            var old = Descendants<MarkdownView>(history).Single();
            await old.RenderTask;
            await Settle();
            Require(old.Markdown.Contains(tex) && !old.Markdown.Contains(@"\delta_0") && old.Markdown.Contains("这是后文"),
                "History did not open at the complete version seen by the author of the annotation");
            Require(old.VerticalOffset > 0, $"A long historical answer opened at {old.VerticalOffset} (extent {old.ExtentHeight}, viewport {old.ViewportHeight}) instead of revealing the annotated equation");
            history.Close();
            Links(q.Document).Single(link => link.Name == "SnapshotReference").RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await Settle();
            Require(turns[sourceTurn.Id].Answers.Single().Markdown.Contains(@"\delta_0"), "Source navigation failed to show revised math");
            var saved = AiStorage.Load(project).Active;
            Require(saved.Turns.Last().SentAnnotations.Single().AnswerAnchor?.PreviewSource == tex, "Persisting the update changed the sent excerpt");
            Console.WriteLine("PASS: changed excerpt only, immutable copy, historical version and latest source navigation");
        }
        finally { host.Close(); }
    }
}
