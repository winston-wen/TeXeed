using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckLiteralTextAsync(MainWindow window, string output)
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        var root = Path.Combine(output, "project__one_ & 中文 [x]"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a__b_c_ & 中文 [x] #%.tex");
        File.WriteAllText(file, "\\documentclass{article}\n\\begin{document}\nLiteral filename.\n\\end{document}\n");
        await CallAsync(window, "OpenAsync", file);
        var settings = (AppSettings)Field(window, "_settings")!;
        var paths = new[] { file, @"C:\项目_甲\a__b\尾部_\main_file.tex", @"C:\_leading\a&b [x] #%.tex", @"C:\组合 é\emoji😀_双__下划线.tex" };
        typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, settings with { RecentProjects = paths });
        Invoke(window, "UpdateRecent");
        var menu = (Menu)window.FindName("MainMenu"); var recent = (MenuItem)window.FindName("RecentMenu");
        Check(recent.Items.Cast<MenuItem>().Select(i => (string)i.Header).SequenceEqual(paths), "Recent paths were mutated before display");
        foreach (var item in recent.Items.Cast<MenuItem>()) Check(Equals(item.ToolTip, item.Header), "Recent path tooltip is not the full literal path");
        var project = (TexProject)Field(window, "_project")!;
        Invoke(window, "BuildFileTree", project.Files); var tree = (TreeView)window.FindName("FileTree"); tree.Visibility = Visibility.Visible;
        var folders = tree.Items.OfType<TreeViewItem>().SelectMany(LiteralTreeItems).ToArray();
        Check(folders.All(i => i.HeaderTemplate is not null), "File tree title uses implicit mnemonic rendering");
        var leaf = folders.Single(i => Equals(i.Tag, file));
        Check(((string)leaf.Header).Contains(Path.GetFileName(file)), "File tree lost literal filename");
        await Settle();
        Check(Descendants<TextBlock>(leaf).Any(t => t.Text == (string)leaf.Header), "File tree display changed literal filename");
        var tabs = Descendants<TextBlock>((EditorTabStrip)window.FindName("FileTabs")).Concat(Descendants<TextBlock>((EditorTabStrip)window.FindName("RightFileTabs")));
        Check(tabs.Any(t => t.Text == Path.GetFileName(file)), "Document tab changed filename characters: " + string.Join(" | ", tabs.Select(t => t.Text)));

        var themeFile = Path.Combine(ThemeManager.DirectoryPath, "literal-text-test.json");
        var themeName = "主题_甲__乙_ & [x]";
        File.WriteAllText(themeFile, JsonSerializer.Serialize(ThemeManager.Current with { Id = "literal-text-test", Name = themeName }));
        try
        {
            Invoke(window, "RefreshThemeMenu"); var themes = (MenuItem)window.FindName("ThemeMenu");
            var themeItem = themes.Items.OfType<MenuItem>().Single(i => Equals(i.Header, themeName));
            Check(!LiteralText.GetRecognizesAccessKey(themeItem) && LiteralText.MenuLabel(themeItem) == themeName, "Dynamic theme name was parsed as a mnemonic");
            foreach (var id in new[] { "ristretto", "github-light-default" })
            {
                ThemeManager.Apply(id); window.Width = 1440; await Settle();
                var fileMenu = menu.Items.OfType<MenuItem>().First(); fileMenu.IsSubmenuOpen = true; recent.IsSubmenuOpen = true; await Settle();
                foreach (var item in recent.Items.Cast<MenuItem>())
                {
                    item.ApplyTemplate();
                    Check(Descendants<ContentPresenter>(item).Where(p => p.ContentSource == "Header").All(p => !p.RecognizesAccessKey), "Recent item still enables access-key parsing");
                    Check(Descendants<TextBlock>(item).Any(t => t.Text == (string)item.Header), "Native menu text differs from original path");
                }
                var popup = (Popup)recent.Template.FindName("PART_Popup", recent);
                SaveLiteralVisual((FrameworkElement)popup.Child, Path.Combine(output, "literal-recent-" + id + ".png"));
                recent.IsSubmenuOpen = false; fileMenu.IsSubmenuOpen = false;
                var viewMenu = menu.Items.OfType<MenuItem>().Single(i => i.Items.Contains(themes));
                viewMenu.IsSubmenuOpen = true; themes.IsSubmenuOpen = true; await Settle();
                Check(Descendants<TextBlock>(themes.Items.OfType<MenuItem>().Single(i => Equals(i.Header, themeName))).Any(t => t.Text == themeName), "Wide theme menu changed literal name");
                themes.IsSubmenuOpen = false; viewMenu.IsSubmenuOpen = false;
                var compact = new CompactMenuPanel(menu.Items.OfType<MenuItem>().ToArray(), (Style)window.FindResource("CompactMenuRow"));
                var host = new Window { Owner = window, Width = 920, Height = 360, Content = compact, Title = "原文路径 · 紧凑菜单" }; host.Show(); compact.NavigateTo(recent); host.UpdateLayout(); await Settle();
                foreach (var path in paths) Check(Descendants<TextBlock>(compact).Any(t => t.Text == path), "Compact menu removed literal characters");
                Check(Descendants<Button>(compact).Where(b => b.Tag is MenuItem).All(b => System.Windows.Automation.AutomationProperties.GetName(b) == (string)((MenuItem)b.Tag).Header), "Compact menu accessibility name differs from path");
                Capture(host, Path.Combine(output, "literal-compact-" + id + ".png"));
                var group = new MenuItem { Header = "路径__分组_ & [x]" }; group.Items.Add(new MenuItem { Header = paths[0] }); compact.NavigateTo(group); host.UpdateLayout();
                Check(Descendants<TextBlock>(compact).Any(t => t.Text == "‹  " + (string)group.Header), "Compact breadcrumb removed literal underscores");
                host.Close();
            }
            foreach (var command in menu.Items.OfType<MenuItem>())
            {
                Check(LiteralText.GetRecognizesAccessKey(command), "Top-level command mnemonic was disabled");
                Check(!LiteralText.MenuLabel(command).Contains('_'), "Command caption kept the mnemonic marker");
            }
            Check(ReferenceEquals(Invoke(window, "FindMenuAccessKey", System.Windows.Input.Key.F), menu.Items[0]), "Alt+F no longer selects File");
            var escaped = new MenuItem { Header = "保存__副本(_S)_尾" }; LiteralText.SetRecognizesAccessKey(escaped, true);
            Check(LiteralText.MenuLabel(escaped) == "保存_副本(S)_尾", "Explicit mnemonic decoding removed extra literal underscores");
            var ai = (AiPanel)Field(window, "_aiPanel")!;
            var book = (TeXeed.App.AI.AiNotebook)Field(ai, "_book")!;
            const string annotation = "[批注] C:\\a_b\\main__file.tex";
            book.Turns.Add(new TeXeed.App.AI.AiTurn("[主题] a_b", string.Join("\n\n", Enumerable.Repeat("正文用于检查滚动提示。", 60)))
                { SentAnnotations = [new() { Question = annotation }], Prompt = "[主题] a_b" });
            Invoke(window, "OpenAiTab"); Invoke(ai, "RefreshTranscript"); await Settle();
            ((ScrollViewer)ai.FindName("MessagesScroll")).ScrollToEnd(); await Settle();
            var context = (TextBlock)ai.FindName("ReadingContextAnnotations");
            Check(context.IsVisible && context.Text == "1. " + annotation, "Plain reading hint showed Markdown escape characters");
        }
        finally { File.Delete(themeFile); ThemeManager.Apply("ristretto"); }
        Console.WriteLine($"Literal UI text: {count} checks passed (wide/compact menus, breadcrumbs, tooltips, automation, filenames, themes, both palettes and Alt mnemonics).");
    }
    private static IEnumerable<TreeViewItem> LiteralTreeItems(TreeViewItem item)
    { yield return item; foreach (var child in item.Items.OfType<TreeViewItem>()) foreach (var descendant in LiteralTreeItems(child)) yield return descendant; }
    private static void SaveLiteralVisual(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output);
    }
}
