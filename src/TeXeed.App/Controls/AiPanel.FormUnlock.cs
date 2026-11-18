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
    private TextBlock? _formUnlockLabel;
    private AiConversation? _formUnlockConversation;
    private AiTurn? _formUnlockSource;
    private AiTurn? _formUnlockWaitingSource;
    private CancellationTokenSource? _formUnlockWait;
    private FrameworkElement? _formUnlockWaitingAnchor;
    private RoutedEventHandler? _formUnlockWaitingAnchorUnloaded;
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
                _formUnlockLabel = new TextBlock { Text = "已锁定", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                _formUnlockLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _formUnlockButton = new Button { Content = "解锁本轮题目", Padding = new Thickness(7, 2, 7, 2), ToolTip = "保留已提交的答案，重新填写本轮题目" };
                _formUnlockButton.Click += async (_, _) => { if (_formUnlockWait is { } wait) { wait.Cancel(); if (_formUnlockPopup is not null) _formUnlockPopup.IsOpen = false; } else await ReopenAnswerFormAsync(); };
                panel.Children.Add(_formUnlockLabel); panel.Children.Add(_formUnlockButton);
                var border = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
                border.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); border.SetResourceReference(Border.BorderBrushProperty, "Guide");
                _formUnlockPopup = new Popup { Child = border, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
                _formUnlockPopup.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != Key.Escape) return;
                    _formUnlockWait?.Cancel();
                    var target = _formUnlockPopup.PlacementTarget as Control;
                    _formUnlockPopup.IsOpen = false; _suppressFormUnlockPrompt = true;
                    try { target?.Focus(); } finally { _suppressFormUnlockPrompt = false; }
                    e.Handled = true;
                };
            }
            if (_formUnlockWait is { } waiting && ReferenceEquals(_formUnlockWaitingSource, source))
            {
                SetFormUnlockWaitingAnchor(anchor);
                _formUnlockPopup.PlacementTarget = anchor;
                if (focus) _formUnlockButton!.Focus();
                return;
            }
            if (_formUnlockWait is { } previous)
            {
                previous.Cancel(); SetFormUnlockWaitingAnchor(null); _formUnlockWaitingAnchorUnloaded = null;
                _formUnlockWait = null; _formUnlockWaitingSource = null;
                _formUnlockPopup.StaysOpen = false;
            }
            _formUnlockConversation = conversation; _formUnlockSource = source;
            _formUnlockLabel!.Text = _formUnlockWait is null ? "已锁定" : "正在排版，稍后解锁";
            _formUnlockButton!.Content = _formUnlockWait is null ? "解锁本轮题目" : "取消";
            _formUnlockButton.IsEnabled = _formUnlockWait is not null || _operation is null && !_formUnlockSaving && !_queueDispatching && !_formGradeCommitPending && !_pendingFormSubmissions.ContainsKey(source.Id);
            _formUnlockPopup.IsOpen = false; _formUnlockPopup.PlacementTarget = anchor; _formUnlockPopup.IsOpen = true;
            if (focus && _formUnlockButton.IsEnabled) _formUnlockButton.Focus();
        }));
    }

    private void SetFormUnlockWaitingAnchor(FrameworkElement? anchor)
    {
        if (_formUnlockWaitingAnchor is not null && _formUnlockWaitingAnchorUnloaded is not null)
            _formUnlockWaitingAnchor.Unloaded -= _formUnlockWaitingAnchorUnloaded;
        _formUnlockWaitingAnchor = anchor;
        if (anchor is not null && _formUnlockWaitingAnchorUnloaded is not null)
            anchor.Unloaded += _formUnlockWaitingAnchorUnloaded;
    }

    private static Task[] PendingFormRendering(AiTurnView view) => view.Answers.Select(answer => answer.RenderTask)
        .Concat(VisualChildren<ChatFormula>(view).Select(formula => formula.RenderTask))
        .Where(task => !task.IsCompleted).Distinct().ToArray();

    private async Task ReopenAnswerFormAsync()
    {
        var source = _formUnlockSource; var conversation = _formUnlockConversation;
        if (source is null || conversation is null || _project is null || _disposed || _operation is not null || _queueDispatching || _formUnlockSaving || _formUnlockWait is not null ||
            _formGradeCommitPending || _pendingFormSubmissions.ContainsKey(source.Id) || !ReferenceEquals(_book.Active, conversation) ||
            !conversation.Turns.Contains(source) || AiForms.SubmittedFor(conversation, source.Id) is null) return;
        var book = _book; Dictionary<string, System.Text.Json.JsonElement>? original = null, originalConversation = null;
        if (_turnViews.TryGetValue(source.Id, out var sourceView))
        {
            var rendering = PendingFormRendering(sourceView);
            if (rendering.Length > 0)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _formUnlockWait = wait; _formUnlockWaitingSource = source;
                if (_formUnlockLabel is not null) _formUnlockLabel.Text = "正在排版，稍后解锁";
                if (_formUnlockButton is not null) { _formUnlockButton.Content = "取消"; _formUnlockButton.IsEnabled = true; }
                if (_formUnlockPopup is not null) _formUnlockPopup.StaysOpen = true;
                var lostSource = false;
                DependencyPropertyChangedEventHandler hidden = (_, _) => { if (!IsVisible) wait.Cancel(); };
                RoutedEventHandler unloaded = (_, _) => wait.Cancel();
                RoutedEventHandler sourceUnloaded = (_, _) => { lostSource = true; wait.Cancel(); };
                _formUnlockWaitingAnchorUnloaded = sourceUnloaded;
                SetFormUnlockWaitingAnchor(_formUnlockPopup?.PlacementTarget as FrameworkElement);
                EventHandler closed = (_, _) => wait.Cancel();
                IsVisibleChanged += hidden; Unloaded += unloaded; sourceView.Unloaded += sourceUnloaded;
                if (_formUnlockPopup is not null) _formUnlockPopup.Closed += closed;
                try
                {
                    do
                    {
                        await Task.WhenAll(rendering).WaitAsync(wait.Token);
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        wait.Token.ThrowIfCancellationRequested();
                        rendering = PendingFormRendering(sourceView);
                    } while (rendering.Length > 0);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                    if (lostSource && ReferenceEquals(_formUnlockWait, wait) && !_disposed && IsVisible)
                    {
                        if (_formUnlockPopup is not null) _formUnlockPopup.IsOpen = false;
                        if (string.IsNullOrEmpty(StatusText.Text))
                        {
                            const string notice = "已离开题目，解锁已取消。";
                            StatusText.Text = notice;
                            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
                            timer.Tick += (_, _) => { timer.Stop(); if (!_disposed && StatusText.Text == notice) StatusText.Text = ""; };
                            timer.Start();
                        }
                    }
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) ShowError(ex); return; }
                finally
                {
                    IsVisibleChanged -= hidden; Unloaded -= unloaded; sourceView.Unloaded -= sourceUnloaded;
                    if (_formUnlockPopup is not null) _formUnlockPopup.Closed -= closed;
                    if (ReferenceEquals(_formUnlockWait, wait))
                    {
                        SetFormUnlockWaitingAnchor(null); _formUnlockWaitingAnchorUnloaded = null;
                        _formUnlockWait = null; _formUnlockWaitingSource = null;
                        if (_formUnlockPopup is not null) _formUnlockPopup.StaysOpen = false;
                        if (!_disposed && _formUnlockLabel is not null) _formUnlockLabel.Text = "已锁定";
                        if (!_disposed && _formUnlockButton is not null) _formUnlockButton.Content = "解锁本轮题目";
                    }
                }
                if (_disposed || _operation is not null || _queueDispatching || _formGradeCommitPending || _formUnlockSaving ||
                    !ReferenceEquals(book, _book) || !ReferenceEquals(_book.Active, conversation) || !conversation.Turns.Contains(source) ||
                    _pendingFormSubmissions.ContainsKey(source.Id) || AiForms.SubmittedFor(conversation, source.Id) is null) return;
            }
        }
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
                if (_formUnlockPopup is not null && ReferenceEquals(_formUnlockSource, source) && ReferenceEquals(_formUnlockConversation, conversation)) _formUnlockPopup.IsOpen = false;
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
