using System.Windows;

namespace TeXeed.App;

public partial class MainWindow
{
    private void ApplyWordWrap()
    {
        foreach (var sourceEditor in Editors) sourceEditor.SetWordWrap(_settings.WordWrap);
        WordWrapMenuItem.IsChecked = _settings.WordWrap;
    }
    private void WordWrap_Click(object sender, RoutedEventArgs e) => ToggleWordWrap();
    private void ToggleWordWrap()
    {
        if (_closing || _allowClose || _transitioning) return;
        _settings = _settings with { WordWrap = !_settings.WordWrap };
        ApplyWordWrap();
        PersistSettings();
    }
}
