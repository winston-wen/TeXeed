using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task PointerZoom()
    {
        var path = SaveFixture(6, varied: true);
        using var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            preview.SetZoom(1.2);
            preview.ShowLocation(new PdfLocation(3, 140, 100), highlight: false, viewportFraction: .5);
            var point = new Point(140, 100);
            var pointer = PagePointOnViewport(preview, 2, point);
            var scroll = (ScrollViewer)Field(preview, "_scroll");
            Require(pointer.X > 30 && pointer.X < scroll.ViewportWidth - 30 && pointer.Y > 30 && pointer.Y < scroll.ViewportHeight - 30,
                "Zoom fixture content point is not inside the viewport.");
            Require(scroll.ScrollableWidth < 1, "Zoom fixture should start horizontally centered without horizontal scrolling.");
            foreach (var zoom in new[] { 1.7, 3, 2.1, .7, .35, .4, 2.4, 3d })
            {
                // Deliberately do not yield between zooms: rapid wheel events must
                // capture the latest layout and scroll offsets, not a previous scale.
                preview.SetZoom(zoom, pointer); window.UpdateLayout();
                SamePoint(PagePointOnViewport(preview, 2, point), pointer, "Repeated pointer zoom at " + zoom);
            }
            Require(scroll.ScrollableWidth > 1, "Zoom fixture did not transition into horizontal scrolling.");
            preview.SetZoom(999, pointer); window.UpdateLayout();
            Near(preview.Zoom, 3, "Maximum zoom clamp");
            SamePoint(PagePointOnViewport(preview, 2, point), pointer, "Repeated maximum zoom");
            preview.SetZoom(.01, pointer); window.UpdateLayout();
            Near(preview.Zoom, .2, "Minimum zoom clamp");
            SamePoint(PagePointOnViewport(preview, 2, point), pointer, "Minimum zoom preserves the pointer anchor");
            var settled = PagePointOnViewport(preview, 2, point);
            preview.SetZoom(double.NaN, pointer);
            preview.SetZoom(2, new Point(double.NaN, 100));
            SamePoint(PagePointOnViewport(preview, 2, point), settled, "Invalid zoom input changed the viewport");
            await Render(preview);
            SamePoint(RenderedPointOnViewport(preview, 2, point), pointer, "Rendered PDF content is not under the retained pointer");
        }
        finally { window.Close(); File.Delete(path); }
    }

    private static async Task RotatedAndGapZoom()
    {
        var path = SaveFixture(4, rotation: 90, crop: true, varied: true);
        using var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            preview.SetZoom(1.1);
            var point = new Point(130, 70);
            preview.ShowLocation(new PdfLocation(2, point.X, point.Y), highlight: false, viewportFraction: .5);
            var pointer = PagePointOnViewport(preview, 1, point);
            foreach (var zoom in new[] { .45, 2.5, .8, 3d })
            {
                preview.SetZoom(zoom, pointer); window.UpdateLayout();
                SamePoint(PagePointOnViewport(preview, 1, point), pointer, "Rotated/cropped page pointer anchor at " + zoom);
            }
            await Render(preview);
            SamePoint(RenderedPointOnViewport(preview, 1, point), pointer, "Rotated PDF raster content moved away from the pointer");

            // The middle of a fixed-size page gap is not a PDF coordinate. Zoom
            // retains the nearest page edge at its previous screen position.
            preview.SetZoom(.8);
            var document = (PdfDocument)Field(preview, "_document");
            var size = Layout(preview, 1).Size;
            var edge = document.ViewToPage(1, new Point(size.Width / 2, size.Height), size);
            preview.ShowLocation(new PdfLocation(2, edge.X, edge.Y), highlight: false, viewportFraction: .5);
            var edgeBefore = PagePointOnViewport(preview, 1, edge);
            var gapPointer = new Point(edgeBefore.X, edgeBefore.Y + 5);
            preview.SetZoom(1.8, gapPointer); window.UpdateLayout();
            SamePoint(PagePointOnViewport(preview, 1, edge), edgeBefore, "Zooming inside a page gap moved its nearest content edge");
        }
        finally { window.Close(); File.Delete(path); }
    }

    private static async Task CenteredNavigation()
    {
        foreach (var count in new[] { 1, 8 })
        {
            var path = SaveFixture(count);
            using var preview = new PdfPreview();
            var window = Host(preview);
            try
            {
                window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
                foreach (var zoom in new[] { .35, 1.2, 3d })
                {
                    preview.SetZoom(zoom);
                    foreach (var location in new[] { new PdfLocation(1, 20, 0, 40, 12), new PdfLocation(count, 20, 288, 40, 12) })
                    {
                        preview.ShowLocation(location, viewportFraction: .5, centerBounds: true); window.UpdateLayout();
                        var point = PagePointOnViewport(preview, location.Page - 1, new Point(location.X + location.Width / 2, location.Y + location.Height / 2));
                        var scroll = (ScrollViewer)Field(preview, "_scroll");
                        Require(Math.Abs(point.Y - scroll.ViewportHeight / 2) < .25,
                            $"Boundary content was clamped away from center (page {location.Page}/{count}, zoom {zoom}, Y {point.Y}, center {scroll.ViewportHeight / 2}).");
                        Require(Equals(Field(preview, "_highlight"), location), "Centering changed the original SyncTeX highlight bounds.");
                    }
                }
                var finalPoint = new Point(20, 300);
                preview.ShowLocation(new PdfLocation(count, finalPoint.X, finalPoint.Y), highlight: false, viewportFraction: .35);
                window.UpdateLayout();
                var finalScroll = (ScrollViewer)Field(preview, "_scroll");
                Require(Math.Abs(PagePointOnViewport(preview, count - 1, finalPoint).Y - finalScroll.ViewportHeight * .35) < .25,
                    "Last-page navigation did not retain its requested manual alignment.");
            }
            finally { window.Close(); File.Delete(path); }
        }

        var rotatedPath = SaveFixture(3, rotation: 90, crop: true);
        using var rotated = new PdfPreview();
        var rotatedWindow = Host(rotated);
        try
        {
            rotatedWindow.Show(); await rotated.LoadAsync(rotatedPath); rotatedWindow.UpdateLayout();
            rotated.SetZoom(.6);
            // At 90 degrees, the visual vertical coordinate depends on the PDF X
            // coordinate. Both dimensions must contribute to the text-box center.
            foreach (var location in new[] { new PdfLocation(1, 10, 80, 12, 20), new PdfLocation(3, 178, 80, 12, 20) })
            {
                rotated.ShowLocation(location, viewportFraction: .5, centerBounds: true); rotatedWindow.UpdateLayout();
                var center = PagePointOnViewport(rotated, location.Page - 1, new Point(location.X + location.Width / 2, location.Y + location.Height / 2));
                var scroll = (ScrollViewer)Field(rotated, "_scroll");
                Require(Math.Abs(center.Y - scroll.ViewportHeight / 2) < .25,
                    "Rotated/cropped first or last text-box center was not centered vertically.");
                Require(Equals(Field(rotated, "_highlight"), location), "Rotated centering changed the source highlight bounds.");
            }
        }
        finally { rotatedWindow.Close(); File.Delete(rotatedPath); }
    }

    private static async Task ToolbarZoom()
    {
        var path = SaveFixture(5, varied: true);
        using var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            preview.SetZoom(3);
            preview.ShowLocation(new PdfLocation(4, 180, 150), highlight: false, viewportFraction: .5);
            var scroll = (ScrollViewer)Field(preview, "_scroll");
            var canvas = (Canvas)Field(preview, "_pages");
            var layout = Layout(preview, 3);
            var center = new Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2);
            var onCanvas = scroll.TranslatePoint(center, canvas);
            var document = (PdfDocument)Field(preview, "_document");
            var content = document.ViewToPage(3, new Point(onCanvas.X - layout.Left - 1, onCanvas.Y - layout.Top - 1), layout.Size);
            preview.SetZoom(1.8); window.UpdateLayout();
            SamePoint(PagePointOnViewport(preview, 3, content), center, "Toolbar +/- did not keep the viewport-center reading position");
            preview.FitWidth(); window.UpdateLayout();
            var afterFit = PagePointOnViewport(preview, 3, content);
            Require(Math.Abs(afterFit.Y - center.Y) < .25, "Fit width changed the vertical reading position.");
            Require(scroll.ScrollableWidth < 1 && scroll.HorizontalOffset < 1, "Fit width did not restore horizontal page centering.");
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(Math.Abs(PagePointOnViewport(preview, 3, content).Y - center.Y) < .25,
                "A deferred layout changed the fit-width reading position.");
        }
        finally { window.Close(); File.Delete(path); }
    }

    private static async Task ResizeReadingPosition()
    {
        var path = SaveFixture(8);
        using var preview = new PdfPreview();
        var window = Host(preview);
        try
        {
            window.Show(); await preview.LoadAsync(path); window.UpdateLayout();
            var location = new PdfLocation(4, 80, 140, 40, 12);
            var content = new Point(location.X + location.Width / 2, location.Y + location.Height / 2);
            preview.ShowLocation(location, highlight: false, viewportFraction: .5, centerBounds: true);
            void AssertCentered(string operation)
            {
                var scroll = (ScrollViewer)Field(preview, "_scroll");
                var actual = PagePointOnViewport(preview, location.Page - 1, content);
                Require(Math.Abs(actual.Y - scroll.ViewportHeight / 2) < .25,
                    $"{operation} moved the centered PDF content: Y {actual.Y:0.00}, center {scroll.ViewportHeight / 2:0.00}.");
            }
            AssertCentered("Initial navigation");
            foreach (var size in new[] { new Size(960, 600), new Size(420, 600), new Size(720, 800), new Size(680, 450) })
            {
                window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                AssertCentered("Fit-width window resize to " + size);
            }
            foreach (var width in new[] { 300d, 550d, 380d })
            {
                // Changing a split-pane column follows the same control SizeChanged
                // path even though the outer application window has not resized.
                preview.Width = width; window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                AssertCentered("Preview column resize to " + width);
            }
            Require((bool)Field(preview, "_fit"), "Resizing unexpectedly disabled fit-width mode.");
            await Render(preview);
            var rendered = RenderedPointOnViewport(preview, location.Page - 1, content);
            var viewport = (ScrollViewer)Field(preview, "_scroll");
            Require(Math.Abs(rendered.Y - viewport.ViewportHeight / 2) < .25, "Actual raster content moved away from center after resizing.");
        }
        finally { window.Close(); File.Delete(path); }
    }

    private static (double Left, double Top, Size Size) Layout(PdfPreview preview, int index)
    {
        var layout = ((IList)Field(preview, "_layouts"))[index]!;
        return ((double)layout.GetType().GetProperty("Left")!.GetValue(layout)!,
            (double)layout.GetType().GetProperty("Top")!.GetValue(layout)!,
            (Size)layout.GetType().GetProperty("Size")!.GetValue(layout)!);
    }

    private static Point PagePointOnViewport(PdfPreview preview, int index, Point point)
    {
        var layout = Layout(preview, index);
        var mapped = (Point)typeof(PdfPreview).GetMethod("PageToDisplay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(preview, [index, point])!;
        var canvas = (Canvas)Field(preview, "_pages");
        var scroll = (ScrollViewer)Field(preview, "_scroll");
        return canvas.TranslatePoint(new Point(layout.Left + 1 + mapped.X, layout.Top + 1 + mapped.Y), scroll);
    }

    private static Point RenderedPointOnViewport(PdfPreview preview, int index, Point point)
    {
        var visual = ((IDictionary)Field(preview, "_visuals"))[index] ?? throw new InvalidOperationException("Anchored page was not rendered.");
        var image = (Image)visual.GetType().GetProperty("Image")!.GetValue(visual)!;
        preview.UpdateLayout();
        var mapped = (Point)typeof(PdfPreview).GetMethod("PageToDisplay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(preview, [index, point])!;
        return image.TranslatePoint(mapped, (ScrollViewer)Field(preview, "_scroll"));
    }

    private static void SamePoint(Point actual, Point expected, string message) =>
        Require(Math.Abs(actual.X - expected.X) < .25 && Math.Abs(actual.Y - expected.Y) < .25,
            $"{message}: ({actual.X:0.00}, {actual.Y:0.00}) != ({expected.X:0.00}, {expected.Y:0.00})");
}
