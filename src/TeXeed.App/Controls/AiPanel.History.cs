using System.Windows;
using System.Windows.Controls;
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
        if (_store is not null) throw new InvalidOperationException("请从 AI 菜单新建独立会话窗口。");
        if (_project is null) return;
        await FinishConversationWorkAsync();
        if (_book.Turns.Count > 0 || _book.Entries.Count > 0 || Composer.Text.Length > 0) _book.NewConversation();
        ShowCurrentConversation();
        await SaveAsync();
    }
    internal async Task SelectConversationAsync(string id)
    {
        if (_store is not null && id != ConversationId) throw new InvalidOperationException("会话与窗口固定对应，请打开该会话的窗口。");
        if (!_book.Conversations.Any(c => c.Id == id)) return;
        await FinishConversationWorkAsync();
        _book.ActiveConversationId = id;
        ShowCurrentConversation();
        await SaveAsync();
    }
    internal async Task DeleteConversationAsync(string id)
    {
        if (_store is not null) throw new InvalidOperationException("请先关闭会话窗口，再从历史管理删除。");
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
        _restoringReading = true;
        _updating = true;
        Composer.Text = _book.Draft;
        _updating = false;
        SyncPermission(); UpdateContextUsage();
        MessageSearchBox.Clear();
        Rebind(); RefreshTranscript();
        StatusText.Text = "";
        RestoreReadingAnchor();
    }
    private void Options_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
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
