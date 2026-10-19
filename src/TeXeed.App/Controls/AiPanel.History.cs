using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private IReadOnlyList<AiTurn> _messageMatches = [];
    private int _messageMatchIndex;

    private async Task FinishConversationWorkAsync()
    {
        _operation?.Cancel();
        if (_operationDone is { } work) await work.Task;
        _saveTimer.Stop();
        await SaveAsync();
    }
    internal async Task NewConversationAsync()
    {
        if (_project is null) return;
        await FinishConversationWorkAsync();
        if (_book.Turns.Count > 0 || _book.Entries.Count > 0 || Composer.Text.Length > 0) _book.NewConversation();
        ShowCurrentConversation();
        await SaveAsync();
    }
    internal async Task SelectConversationAsync(string id)
    {
        if (!_book.Conversations.Any(c => c.Id == id)) return;
        await FinishConversationWorkAsync();
        _book.ActiveConversationId = id;
        ShowCurrentConversation();
        await SaveAsync();
    }
    internal async Task DeleteConversationAsync(string id)
    {
        await FinishConversationWorkAsync();
        if (!_book.RemoveConversation(id)) return;
        ShowCurrentConversation();
        await SaveAsync();
    }
    internal async Task DeleteTurnAsync(string id)
    {
        if (_operation is not null) return;
        _book.Turns.RemoveAll(t => t.Id == id);
        _book.Active.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshTranscript();
        await SaveAsync();
    }
    private void ShowCurrentConversation()
    {
        _restoringReading = true; ResetHeaderHover();
        _updating = true;
        Composer.Text = _book.Draft;
        _updating = false;
        SyncPermission(); UpdateContextUsage();
        MessageSearchBox.Clear();
        Rebind(); RefreshTranscript();
        ConversationTitle.Text = AiConversation.MakeTitle(_book.Active.Title);
        ApplyChatHeader();
        StatusText.Text = "";
        RestoreReadingAnchor();
    }
    private async void NewConversation_Click(object sender, RoutedEventArgs e)
    { try { await NewConversationAsync(); Composer.Focus(); } catch (Exception ex) { ShowError(ex); } }
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
            HistorySearchBox.Clear(); RefreshHistory();
            HistoryPopup.IsOpen = true;
            HistorySearchBox.Focus();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void RefreshHistory()
    {
        var matches = AiHistoryWindow.Search(_book, HistorySearchBox.Text.Trim());
        HistoryList.ItemsSource = matches;
        HistoryList.SelectedItem = matches.FirstOrDefault(c => c.Id == _book.ActiveConversationId) ?? matches.FirstOrDefault();
        HistoryEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void HistorySearch_Changed(object sender, TextChangedEventArgs e) { if (HistoryList is not null) RefreshHistory(); }
    private async void HistoryOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiConversation conversation) return;
        try { HistoryPopup.IsOpen = false; await SelectConversationAsync(conversation.Id); Composer.Focus(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiConversation conversation) return;
        try { await DeleteConversationAsync(conversation.Id); RefreshHistory(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || HistoryList.SelectedItem is not AiConversation conversation) return;
        e.Handled = true;
        try { HistoryPopup.IsOpen = false; await SelectConversationAsync(conversation.Id); Composer.Focus(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Options_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
    private void FindMessage_Click(object sender, RoutedEventArgs e)
    {
        MessageSearchPanel.Visibility = MessageSearchPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (MessageSearchPanel.IsVisible) MessageSearchBox.Focus();
    }
    private void MessageSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (MessageMatchStatus is null) return;
        var text = MessageSearchBox.Text.Trim();
        _messageMatches = text.Length == 0 ? [] : _book.Turns.Where(t => t.Question.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Answer.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        _messageMatchIndex = 0;
        ShowMessageMatch();
    }
    private void PreviousMatch_Click(object sender, RoutedEventArgs e) { _messageMatchIndex--; ShowMessageMatch(); }
    private void NextMatch_Click(object sender, RoutedEventArgs e) { _messageMatchIndex++; ShowMessageMatch(); }
    private void ShowMessageMatch()
    {
        MessageMatchStatus.Text = _messageMatches.Count == 0 ? "0" : $"{(_messageMatchIndex = (_messageMatchIndex % _messageMatches.Count + _messageMatches.Count) % _messageMatches.Count) + 1}/{_messageMatches.Count}";
        if (_messageMatches.Count == 0) return;
        var target = _messageMatches[_messageMatchIndex];
        VisualChildren<ChatVirtualizingPanel>(History).FirstOrDefault()?.Reveal(_transcriptItems.IndexOf(target));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        { if (History.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement item) item.BringIntoView(); }));
    }
}
