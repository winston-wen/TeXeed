using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeXeed.App.AI;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class AiHistoryWindow : Window
{
    private readonly AiNotebook _book;
    private readonly Func<string, Task> _delete;
    public string? SelectedConversationId { get; private set; }

    internal AiHistoryWindow(AiNotebook book, Func<string, Task> delete)
    {
        _book = book;
        _delete = delete;
        InitializeComponent();
        WindowTheme.Attach(this);
        Refresh();
        Loaded += (_, _) => SearchBox.Focus();
    }
    internal static IReadOnlyList<AiConversation> Search(AiNotebook book, string query) => book.Conversations
        .Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Turns.Any(t => t.Question.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Answer.Contains(query, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(c => c.UpdatedAt).ToArray();

    private void Refresh()
    {
        var matches = Search(_book, SearchBox.Text.Trim());
        ConversationList.ItemsSource = matches;
        ConversationList.SelectedItem = matches.FirstOrDefault(c => c.Id == _book.ActiveConversationId) ?? matches.FirstOrDefault();
        ResultCount.Text = $"{matches.Count} 个对话";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ConversationList is not null) Refresh(); }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    { OpenButton.IsEnabled = DeleteButton.IsEnabled = ConversationList.SelectedItem is AiConversation; }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not AiConversation conversation) return;
        SelectedConversationId = conversation.Id;
        DialogResult = true;
    }
    private void Conversation_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (e.OriginalSource is DependencyObject) Open_Click(sender, e); }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not AiConversation conversation) return;
        DeleteButton.IsEnabled = false;
        try { await _delete(conversation.Id); Refresh(); }
        catch (Exception ex) { ResultCount.Text = "删除未完成：" + ex.Message; }
    }
}
