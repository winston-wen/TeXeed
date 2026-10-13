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
}
