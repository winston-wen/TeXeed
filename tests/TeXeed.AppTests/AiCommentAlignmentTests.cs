using System.IO;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static Task CheckCommentAlignmentAsync(Window owner, string output)
    {
        var document = new AiSharedDocument(); document.TryInitialize("Hello.", "model");
        var comment = new AiDocumentComment { Start = 0, Length = 5, Quote = "Hello", Text = "需要检查这一处。", Author = "模型" };
        document.Comments.Add(comment);
        var conversation = new AiConversation { SharedDocument = document };
        var sidebar = new AiCommentSidebar(); sidebar.Bind(conversation, "用户", () => { }, () => { });
        using var preview = new AiSessionPdfView(Path.Combine(output, "align-project.tex"), conversation.Id, document, new AppSettings());
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sidebar); Grid.SetColumn(preview, 1); grid.Children.Add(preview);
        var host = new Window { Owner = owner, Content = grid, Width = 800, Height = 600, ShowInTaskbar = false };
        host.Show();
        try
        {
            host.UpdateLayout();
            var sidebarRow = Descendants<Button>(sidebar).Single(b => Equals(b.Tag, comment.Id));
            var header = (TextBlock)sidebarRow.Content;
            if (GlyphInk(header, sidebarRow).Min(g => g.Bounds.Left) > 4) throw new Exception("Session comment header's first glyph is not left aligned.");
            Descendants<Button>(preview).Single(b => Equals(b.Content, "只读评论")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); host.UpdateLayout();
            var pdfRow = Descendants<Button>(preview).Single(b => b.Content is TextBlock label && label.Text.Contains("需要检查这一处。"));
            if (GlyphInk((TextBlock)pdfRow.Content, pdfRow).Min(g => g.Bounds.Left) > 4) throw new Exception("Session PDF comment's first glyph is not left aligned.");
            Capture(host, Path.Combine(output, "session-comment-alignment.png"));
            Console.WriteLine("Session comment and PDF comment glyph alignment passed.");
        }
        finally { host.Close(); }
        return Task.CompletedTask;
    }
}
