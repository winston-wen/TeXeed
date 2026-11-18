using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private bool _syncingChoices;
    private bool _modelPreferenceSaving;
    private TaskCompletionSource? _modelPreferenceIdle;
    private readonly CancellationTokenSource _lifetime = new();

    private void InitializeConversationModel()
    {
        if (_project is null) return;
        if (_book.Active.SelectedModelId is null)
        {
            if (Settings.SelectedModel is not { } model) return;
            _book.Active.SelectedModelId = model.Id;
            _book.Active.SelectedReasoningEffort = Settings.SelectedEffort;
        }
        else if (_book.Active.SelectedReasoningEffort is null)
            _book.Active.SelectedReasoningEffort = _book.Active.SelectedModelId == Settings.SelectedModel?.Id ? Settings.SelectedEffort : "";
        else return;
        Dirty();
    }

    private string? ConversationModelId => _book.Active.SelectedModelId ?? Settings.SelectedModel?.Id;
    private string ConversationEffort => _book.Active.SelectedReasoningEffort ?? Settings.SelectedEffort;

    private string? ConversationModelProblem()
    {
        var available = Settings.AvailableModels;
        if (available.Count == 0 || ConversationModelId is null) return null;
        var model = available.FirstOrDefault(item => item.Id == ConversationModelId);
        if (model is null) return "当前会话选择的模型暂不可用，请刷新模型或选择其他模型。";
        return AiEffort.Choices(model).Any(item => item.Value == ConversationEffort) ? null : "当前会话的推理强度暂不可用，请重新选择。";
    }

    private void SyncModelChoices()
    {
        InitializeConversationModel();
        var selectedId = ConversationModelId;
        var available = Settings.AvailableModels;
        var model = available.FirstOrDefault(item => item.Id == selectedId);
        var displayed = model ?? (selectedId is null ? null : new AiModel(selectedId, selectedId + "（当前不可用）"));
        var models = model is null && displayed is not null ? available.Append(displayed).ToArray() : available.ToArray();
        var selectedEffort = ConversationEffort;
        var efforts = AiEffort.Choices(model);
        if (selectedEffort.Length > 0 && !efforts.Any(item => item.Value == selectedEffort))
            efforts.Add(new AiEffort(selectedEffort, AiEffort.Label(selectedEffort) + "（当前不可用）"));
        _syncingChoices = true;
        try
        {
            ModelPicker.ItemsSource = models;
            ModelPicker.SelectedItem = displayed;
            EffortPicker.ItemsSource = efforts;
            EffortPicker.SelectedItem = efforts.FirstOrDefault(e => e.Value == selectedEffort) ?? efforts[0];
        }
        finally { _syncingChoices = false; }
        ModelPicker.ToolTip = selectedId is not null && model is null ? "当前会话选择的模型暂不可用，请刷新模型或选择其他模型。" : "选择当前会话的模型";
        EffortPicker.ToolTip = selectedEffort.Length > 0 ? "本次请求明确使用：" + AiEffort.Label(selectedEffort) :
            "不发送 reasoning.effort，由服务端决定。" + (model?.DefaultReasoningEffort is { Length: > 0 } value ? "模型目录默认值：" + AiEffort.Label(value) : "模型目录未报告默认强度。");
        UpdateContextUsage();
        ResizeChoices();
    }
    private async Task RefreshModelChoicesAsync()
    {
        if (_operation is not null) return;
        try
        {
            if (!AiSettingsPanel.HasConnection()) { SyncModelChoices(); StatusText.Text = "请先在 AI 设置中登录 ChatGPT 或保存 API Key。"; return; }
            ModelControls.IsEnabled = false;
            await Settings.LoadModelsAsync(_lifetime.Token);
            SyncModelChoices();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (!_disposed) ModelControls.IsEnabled = _operation is null && !_modelPreferenceSaving; }
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelChoicesAsync();
    private async void ChatModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices) return;
        if (_modelPreferenceSaving || _operation is not null || _queueDispatching || _followUpsClosing) { SyncModelChoices(); return; }
        if (ModelPicker.SelectedItem is not AiModel selected) return;
        var model = Settings.AvailableModels.FirstOrDefault(item => item.Id == selected.Id);
        if (model is null) { SyncModelChoices(); return; }
        var effort = AiEffort.Choices(model).Any(item => item.Value == ConversationEffort) ? ConversationEffort : "";
        await SaveConversationModelAsync(model.Id, effort);
    }
    private async void ChatEffort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices) return;
        if (_modelPreferenceSaving || _operation is not null || _queueDispatching || _followUpsClosing) { SyncModelChoices(); return; }
        if (EffortPicker.SelectedItem is not AiEffort effort) return;
        var model = Settings.AvailableModels.FirstOrDefault(item => item.Id == ConversationModelId);
        if (model is null || !AiEffort.Choices(model).Any(item => item.Value == effort.Value)) { SyncModelChoices(); return; }
        await SaveConversationModelAsync(model.Id, effort.Value);
    }
    private async Task SaveConversationModelAsync(string modelId, string effort)
    {
        if (_project is null || _disposed) { SyncModelChoices(); return; }
        var conversation = _book.Active;
        var previous = (conversation.SelectedModelId, conversation.SelectedReasoningEffort, conversation.ModelPreferenceExplicit);
        _modelPreferenceSaving = true; _modelPreferenceIdle = new(TaskCreationOptions.RunContinuationsAsynchronously); ModelControls.IsEnabled = false; SubmitButton.IsEnabled = false;
        void Apply() { conversation.SelectedModelId = modelId; conversation.SelectedReasoningEffort = effort; conversation.ModelPreferenceExplicit = true; }
        void Restore() { conversation.SelectedModelId = previous.SelectedModelId; conversation.SelectedReasoningEffort = previous.SelectedReasoningEffort; conversation.ModelPreferenceExplicit = previous.ModelPreferenceExplicit; }
        try
        {
            _book.Draft = Composer.Text;
            if (_store is not null) await _store.SaveWithMutationAsync(Apply, Restore);
            else { Apply(); try { await SaveAsync(); } catch { Restore(); throw; } }
            if (!_disposed) SyncModelChoices();
        }
        catch (Exception ex) { if (!_disposed) { SyncModelChoices(); ShowError(ex); } }
        finally
        {
            _modelPreferenceSaving = false;
            if (!_disposed) { ModelControls.IsEnabled = _operation is null; SubmitButton.IsEnabled = _operation is null; }
            var idle = _modelPreferenceIdle; _modelPreferenceIdle = null; idle?.TrySetResult();
            if (!_followUpsClosing && !_disposed && _operation is null) _ = DispatchNextQueuedAsync();
        }
    }
    private void SyncPermission()
    {
        _syncingChoices = true;
        try { PermissionPicker.SelectedItem = AiPermissionChoice.Choices.FirstOrDefault(c => c.Value == Permission) ?? AiPermissionChoice.Choices[^1]; }
        finally { _syncingChoices = false; }
        PermissionPicker.ToolTip = (PermissionPicker.SelectedItem as AiPermissionChoice)?.Description;
        ResizeChoices();
    }
    private void Permission_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices || _operation is not null || PermissionPicker.SelectedItem is not AiPermissionChoice choice) return;
        _book.Active.Permission = choice.Value;
        PermissionPicker.ToolTip = choice.Description;
        ResizeChoices();
        Dirty();
    }
    private void UpdateContextUsage()
    {
        var model = Settings.AvailableModels.FirstOrDefault(item => item.Id == ConversationModelId);
        var usage = _book.Active.ContextUsage;
        if (usage is not null && usage.Model == model?.Id)
        { ContextUsageText.Text = usage.Display; ContextUsageText.ToolTip = usage.Description; }
        else
        {
            ContextUsageText.Text = model?.ContextWindow is > 0 ? "0.0%" : "—";
            ContextUsageText.ToolTip = model?.ContextWindow is > 0 ? "尚无本模型请求用量；发送后按服务端返回的实际 token 数更新。" : "模型目录尚未提供上下文窗口大小；有实际用量和窗口大小后显示百分比。";
        }
    }
    private void ResizeChoices()
    {
        double Width(ComboBox box, double minimum, double maximum)
        {
            var text = new System.Windows.Media.FormattedText(box.SelectedItem?.ToString() ?? "选择模型", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new System.Windows.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize,
                System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return Math.Clamp(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 16, minimum, maximum);
        }
        ModelPicker.Width = Width(ModelPicker, 94, ActualWidth > 0 && ActualWidth < 420 ? 134 : 190);
        EffortPicker.Width = Width(EffortPicker, 48, 112);
        PermissionPicker.Width = Width(PermissionPicker, 65, 122);
        var compact = ActualWidth > 0 && ActualWidth < PermissionPicker.Width + ModelPicker.Width + EffortPicker.Width + 175;
        Grid.SetRow(ModelControls, compact ? 1 : 0); Grid.SetColumn(ModelControls, compact ? 0 : 3); Grid.SetColumnSpan(ModelControls, compact ? 4 : 1);
        ModelControls.HorizontalAlignment = HorizontalAlignment.Right;
        ModelControls.Margin = new Thickness(0, compact ? 2 : 0, 0, 0);
        Grid.SetRow(SendControls, compact ? 1 : 0);
    }
}
