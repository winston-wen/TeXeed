using System.Windows;
using System.Windows.Controls;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private bool _syncingChoices;
    private readonly CancellationTokenSource _lifetime = new();

    private void SyncModelChoices()
    {
        _syncingChoices = true;
        try
        {
            ModelPicker.ItemsSource = Settings.AvailableModels;
            ModelPicker.SelectedItem = Settings.SelectedModel;
            var efforts = AiEffort.Choices(Settings.SelectedModel);
            EffortPicker.ItemsSource = efforts;
            EffortPicker.SelectedItem = efforts.FirstOrDefault(e => e.Value == Settings.SelectedEffort) ?? efforts[0];
        }
        finally { _syncingChoices = false; }
        UpdateConnectionLabel();
        EffortPicker.ToolTip = Settings.SelectedEffort.Length > 0 ? "本次请求明确使用：" + AiEffort.Label(Settings.SelectedEffort) :
            "不发送 reasoning.effort，由服务端决定。" + (Settings.SelectedModel?.DefaultReasoningEffort is { Length: > 0 } value ? "模型目录默认值：" + AiEffort.Label(value) : "模型目录未报告默认强度。");
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
        finally { if (!_disposed) ModelControls.IsEnabled = _operation is null; }
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelChoicesAsync();
    private async void ChatModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices || ModelPicker.SelectedItem is not AiModel model) return;
        try { await Settings.SelectModelAsync(model); } catch (Exception ex) { ShowError(ex); }
    }
    private async void ChatEffort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingChoices || EffortPicker.SelectedItem is not AiEffort effort) return;
        try { await Settings.SelectEffortAsync(effort.Value); } catch (Exception ex) { ShowError(ex); }
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
        var model = Settings.SelectedModel;
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
            return Math.Clamp(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 34, minimum, maximum);
        }
        ModelPicker.Width = Width(ModelPicker, 94, ActualWidth > 0 && ActualWidth < 420 ? 134 : 190);
        EffortPicker.Width = Width(EffortPicker, 48, 112);
        PermissionPicker.Width = Width(PermissionPicker, 65, 122);
        var compact = ActualWidth > 0 && ActualWidth < PermissionPicker.Width + ModelPicker.Width + EffortPicker.Width + 175;
        Grid.SetRow(ModelControls, compact ? 1 : 0); Grid.SetColumn(ModelControls, compact ? 0 : 3); Grid.SetColumnSpan(ModelControls, compact ? 4 : 1);
        ModelControls.HorizontalAlignment = HorizontalAlignment.Right;
        ModelControls.Margin = new Thickness(0, compact ? 6 : 0, 0, 0);
        Grid.SetRow(SendControls, compact ? 1 : 0);
    }
}
