using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private string? _titleEditingId;
    private bool _savingTitle;
    internal void BeginTitleRename()
    {
        if (_project is null || _titleEditingId is not null || _savingTitle) return;
        _headerCloseTimer.Stop(); _titleEditingId = _book.Active.Id;
        TitleEditor.Text = _book.Active.Title;
        ConversationTitle.Visibility = Visibility.Collapsed; TitleEditControls.Visibility = Visibility.Visible;
        ApplyChatHeader(); TitleEditor.Focus(); TitleEditor.SelectAll();
    }
    private void CancelTitleRename()
    {
        _titleEditingId = null; TitleEditControls.Visibility = Visibility.Collapsed; ConversationTitle.Visibility = Visibility.Visible;
        ApplyChatHeader();
    }
    internal async Task CommitTitleRenameAsync()
    {
        if (_titleEditingId is not { } id || _savingTitle) return;
        var title = TitleEditor.Text.Trim();
        if (title.Length == 0) { TitleEditor.Focus(); return; }
        _savingTitle = true; TitleEditControls.IsEnabled = false;
        try { await RenameConversationAsync(id, title); CancelTitleRename(); }
        catch (Exception ex) { ShowError(ex); }
        finally { _savingTitle = false; TitleEditControls.IsEnabled = true; }
    }
    private async void Title_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_titleEditingId is null || _savingTitle) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (ReferenceEquals(source, TitleEditControls)) return;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        await CommitTitleRenameAsync();
    }
    private void Title_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) { e.Handled = true; BeginTitleRename(); } }
    private void Title_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.F2) { e.Handled = true; BeginTitleRename(); } }
    private async void Title_Accept(object sender, RoutedEventArgs e) => await CommitTitleRenameAsync();
    private async void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await CommitTitleRenameAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelTitleRename(); ConversationTitle.Focus(); }
    }
    private void Title_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Background,
        new Action(async () => { if (!TitleEditControls.IsKeyboardFocusWithin) await CommitTitleRenameAsync(); }));
}
