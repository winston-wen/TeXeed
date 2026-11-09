using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static int RunAiNativeMentorIntegration(string output)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TeXeed;component/Theme.xaml", UriKind.Relative) });
        var result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAiNativeMentorAsync(output); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }), DispatcherPriority.ApplicationIdle);
        app.Run(); return result;
    }

    private static async Task CheckAiNativeMentorAsync(string output)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        new AiCredentials { ApiKey = "fixture-not-real", UseApiKey = true }.Save();
        using var handler = new MentorIntegrationHandler();
        using var connection = new AiConnection(handler);
        using var panel = new AiPanel(connection);
        var host = new Window { Width = 800, Height = 700, ShowInTaskbar = false, Content = panel };
        host.Show();
        try
        {
            var project = Path.Combine(output, "native-mentor-" + Guid.NewGuid().ToString("N") + ".tex");
            await panel.SetProjectAsync(project);
            var legacyView = (AiDocumentView)panel.FindName("SharedDocumentView");
            var legacyBrowser = Field(legacyView, "_browser")!;
            Check(legacyView.Visibility == Visibility.Collapsed && legacyBrowser.GetType().GetProperty("CoreWebView2")!.GetValue(legacyBrowser) is null,
                "Hidden legacy document view unnecessarily initialized WebView2");
            var models = (ComboBox)panel.Settings.FindName("Models"); models.ItemsSource = new[] { new AiModel("fixture-model", "测试模型") }; models.SelectedIndex = 0;
            var book = (AiNotebook)Field(panel, "_book")!;
            var source = new AiSessionSourceView(book.Active.SharedDocument, AiDocumentRepository.GetDirectory(project, book.Active.Id), new AppSettings());
            panel.SharedDocumentChanged += (_, _) => source.RefreshFromDocument();
            var composer = (TextBox)panel.FindName("Composer");
            composer.Text = "为什么成立？";
            var initial = handler.Enqueue(); var first = panel.SubmitAsync();
            await initial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(((ScrollViewer)panel.FindName("SharedSubmittedMessage")).Visibility == Visibility.Visible, "Submitted note was not shown");
            initial.Answer.TrySetResult("\\documentclass{article}\n\\begin{document}\n这个结论需要条件。\n\\end{document}");
            await first.WaitAsync(TimeSpan.FromSeconds(20));
            Check(!book.Turns.Last().IsIncomplete && source.Editor.Text == book.Active.SharedDocument.Source, "Initial model result did not reach the source editor");
            var start = source.Editor.Text.IndexOf("需要条件", StringComparison.Ordinal);
            Check(panel.BeginSourceComment(source.Revision, start, 4), "Native source selection was rejected");
            host.UpdateLayout();
            var sidebar = (AiCommentSidebar)Field(panel, "_commentSidebar")!;
            Descendants<TextBox>(sidebar).Single().Text = "具体是什么条件？";
            Descendants<Button>(sidebar).Single(b => Equals(b.Content, "添加评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(book.Active.SharedDocument.Comments.Any(c => c.Text == "具体是什么条件？"), "Native comment was not committed");
            composer.Text = "请补充条件"; var invalid = handler.Enqueue(); invalid.Answer.TrySetResult("不是 JSON");
            await panel.SubmitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var failed = book.Turns.Last(); Check(failed.IsIncomplete, "Invalid model response succeeded");
            var frozen = failed.MentorRequest!; Check(frozen.References.Any(r => r.Comment.Text == "具体是什么条件？"), "Comment was missing from the request snapshot");
            book.Active.SharedDocument.Comments[0].Text = "后来修改"; composer.Text = "下一条草稿";
            var retry = handler.Enqueue(); retry.Answer.TrySetResult(JsonSerializer.Serialize(new { initial_latex = (string?)null, edits = new[] { new { old_text = "这个结论需要条件。", new_text = "这个结论需要正整数条件。", reason = "补充条件" } }, comments = Array.Empty<object>(), message = "" }));
            await panel.SubmitAsync(failed).WaitAsync(TimeSpan.FromSeconds(20));
            Check(composer.Text == "下一条草稿" && source.Editor.Text.Contains("正整数条件"), "Retry lost the next draft or failed to update the native source");
            using var request = JsonDocument.Parse(handler.Requests.Last());
            var body = string.Join("\n", request.RootElement.GetProperty("input").EnumerateArray()
                .Where(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                .SelectMany(item => item.GetProperty("content").EnumerateArray())
                .Where(item => item.TryGetProperty("text", out _)).Select(item => item.GetProperty("text").GetString() ?? ""));
            Check(body.Contains("具体是什么条件") && !body.Contains("后来修改"), "Retry did not use the original snapshot");
            Console.WriteLine("Native simulated model, comment snapshot, retry draft and source update verified.");
        }
        finally { host.Close(); }
    }
}
