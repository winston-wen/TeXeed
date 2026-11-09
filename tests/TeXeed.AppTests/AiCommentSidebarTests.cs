using System.IO;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static Task CheckAiCommentSidebarAsync(MainWindow owner, string output)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var conversation = new AiConversation();
        conversation.SharedDocument.TryInitialize("abcd", "模型");
        var sidebar = new AiCommentSidebar(); var draftChanges = 0; var documentChanges = 0;
        var host = new Window { Owner = owner, Width = 420, Height = 700, Content = sidebar }; host.Show();
        try
        {
            sidebar.Bind(conversation, "小明", () => draftChanges++, () => documentChanges++);
            Check(sidebar.BeginComment(conversation.SharedDocument.Revision, 1, 2), "Valid source selection rejected");
            host.UpdateLayout();
            Descendants<TextBox>(sidebar).Single().Text = "解释这个位置";
            Check(draftChanges > 0 && conversation.SharedDocument.Comments.Count == 0, "Comment draft was committed or not persisted");
            var project = Path.Combine(output, "sidebar-drafts.tex");
            var book = new AiNotebook { Conversations = [conversation], ActiveConversationId = conversation.Id };
            AiStorage.Save(project, book.Snapshot());
            var loaded = AiStorage.Load(project).Active;
            Check(loaded.CommentDrafts.NewComment == "解释这个位置" && loaded.CommentDrafts.Selection?.Quote == "bc", "Draft or source selection lost on reload");
            Descendants<Button>(sidebar).Single(b => Equals(b.Content, "添加评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
            var comment = conversation.SharedDocument.Comments.Single();
            Check(comment.Text == "解释这个位置" && comment.Author == "小明" && comment.AnchorRevision == conversation.SharedDocument.Revision && documentChanges == 1, "Confirmed comment missing author or source revision");
            var header = Descendants<Button>(sidebar).Single(b => Equals(b.Tag, comment.Id));
            Check(((TextBlock)header.Content).TranslatePoint(new Point(), header).X < 1, "Comment header is not left aligned");
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
            var boxes = Descendants<TextBox>(sidebar).ToArray(); boxes[0].Text = "本地修改";
            comment.Text = "并发修改";
            Descendants<Button>(sidebar).Single(b => Equals(b.Content, "保存修改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(comment.Text == "并发修改" && conversation.CommentDrafts.Edits["c:" + comment.Id].Text == "本地修改", "Conflicting update overwrote content or draft");
            host.UpdateLayout();
            Descendants<TextBox>(sidebar).Last().Text = "本地回复";
            var other = new AiConversation();
            sidebar.Bind(other, null, () => draftChanges++, () => documentChanges++);
            Check(other.SharedDocument.Comments.Count == 0 && conversation.CommentDrafts.Replies[comment.Id] == "本地回复", "Switching conversations mixed comments or drafts");
            sidebar.Bind(conversation, "小明", () => draftChanges++, () => documentChanges++);
            host.UpdateLayout();
            Descendants<Button>(sidebar).Single(b => Equals(b.Tag, comment.Id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
            Check(Descendants<TextBox>(sidebar).Any(b => b.Text == "本地回复"), "Returning to a session lost reply draft");
            Descendants<Button>(sidebar).Single(b => Equals(b.Content, "回复")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(comment.Replies.Single().Author == "小明" && comment.Replies[0].Text == "本地回复", "Reply was not committed with its author");
            Console.WriteLine($"AI comment sidebar: {checks} checks passed.");
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    }
}
