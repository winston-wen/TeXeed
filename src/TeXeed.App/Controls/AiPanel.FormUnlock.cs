using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private Popup? _formUnlockPopup;
    private Button? _formUnlockButton;
    private AiConversation? _formUnlockConversation;
    private AiTurn? _formUnlockSource;
    private bool _formUnlockSaving;
    private int _formUnlockRequest;
    private bool _suppressFormUnlockPrompt;

    private void ShowFormUnlock(AiConversation conversation, AiTurn source, Control anchor, bool focus)
    {
        if (_suppressFormUnlockPrompt) return;
        var request = ++_formUnlockRequest;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (request != _formUnlockRequest || _disposed || !anchor.IsLoaded || !ReferenceEquals(_book.Active, conversation) ||
                !_pendingFormSubmissions.ContainsKey(source.Id) && AiForms.SubmittedFor(conversation, source.Id) is null) return;
            if (_formUnlockPopup is null)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 5, 7, 5) };
                var label = new TextBlock { Text = "已锁定", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _formUnlockButton = new Button { Content = "解锁本轮题目", Padding = new Thickness(7, 2, 7, 2), ToolTip = "保留已提交的答案，重新填写本轮题目" };
                _formUnlockButton.Click += async (_, _) => await ReopenAnswerFormAsync();
                panel.Children.Add(label); panel.Children.Add(_formUnlockButton);
                var border = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
                border.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); border.SetResourceReference(Border.BorderBrushProperty, "Guide");
                _formUnlockPopup = new Popup { Child = border, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
                _formUnlockPopup.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Escape) return;
                    var target = _formUnlockPopup.PlacementTarget as Control;
                    _formUnlockPopup.IsOpen = false; _suppressFormUnlockPrompt = true;
                    try { target?.Focus(); } finally { _suppressFormUnlockPrompt = false; }
                    e.Handled = true;
                };
            }
            _formUnlockConversation = conversation; _formUnlockSource = source;
            _formUnlockButton!.IsEnabled = _operation is null && !_formUnlockSaving && !_queueDispatching && !_formGradeCommitPending && !_pendingFormSubmissions.ContainsKey(source.Id);
            _formUnlockPopup.IsOpen = false; _formUnlockPopup.PlacementTarget = anchor; _formUnlockPopup.IsOpen = true;
            if (focus && _formUnlockButton.IsEnabled) _formUnlockButton.Focus();
        }));
    }

    private async Task ReopenAnswerFormAsync()
    {
        var source = _formUnlockSource; var conversation = _formUnlockConversation;
        if (source is null || conversation is null || _project is null || _disposed || _operation is not null || _queueDispatching || _formUnlockSaving ||
            _formGradeCommitPending || _pendingFormSubmissions.ContainsKey(source.Id) || !ReferenceEquals(_book.Active, conversation) ||
            !conversation.Turns.Contains(source) || AiForms.SubmittedFor(conversation, source.Id) is null) return;
        var book = _book; Dictionary<string, System.Text.Json.JsonElement>? original = null, originalConversation = null;
        DateTimeOffset updated = default; var mutated = false; var paused = false;
        void Mutate()
        {
            original = source.AdditionalData; originalConversation = conversation.AdditionalData; updated = conversation.UpdatedAt;
            if (!AiForms.Reopen(conversation, source.Id)) throw new InvalidOperationException("题目已不处于锁定状态。");
            mutated = true; conversation.UpdatedAt = DateTimeOffset.UtcNow;
            var queue = AiFollowUpQueue.Load(conversation);
            if (!queue.IsPaused && queue.Items.Any(item => item.Kind == "queue" && item.State == "pending"))
            { queue.Pause("题目已解锁，请填写后决定是否继续排队。"); queue.Store(conversation); paused = true; }
        }
        void Rollback() { if (mutated) { source.AdditionalData = original; conversation.AdditionalData = originalConversation; conversation.UpdatedAt = updated; } }
        _formUnlockSaving = true; if (_formUnlockButton is not null) _formUnlockButton.IsEnabled = false;
        try
        {
            book.Draft = Composer.Text;
            if (_store is not null) await _store.SaveWithMutationAsync(Mutate, Rollback);
            else
            {
                await _saveGate.WaitAsync();
                try { try { Mutate(); var snapshot = book.Snapshot(); await Task.Run(() => AiStorage.Save(_project!, snapshot)); } catch { Rollback(); throw; } }
                finally { _saveGate.Release(); }
            }
            if (!_disposed && ReferenceEquals(_book.Active, conversation))
            {
                var values = AiForms.Draft(source); var types = AiForms.FieldTypes(source);
                _updatingFormBindings = true;
                try
                {
                    foreach (var pair in _formBindings.Where(item => item.Key.Conversation == conversation.Id && item.Key.Turn == source.Id))
                    {
                        var value = AiForms.Value(values, pair.Key.Type, pair.Key.Id, AiForms.AllowLegacy(types, pair.Key.Id));
                        foreach (var binding in pair.Value)
                            if (binding.Control is TextBox text && text.Text != value) text.Text = value;
                            else if (binding.Control is RadioButton radio) radio.IsChecked = binding.Option == value;
                            else if (binding.Control is CheckBox check) check.IsChecked = value == "true";
                    }
                }
                finally { _updatingFormBindings = false; }
                if (_formUnlockPopup is not null) _formUnlockPopup.IsOpen = false;
                RefreshFormEnabled(); RefreshFollowUps();
                if (paused) StatusText.Text = "题目已解锁，排队已暂停；填写后可继续。";
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _formUnlockSaving = false;
            if (!_disposed && !_followUpsClosing && _operation is null) _ = DispatchNextQueuedAsync();
        }
    }
}
