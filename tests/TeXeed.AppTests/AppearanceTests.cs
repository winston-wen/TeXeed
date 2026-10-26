using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckAppearanceAsync(MainWindow window, string output)
    {
        var root = Path.Combine(output, "ristretto-project"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "main.tex");
        File.WriteAllText(path, """
            \documentclass{article}
            \title{密码协议与 \textbf{LaTeX} 排版}
            \begin{document}
            \section{协议中的 $\alpha_i^2 + \beta$}
            \subsection{复杂度 $\frac{n^2}{\log n}$}
            \subsection{域 $\mathbb{R}$ 与 $\sqrt{x+1}$}
            \section{\textbf{加粗}、\emph{强调}与 50\%}
            \subsection{中文标题与 $E=mc^2$，在狭窄侧栏中仍可点击跳转}
            \subsection{未知命令 \custommacro{内容}}
            \end{document}
            """);
        var service = new ProjectService();
        await service.SetNameAsync(await service.LoadAsync(path), "");
        await CallAsync(window, "OpenAsync", path);
        if (window.Title != "ristretto-project — TeXeed") throw new Exception("Window title did not default to the project folder");
        var outline = (DocumentOutline)window.FindName("OutlineTree");
        window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var titles = Descendants<LatexTitleBlock>(outline).ToArray();
        if (titles.Length != 6 || !titles.Any(t => Descendants<OutlineFormula>(t).Any(f => f.IsFormulaRendered))) throw new Exception("Math headings or fraction layout missing");
        var fraction = titles.Single(t => t.Latex.Contains(@"\frac"));
        if (fraction.ActualHeight < 13 || fraction.ActualHeight > 30) throw new Exception("Fraction title has invalid height");
        if (Descendants<TextBlock>(fraction).Any(t => t.Foreground is SolidColorBrush b && b.Color == Colors.Black))
            throw new Exception("Inline formula lost dark-theme foreground inheritance");
        Capture(window, Path.Combine(output, "ristretto-workspace.png"));
        var menu = (Menu)window.FindName("MainMenu"); menu.Visibility = Visibility.Visible;
        var fileMenu = (MenuItem)window.FindName("FirstMenuItem"); fileMenu.IsSubmenuOpen = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var menuPopup = (System.Windows.Controls.Primitives.Popup)fileMenu.Template.FindName("PART_Popup", fileMenu);
        CaptureOutline((FrameworkElement)menuPopup.Child, Path.Combine(output, "ristretto-menu.png"));
        fileMenu.IsSubmenuOpen = false;
        var sidebar = (FrameworkElement)window.FindName("SidebarPanel");
        CaptureOutline(sidebar, Path.Combine(output, "outline-latex.png"));
        var column = (ColumnDefinition)window.FindName("SidebarColumn"); var width = column.Width;
        column.Width = new GridLength(200); window.UpdateLayout();
        if (Descendants<ScrollViewer>(outline).First().ScrollableWidth > 1 || !Descendants<OutlineTitleViewport>(outline).Any(t => t.IsClipped)) throw new Exception("Long LaTeX titles lack bounded clipping");
        CaptureOutline(sidebar, Path.Combine(output, "outline-latex-narrow.png")); column.Width = width;

        var project = await service.LoadAsync(path);
        var dialog = new SettingsWindow(new AppSettings { LivePreview = false }, project) { Owner = window };
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var name = (TextBox)dialog.FindName("ProjectNameBox");
                if (name.Text.Length != 0) throw new Exception("Default project name should leave the override empty");
                ((Button)dialog.FindName("UseTitleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (name.Text != "密码协议与 LaTeX 排版") throw new Exception("Use title did not strip title markup");
                Capture(dialog, Path.Combine(output, "ristretto-settings.png"));
                var combo = (ComboBox)dialog.FindName("EngineBox"); combo.IsDropDownOpen = true;
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                CaptureOutline((FrameworkElement)popup.Child, Path.Combine(output, "ristretto-dropdown.png")); combo.IsDropDownOpen = false;
                Descendants<Button>(dialog).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        if (dialog.ShowDialog() != true || failure is not null) throw new Exception("Project title settings failed", failure);
        if ((await service.LoadAsync(path)).Name != "密码协议与 LaTeX 排版") throw new Exception("Project name was not persisted");
        await CallAsync(window, "OpenAsync", path);
        if (window.Title != "密码协议与 LaTeX 排版 — TeXeed") throw new Exception("Configured title did not reach window caption");
        Console.WriteLine("PASS: Ristretto window/editor/settings/dropdown, rich LaTeX outline with narrow layout, project name settings and reopened caption");
    }
}
