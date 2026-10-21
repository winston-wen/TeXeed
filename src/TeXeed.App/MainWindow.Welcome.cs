using System.IO;
using System.Windows;
using System.Windows.Controls;
using TeXeed.App.Services;

namespace TeXeed.App;

public partial class MainWindow
{
    private readonly HashSet<string> _selectedRecent = new(StringComparer.OrdinalIgnoreCase);
    private bool _welcomeReady, _welcomeTutorialSelected;

    private void InitializeWelcome()
    {
        _welcomeReady = true;
        UpdateWelcomeLayout();
    }

    private void Welcome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_welcomeReady) UpdateWelcomeLayout();
    }

    private void UpdateWelcomeLayout()
    {
        var compact = Welcome.ActualWidth < 1040 || Welcome.ActualHeight < 560;
        var shortWindow = Welcome.ActualHeight < 420;
        WelcomePages.Margin = shortWindow ? new Thickness(12, 10, 12, 8)
            : compact ? new Thickness(18, 18, 18, 16) : new Thickness(48, 42, 48, 32);
        WelcomeOpenButton.Padding = WelcomeNewButton.Padding = shortWindow ? new Thickness(12, 7, 12, 7) : new Thickness(18, 11, 18, 11);
        WelcomeDropHint.Margin = new Thickness(0, 2, 0, shortWindow ? 10 : 24);
        RecentHeading.Margin = new Thickness(0, 0, 0, shortWindow ? 6 : 12);
        RecentActions.Margin = new Thickness(0, shortWindow ? 8 : 16, 0, 0);
        RecentRemovalHint.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        WelcomeGap.Width = new GridLength(compact ? 0 : 56);
        WelcomeTutorialColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(WelcomeTutorial, compact ? 0 : 2);
        WelcomeTutorial.Padding = new Thickness(compact ? 18 : 28);
        WelcomeProjects.Visibility = !compact || !_welcomeTutorialSelected ? Visibility.Visible : Visibility.Collapsed;
        WelcomeTutorial.Visibility = !compact || _welcomeTutorialSelected ? Visibility.Visible : Visibility.Collapsed;
        WelcomePager.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        WelcomePreviousButton.IsEnabled = _welcomeTutorialSelected;
        WelcomeNextButton.IsEnabled = !_welcomeTutorialSelected;
        WelcomePageNumber.Text = _welcomeTutorialSelected ? "2 / 2" : "1 / 2";
        // At the minimum window width the page names already identify the two pages.
        WelcomePageNumber.Visibility = Welcome.ActualWidth < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void WelcomePrevious_Click(object sender, RoutedEventArgs e)
    {
        _welcomeTutorialSelected = false;
        UpdateWelcomeLayout();
        WelcomeNextButton.Focus();
    }

    private void WelcomeNext_Click(object sender, RoutedEventArgs e)
    {
        _welcomeTutorialSelected = true;
        UpdateWelcomeLayout();
        WelcomePreviousButton.Focus();
    }

    private void UpdateRecent()
    {
        _selectedRecent.IntersectWith(_settings.RecentProjects);
        RecentMenu.Items.Clear();
        RecentList.Children.Clear();
        foreach (var path in _settings.RecentProjects)
        {
            var item = new MenuItem { Header = path, ToolTip = path };
            item.Click += async (_, _) => await OpenAsync(path);
            RecentMenu.Items.Add(item);

            var row = new Grid();
            // One shared rule per boundary, continuing underneath the checkbox.
            var rule = new Border { BorderThickness = new Thickness(0, RecentList.Children.Count == 0 ? 1 : 0, 0, 1), IsHitTestVisible = false };
            rule.SetResourceReference(Border.BorderBrushProperty, "Line");
            Grid.SetColumnSpan(rule, 2);
            row.Children.Add(rule);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var selection = new CheckBox
            {
                Tag = path, IsChecked = _selectedRecent.Contains(path), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0), ToolTip = "选择记录：" + path
            };
            System.Windows.Automation.AutomationProperties.SetName(selection, "选择记录：" + path);
            selection.Checked += RecentSelection_Changed;
            selection.Unchecked += RecentSelection_Changed;
            row.Children.Add(selection);
            var label = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            var button = new Button
            {
                Content = label, Tag = path, ToolTip = path,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0), Padding = new Thickness(8, 10, 8, 10), BorderThickness = new Thickness(0)
            };
            // The shared button template centers its content; stretch this path label
            // explicitly so long paths are constrained and ellipsized inside the row.
            button.SetResourceReference(StyleProperty, "WelcomePathButton");
            System.Windows.Automation.AutomationProperties.SetName(button, "打开项目：" + path);
            button.Click += async (_, _) => await OpenAsync(path);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            RecentList.Children.Add(row);
        }
        NoRecent.Visibility = RecentList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
        UpdateRecentSelection();
    }

    private void RecentSelection_Changed(object sender, RoutedEventArgs e)
    {
        var selection = (CheckBox)sender;
        var path = (string)selection.Tag;
        if (selection.IsChecked == true) _selectedRecent.Add(path);
        else _selectedRecent.Remove(path);
        UpdateRecentSelection();
    }

    private void UpdateRecentSelection()
    {
        var count = _selectedRecent.Count;
        RecentSelectionSummary.Text = count == 0 ? $"{_settings.RecentProjects.Length} 个项目" : $"已选 {count} 项";
        RecentSelectAllButton.IsEnabled = _settings.RecentProjects.Length > count;
        RecentCancelButton.IsEnabled = RecentDeleteButton.IsEnabled = count > 0;
    }

    private void RecentSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var selection in RecentList.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<CheckBox>()))
            selection.IsChecked = true;
    }

    private void RecentCancel_Click(object sender, RoutedEventArgs e)
    {
        foreach (var selection in RecentList.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<CheckBox>()))
            selection.IsChecked = false;
    }

    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecent.Count == 0) return;
        var next = _settings with { RecentProjects = _settings.RecentProjects.Where(path => !_selectedRecent.Contains(path)).ToArray() };
        try
        {
            SettingsStore.Save(next);
            _settings = next;
            UpdateRecent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex); }
    }
}
