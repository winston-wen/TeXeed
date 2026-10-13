using System.IO;
using System.Windows;
using TeXeed.App.Controls;
using TeXeed.App.Services;

internal static partial class Program
{
    private static void PhysicalDimensions()
    {
        var edid = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edid, 0);
        edid[21] = 60; edid[22] = 34;
        void Checksum() { edid[127] = 0; edid[127] = unchecked((byte)-edid.Sum(value => value)); }
        Checksum();
        Require(PhysicalScreen.ParseEdid(edid) == new Size(600, 340), "Basic EDID dimensions are not centimeters");
        edid[54] = 1; edid[66] = 86; edid[67] = 80; edid[68] = 0x21; // 598 x 336 mm
        Checksum();
        Require(PhysicalScreen.ParseEdid(edid) == new Size(598, 336), "Detailed EDID dimensions must preserve millimeter precision");
        edid[127]++;
        Require(PhysicalScreen.ParseEdid(edid) is null && PhysicalScreen.ParseEdid(edid.AsSpan(0, 64)) is null,
            "Corrupt/truncated EDID was treated as a physical measurement");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2, 3 })
        {
            var measured = PhysicalScreen.FromDimensions(3840, 60, scale);
            Near(measured.DipsPerCentimeter * scale * 30, 1920, "30 cm must occupy the same physical pixels at every UI scale");
            Require(!measured.IsEstimated, "A valid physical measurement was marked estimated");
        }
        Require(PhysicalScreen.FromDimensions(1920, 0, 1).IsEstimated, "Missing panel dimensions need an explicit fallback");
        Near(PhysicalScreen.FromDimensions(2160, 34, 2).DipsPerCentimeter, 2160d / 34 / 2, "Portrait width uses the rotated panel dimension");
    }

    private static async Task PhysicalWidthCap()
    {
        var path = SaveFixture(3);
        using var preview = new PdfPreview(); var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            foreach (var scale in new[] { 1d, 2 })
            {
                preview.SetScreenMeasurement(PhysicalScreen.FromDimensions(3840, 60, scale));
                preview.AutoWidthMaximumCm = 5; preview.FitWidth(); window.UpdateLayout();
                Near(Layout(preview, 0).Size.Width * scale, 320, "5 cm PDF width in physical pixels");
                var limitedZoom = preview.Zoom;
                preview.SetZoom(2); window.UpdateLayout();
                Require(!preview.IsAutoWidth && preview.Zoom > limitedZoom, "Physical cap must apply only to automatic width");
                preview.FitWidth(); window.UpdateLayout();
                Near(Layout(preview, 0).Size.Width * scale, 320, "Re-entering auto width must restore the cap");
                preview.AutoWidthMaximumCm = 2.5; window.UpdateLayout();
                Near(Layout(preview, 0).Size.Width * scale, 160, "Editing physical cap must immediately resize the preview");
            }
            preview.AutoWidthMaximumCm = 200; preview.FitWidth(); window.UpdateLayout();
            Require(Layout(preview, 0).Size.Width <= preview.ActualWidth - 41, "Auto width must respect narrower panes as well as the physical cap");
        }
        finally { window.Close(); File.Delete(path); }
    }
}
