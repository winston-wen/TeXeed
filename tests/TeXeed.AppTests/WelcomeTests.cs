using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckWelcomeAsync(MainWindow window, string output)
    {
        var checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
        T Named<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Settle()
        {
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var root = Path.Combine(output, "recent-projects"); Directory.CreateDirectory(root);
        var paths = Enumerable.Range(1, 10).Select(i => Path.Combine(root, $"项目_{i:00}__中文", "main.tex")).ToArray();
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "\\documentclass{article}\n\\begin{document}Hello.\\end{document}\n");
        }
        var originalFiles = paths.ToDictionary(path => path, File.ReadAllText);
        settingsField.SetValue(window, ((AppSettings)settingsField.GetValue(window)!) with { RecentProjects = paths });
        Invoke(window, "UpdateRecent"); await Settle();
        Check(Named<StackPanel>("RecentList").Children.Count == 10, "Welcome page discarded older recent entries");
        Check(Named<Grid>("WelcomeProjects").IsVisible && Named<Border>("WelcomeTutorial").IsVisible && !Named<Border>("WelcomePager").IsVisible, "Wide welcome page must show both columns");
        var rows = Named<StackPanel>("RecentList");
        Check(Descendants<TextBlock>(rows).Select(t => t.Text).SequenceEqual(paths), "Recent paths lost literal characters or directory names");
        var selections = rows.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<CheckBox>()).ToArray();
        selections[1].IsChecked = true; selections[7].IsChecked = true;
        Check(Named<Button>("RecentDeleteButton").IsEnabled && Named<TextBlock>("RecentSelectionSummary").Text == "已选 2 项", "Selection count did not update");

        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme);
            window.Width = 1440; window.Height = 900; await Settle();
            Capture(window, Path.Combine(output, $"welcome-wide-{theme}.png"));
            window.Width = 700; window.Height = 660; await Settle();
            Check(Named<Grid>("WelcomeProjects").IsVisible && !Named<Border>("WelcomeTutorial").IsVisible && Named<Border>("WelcomePager").IsVisible, "Narrow welcome must start on recent projects");
            Check(Named<ScrollViewer>("RecentScroll").ScrollableHeight > 0, "Recent list is not scrollable");
            Named<ScrollViewer>("RecentScroll").ScrollToVerticalOffset(90); await Settle();
            var offset = Named<ScrollViewer>("RecentScroll").VerticalOffset;
            Capture(window, Path.Combine(output, $"welcome-narrow-recent-{theme}.png"));
            Click("WelcomeNextButton"); await Settle();
            Check(!Named<Grid>("WelcomeProjects").IsVisible && Named<Border>("WelcomeTutorial").IsVisible && Named<TextBlock>("WelcomePageNumber").Text == "2 / 2", "Next button did not show tutorial");
            Named<ScrollViewer>("TutorialScroll").ScrollToVerticalOffset(140); await Settle();
            var tutorialOffset = Named<ScrollViewer>("TutorialScroll").VerticalOffset;
            Capture(window, Path.Combine(output, $"welcome-narrow-tutorial-{theme}.png"));
            Click("WelcomePreviousButton"); await Settle();
            Check(Math.Abs(Named<ScrollViewer>("RecentScroll").VerticalOffset - offset) < 1 && selections.Count(s => s.IsChecked == true) == 2, "Page navigation lost recent scroll or selection");
            Click("WelcomeNextButton"); await Settle();
            Check(Math.Abs(Named<ScrollViewer>("TutorialScroll").VerticalOffset - tutorialOffset) < 1, "Tutorial reading position was reset");
            window.Width = 1440; window.Height = 900; await Settle();
            Check(Named<Grid>("WelcomeProjects").IsVisible && Named<Border>("WelcomeTutorial").IsVisible, "Enlarging window failed to restore both pages");
            window.Width = 280; window.Height = 480; await Settle();
            Check(Named<Border>("WelcomeTutorial").IsVisible && !Named<Grid>("WelcomeProjects").IsVisible, "Resize forgot active compact page");
            Capture(window, Path.Combine(output, $"welcome-minimum-tutorial-{theme}.png"));
            Click("WelcomePreviousButton"); await Settle();
            var pager = Named<Border>("WelcomePager");
            foreach (var name in new[] { "WelcomePreviousButton", "WelcomeNextButton", "RecentDeleteButton", "WelcomeOpenButton", "WelcomeNewButton" })
            {
                var button = Named<Button>(name);
                var rect = new Rect(button.TranslatePoint(new Point(), Named<Grid>("Welcome")), button.RenderSize);
                Check(rect.Left >= 0 && rect.Right <= Named<Grid>("Welcome").ActualWidth + 1 && rect.Bottom <= Named<Grid>("Welcome").ActualHeight + 1, name + " is outside the minimum window");
            }
            Capture(window, Path.Combine(output, $"welcome-minimum-recent-{theme}.png"));
            window.Height = 320; await Settle();
            var recentScroll = Named<ScrollViewer>("RecentScroll");
            Check(recentScroll.ActualHeight >= 40 && recentScroll.TranslatePoint(new Point(0, recentScroll.ActualHeight), Named<Grid>("Welcome")).Y <= pager.TranslatePoint(new Point(), Named<Grid>("Welcome")).Y, "Minimum-height recent list is squeezed out or overlaps pagination");
            Capture(window, Path.Combine(output, $"welcome-minimum-height-{theme}.png"));
            window.Width = 1200; window.Height = 480; await Settle();
            Check(pager.IsVisible && !Named<Border>("WelcomeTutorial").IsVisible, "Short windows must also use the pager");
            window.Width = 700; window.Height = 660; await Settle();
        }
        Click("RecentDeleteButton"); await Settle();
        var remaining = paths.Where((_, index) => index != 1 && index != 7).ToArray();
        Check(((AppSettings)settingsField.GetValue(window)!).RecentProjects.SequenceEqual(remaining), "Delete removed the wrong recent entries");
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json")))!;
        Check(saved.RecentProjects.SequenceEqual(remaining), "Recent removal did not persist");
        Check(Named<MenuItem>("RecentMenu").Items.Cast<MenuItem>().Select(i => (string)i.Header).SequenceEqual(remaining), "Recent menu did not follow deletions");
        Check(paths.All(path => File.ReadAllText(path) == originalFiles[path]), "Removing recent entries changed files");
        Click("RecentSelectAllButton"); Check(Named<TextBlock>("RecentSelectionSummary").Text == "已选 8 项", "Select all missed entries");
        Click("RecentCancelButton"); Check(!Named<Button>("RecentDeleteButton").IsEnabled, "Cancel did not clear selection");
        Click("RecentSelectAllButton"); Click("RecentDeleteButton"); await Settle();
        Check(Named<TextBlock>("NoRecent").IsVisible && !Named<Button>("RecentSelectAllButton").IsEnabled && !Named<MenuItem>("RecentMenu").IsEnabled, "Empty state is incorrect");
        var reopened = new MainWindow { Width = 700, Height = 660 }; reopened.Show(); await Settle();
        Check(((AppSettings)Field(reopened, "_settings")!).RecentProjects.Length == 0, "Reopened app restored removed records");
        await CloseAndObserveAsync(reopened, "Welcome removal persistence");
        settingsField.SetValue(window, ((AppSettings)settingsField.GetValue(window)!) with { RecentProjects = [paths[0]] });
        Invoke(window, "UpdateRecent");
        Descendants<Button>(rows).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 200 && Named<Grid>("Welcome").IsVisible; i++) await Task.Delay(20);
        Check(!Named<Grid>("Welcome").IsVisible && ((TexProject?)Field(window, "_project"))?.MainPath == paths[0], "Recent path click did not open the project");
        Console.WriteLine($"PASS: welcome page ({checks} checks): both themes, responsive columns/pager, selection, scroll retention, persisted removal, unchanged files, empty state and project opening.");
    }
}
