using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPdfCropAsync(MainWindow window, string root, string output)
    {
        var fixture = Path.Combine(output, "crop-project"); Directory.CreateDirectory(fixture);
        var main = Path.Combine(fixture, "main.tex");
        var source = """
            \documentclass{article}
            \pagestyle{plain}
            \begin{document}
            \section{A comfortable reading view}
            The editor and preview share a softer warm palette.\par
            Ordinary paragraph spacing stays intact.\par
            \vspace{140pt}
            \subsection{Content after a large blank}
            Lower content remains clickable after whitespace compression.\par
            \rule{180pt}{24pt}\par
            A figure and its caption stay together.\par
            \newpage
            \section{Continuous reading}
            Page two follows page one without large paper margins.\par
            \vspace{110pt}
            Additional content retains its source mapping after cropping.\par
            \end{document}
            """;
        File.WriteAllText(main, source);
        var field = window.GetType().GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = (AppSettings)field.GetValue(window)!;
        var settings = previous with { LivePreview = false, AutoCropPdf = true, FollowMode = PreviewFollowMode.None,
            Engine = TexEngine.Tectonic, EnginePath = Path.Combine(root, ".tools", "tectonic", "tectonic.exe"),
            SynctexPath = Path.Combine(fixture, "no-cli.exe") };
        field.SetValue(window, settings);
        Environment.SetEnvironmentVariable("TECTONIC_CACHE_DIR", Path.Combine(root, ".tools", "tectonic", "cache"));
        var preview = (PdfPreview)window.FindName("Preview");
        var editor = (LatexEditor)window.FindName("Editor");
        var toggle = (CheckBox)window.FindName("AutoCropPdfBox");
        ((ComboBox)window.FindName("FollowModeBox")).SelectedValue = PreviewFollowMode.None;
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        try
        {
            await CallAsync(window, "OpenAsync", main); await CallAsync(window, "BuildAsync");
            await WaitForPreviewRenderedAsync(window);
            var pdf = (string?)Field(window, "_pdfPath");
            if (pdf is null || preview.PageCount != 2) throw new Exception("Crop fixture did not compile: " + ((TextBox)window.FindName("LogText")).Text);
            if (editor.Foreground is not SolidColorBrush { Color: var color } || color != Color.FromRgb(216, 204, 200))
                throw new Exception("Editor body text did not adopt soft warm white");
            var original = File.ReadAllBytes(pdf);
            var layout = ((System.Collections.IEnumerable)Field(preview, "_layouts")!).Cast<object>().First();
            var crop = layout.GetType().GetProperty("Crop")!.GetValue(layout) ?? throw new Exception("Automatic crop is not active");
            var bands = ((System.Collections.IEnumerable)crop.GetType().GetProperty("Bands")!.GetValue(crop)!).Cast<object>().ToArray();
            if (bands.Length < 3 || (double)crop.GetType().GetProperty("Height")!.GetValue(crop)! > 380)
                throw new Exception("Large body/footer whitespace was not removed from the real PDF");
            Capture(window, Path.Combine(output, "pdf-auto-crop.png"));
            var scroll = (ScrollViewer)Field(preview, "_scroll")!;
            if (scroll.ScrollableWidth > 1) throw new Exception("Automatic fill introduced horizontal scrolling");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await WaitForPreviewRenderedAsync(window); Capture(window, Path.Combine(output, "pdf-full-page.png"));
            if (scroll.ScrollableWidth > 1) throw new Exception("Restoring a full page retained stale horizontal anchor padding");
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await WaitForPreviewRenderedAsync(window);
            var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json")))!;
            if (!persisted.AutoCropPdf || persisted.EditorFontFamily != previous.EditorFontFamily) throw new Exception("Crop setting was not saved or changed the font preference");
            var line = Array.FindIndex(source.Split('\n'), text => text.StartsWith("Lower content", StringComparison.Ordinal)) + 1;
            editor.NavigateTo(new SourceLocation(main, line));
            await CallAsync(window, "ForwardAsync", true);
            var point = await new SyncTexService().ForwardAsync((TexProject)Field(window, "_project")!, pdf, new SourceLocation(main, line), settings)
                ?? throw new Exception("Missing real SyncTeX position");
            var mapped = (Point)Invoke(preview, "PageToDisplay", point.Page - 1, new Point(point.X, point.Y))!;
            var originalPoint = (Point)Invoke(preview, "DisplayToPage", point.Page - 1, mapped)!;
            if ((originalPoint - new Point(point.X, point.Y)).Length > .1) throw new Exception("Cropped SyncTeX round-trip moved the target");
            editor.NavigateTo(new SourceLocation(main, 1));
            await CallAsync(window, "InverseAsync", point with { X = originalPoint.X, Y = originalPoint.Y });
            if (Math.Abs(editor.CurrentLocation!.Line - line) > 1) throw new Exception("Cropped PDF inverse navigation reached the wrong source line");
            if (!File.ReadAllBytes(pdf).SequenceEqual(original)) throw new Exception("Cropping changed the PDF that is exported");
            await CheckPreviewModeToolbarAsync(window, output);
            Console.WriteLine("PASS: soft editor text, real PDF margin/internal-gap cropping, continuous width fill, saved crop setting and cropped SyncTeX round-trip");
        }
        finally { field.SetValue(window, previous); }
    }
}
