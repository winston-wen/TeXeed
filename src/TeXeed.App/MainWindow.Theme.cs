using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class MainWindow
{
    private void ThemeMenu_Opened(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, ThemeMenu)) RefreshThemeMenu(); }

    private void RefreshThemeMenu()
    {
        ThemeMenu.Items.Clear();
        foreach (var theme in ThemeManager.List())
        {
            var item = new MenuItem { Header = theme.Name, Tag = theme.Id, IsCheckable = true, IsChecked = ThemeManager.Current.Id == theme.Id };
            item.Click += Theme_Click; ThemeMenu.Items.Add(item);
        }
        ThemeMenu.Items.Add(new Separator());
        var reload = new MenuItem { Header = "重新加载当前主题" };
        reload.Click += (_, _) => SelectTheme(ThemeManager.Current.Id, false); ThemeMenu.Items.Add(reload);
        var folder = new MenuItem { Header = "打开主题文件夹" };
        folder.Click += (_, _) =>
        {
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(ThemeManager.DirectoryPath); Process.Start(start);
            }
            catch (Exception ex) { Error(ex); }
        };
        ThemeMenu.Items.Add(folder);
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => SelectTheme((string)((MenuItem)sender).Tag, true);

    private void SelectTheme(string id, bool changePdfMode)
    {
        try
        {
            ThemeManager.Apply(id);
            _settings = _settings with { ThemeId = id, InvertPdfColors = changePdfMode ? ThemeManager.Current.IsDark : _settings.InvertPdfColors };
            Preview.InvertColors = _settings.InvertPdfColors;
            InvertPdfBox.IsChecked = _settings.InvertPdfColors;
            PersistSettings(); RefreshThemeMenu();
            Status("主题：" + ThemeManager.Current.Name);
        }
        catch (Exception ex) { RefreshThemeMenu(); Error(ex); }
    }
}
