using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckCompositeRevisionSourcesAsync(MainWindow owner, string output)
    {
        var project = Path.Combine(output, "nested-revision-source.tex");
        var store = new AiWorkspaceStore(project, new AiNotebook());
        var root = store.Book.Active;
        const string source = "# 初始标题\n\n> 甲：引文一收尾。\n>\n> 乙：引文二收尾。\n\n- 丙：清单一收尾。\n\n  丁：清单二收尾。\n\n| 戊：表头一收尾。 | 己：表头二收尾。 |\n|------------------|------------------|\n| 庚：单元一收尾。 | 辛：单元二收尾。 |";
        var turn = new AiTurn("复合块", source);
        root.Turns.Add(turn);
        var cases = new[] { "甲：引文一", "乙：引文二", "丙：清单一", "丁：清单二", "戊：表头一", "己：表头二", "庚：单元一", "辛：单元二" };
        var branches = cases.Select(prefix => new AiConversation { Title = prefix[2..], ParentConversationId = root.Id,
            BranchAnchor = new AiAnswerAnchor(turn.Id, turn.Id + "-answer", 0, prefix.Length - 2, prefix[2..], AiStorage.Hash(source)) }).ToArray();
        store.Book.Conversations.AddRange(branches);
        using var panel = new AiPanel();
        panel.Bind(store, root.Id);
        var host = new Window { Content = panel, Owner = owner, Width = 820, Height = 850, Left = -2500, Top = -2500, ShowActivated = false };
        host.Show();
        async Task Layout()
        {
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
        }
        async Task CheckPresent()
        {
            await Layout();
            var answer = ((Dictionary<string, AiTurnView>)Field(panel, "_turnViews")!)[turn.Id].Answers.Single();
            await answer.RenderTask;
            await Layout();
            foreach (var (branch, prefix) in branches.Zip(cases))
            {
                if (!await panel.NavigateBranchSourceAsync(branch.BranchAnchor!, sourceId: branch.Id) ||
                    !answer.BranchLinkInlines.TryGetValue(branch.Id, out var bubble) || bubble.ElementStart.Paragraph is not { } paragraph)
                    throw new Exception("Missing reference in the revised nested block: " + prefix);
                var left = answer.CopySource(paragraph.ContentStart, bubble.ElementStart).Trim();
                var right = new TextRange(bubble.ElementEnd, paragraph.ContentEnd).Text.Trim();
                if (left != prefix || right != "收尾。")
                    throw new Exception($"Nested reference landed in the wrong paragraph, cell or offset: {prefix}, actual={left}|{right}");
            }
        }
        try
        {
            await Layout();
            var draft = panel.BeginRevision(turn);
            var changed = AiRevisionText.Clone(draft.Rows);
            changed[0] = changed[0] with { Text = "已经整理的标题" };
            await panel.CommitRevisionAsync(turn.Id, draft.Version, changed, "user");
            await CheckPresent();
            panel.Bind(await AiWorkspaceStore.LoadAsync(project), root.Id);
            await CheckPresent();
            Console.WriteLine("PASS: eight revised references retained their exact WPF paragraphs in a quote, loose list item, table headers and body cells across reopen");
        }
        finally { host.Close(); }
    }
}
