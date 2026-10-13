using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPreviewSettingsAsync(MainWindow window, string output)
    {
        var preview = (PdfPreview)window.FindName("Preview");
        var pdf = Path.Combine(output, "preview.pdf"); WriteThemePdf(pdf); await preview.LoadAsync(pdf);
        async Task Settle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
        PreviewOptions Options() => (PreviewOptions)typeof(MainWindow).GetProperty("CurrentPreviewOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var original = Options();
        if (original.MaximumWidthCm != 30 || !original.CompressWhitespace) throw new Exception("Fresh preview defaults are not 30 cm and compressed whitespace");
        var dialog = (PreviewSettingsWindow)Invoke(window, "CreatePreviewSettings")!; dialog.Show(); await Settle();
        if (dialog.ActualWidth > 305 || dialog.FindName("ApplyButton") is not null || Descendants<TextBlock>(dialog).Any(t => t.Text == "预览设置"))
            throw new Exception("Preview settings still have a wide layout, duplicated heading or Apply button");
        Capture(dialog, Path.Combine(output, "settings-dark.png"));
        var width = (TextBox)dialog.FindName("MaximumWidthInput");
        width.Text = "12.5";
        if (preview.AutoWidthMaximumCm != 12.5 || ((AppSettings)Field(window, "_settings")!).AutoWidthMaximumCm != 12.5)
            throw new Exception("Changing the centimeter cap did not apply immediately");
        var zoomInput = (TextBox)dialog.FindName("ZoomInput");
        if (zoomInput.Text != (preview.Zoom * 100).ToString("F2", CultureInfo.CurrentCulture) + "%")
            throw new Exception("Automatic zoom input does not display the actual constrained scale");
        ((ComboBox)dialog.FindName("FollowModeBox")).SelectedValue = PreviewFollowMode.None;
        ((CheckBox)dialog.FindName("InvertPdfBox")).IsChecked = false;
        var compress = (CheckBox)dialog.FindName("CompressWhitespaceBox"); compress.IsChecked = false;
        if (preview.InvertColors || preview.CompressVerticalWhitespace || Options().Follow != PreviewFollowMode.None)
            throw new Exception("Preview options require an explicit Apply action");
        var crop = (CheckBox)dialog.FindName("AutoCropPdfBox"); crop.IsChecked = false; await Settle();
        if (compress.IsEnabled || preview.AutoCrop) throw new Exception("Compression control must be disabled without cropping");
        crop.IsChecked = true; await Settle();
        if (!compress.IsEnabled || compress.IsChecked == true) throw new Exception("Compression preference did not survive disabling crop");
        ((RadioButton)dialog.FindName("ManualZoom")).IsChecked = true; zoomInput.Text = "137.62%";
        if (preview.IsAutoWidth || Math.Abs(preview.Zoom - 1.3762) > .00001 || width.IsEnabled)
            throw new Exception("Manual zoom did not apply precisely or disabled width still editable");
        zoomInput.Text = "NaN";
        if (Math.Abs(preview.Zoom - 1.3762) > .00001) throw new Exception("Invalid live zoom was accepted");
        ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (Options() != original) throw new Exception("Cancel did not restore all opening preferences and automatic scale");
        dialog = (PreviewSettingsWindow)Invoke(window, "CreatePreviewSettings")!; dialog.Show(); await Settle();
        ((TextBox)dialog.FindName("MaximumWidthInput")).Text = "0";
        if (preview.AutoWidthMaximumCm != 30) throw new Exception("Invalid centimeter cap was accepted");
        ((TextBox)dialog.FindName("MaximumWidthInput")).Text = "22.25";
        ((CheckBox)dialog.FindName("CompressWhitespaceBox")).IsChecked = false;
        ((RadioButton)dialog.FindName("ManualZoom")).IsChecked = true;
        ((TextBox)dialog.FindName("ZoomInput")).Text = "500%";
        ((CheckBox)dialog.FindName("AutoCropPdfBox")).IsChecked = false;
        if (preview.AutoCrop || preview.Zoom != 3) throw new Exception("Turning off cropping failed to clamp manual zoom to its valid range");
        ((TextBox)dialog.FindName("ZoomInput")).Text = "137.62%";
        ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        if (dialog.IsVisible || preview.Zoom != 1.3762 || preview.AutoWidthMaximumCm != 22.25 || preview.CompressVerticalWhitespace)
            throw new Exception("Confirm failed to retain the live settings");
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("TEXEED_HOME")!, "settings.json"))))
            if (json.RootElement.GetProperty("AutoWidthMaximumCm").GetDouble() != 22.25 || json.RootElement.GetProperty("CompressPdfWhitespace").GetBoolean())
                throw new Exception("Physical width and compression were not persisted");
        var confirmed = Options(); dialog = (PreviewSettingsWindow)Invoke(window, "CreatePreviewSettings")!; dialog.Show(); await Settle();
        ((CheckBox)dialog.FindName("InvertPdfBox")).IsChecked = !confirmed.Invert;
        dialog.Close(); await Settle();
        if (Options() != confirmed) throw new Exception("Caption close did not roll back the live options");
        ThemeManager.Apply("github-light-default");
        dialog = (PreviewSettingsWindow)Invoke(window, "CreatePreviewSettings")!; dialog.Show(); await Settle();
        Capture(dialog, Path.Combine(output, "settings-light.png")); dialog.Close(); ThemeManager.Apply("ristretto");
        Invoke(window, "ApplyPreviewOptions", original); await WaitForPreviewRenderedAsync(window);
        Console.WriteLine("PASS: compact dark/light dialog, live validation/zoom/follow/colors, editable 30 cm cap, independent compression, confirm persistence and cancel/close restoration");
    }
}
