using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeXeed.App;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static async Task CheckPreviewSplitFollowAsync(MainWindow window, TexProject project, string pdf, AppSettings settings)
    {
        var model = (TabWorkspace)Field(window, "_workspaceTabs")!;
        var preview = (PdfPreview)window.FindName("Preview");
        var sync = new SyncTexService();
        var farPoint = await sync.ForwardAsync(project, pdf, new SourceLocation(project.MainPath, 250), settings)
            ?? throw new Exception("Split follow fixture lacks distant PDF location");
        var inversePoint = await sync.ForwardAsync(project, pdf, new SourceLocation(project.MainPath, 130), settings)
            ?? throw new Exception("Split follow fixture lacks inverse PDF location");
        foreach (var mode in new[] { PreviewFollowMode.Viewport, PreviewFollowMode.Cursor, PreviewFollowMode.None })
        foreach (var target in new[] { TabSide.Left, TabSide.Right })
        {
            var sourceSide = target == TabSide.Left ? TabSide.Right : TabSide.Left;
            Invoke(window, "MoveWorkspaceTab", project.MainPath, sourceSide, int.MaxValue, false);
            var source = (LatexEditor)window.FindName(sourceSide == TabSide.Left ? "LeftEditor" : "RightEditor");
            source.CaretOffset = source.Document.GetLineByNumber(5).Offset;
            Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, sourceSide, int.MaxValue, false);
            Invoke(window, "SelectWorkspaceTab", sourceSide, TabWorkspace.PreviewKey);
            Invoke(window, "ChangeFollowMode", mode);
            Invoke(window, "CancelPendingNavigation");
            preview.ShowLocation(farPoint);
            await WaitForPreviewRenderedAsync(window);
            var caret = source.CaretOffset;
            Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, target, int.MaxValue, true);
            await WaitForPreviewRenderedAsync(window);
            await Task.Delay(450);
            if (preview.VisiblePage != farPoint.Page || source.CaretOffset != caret || !(bool)Field(window, "_followAwaitingSourceInteraction")!)
                throw new Exception($"{mode} {target}: preview split changed the PDF page or source caret");

            if (target == TabSide.Left)
            {
                preview.ShowLocation(inversePoint); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var scroll = (System.Windows.Controls.ScrollViewer)Field(preview, "_scroll")!;
                var offset = scroll.VerticalOffset;
                await CallAsync(window, "InverseAsync", inversePoint);
                if ((bool)Field(window, "_followAwaitingSourceInteraction")! || source.CaretOffset == caret)
                    throw new Exception($"{mode}: inverse lookup failed to resume after split");
                await AssertPdfOffsetStableAsync(scroll, offset, $"{mode}: inverse lookup fed back into the preview");
            }
            else
            {
                source.TextArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if ((bool)Field(window, "_followAwaitingSourceInteraction")!) throw new Exception($"{mode}: source click did not resume following");
                if (mode == PreviewFollowMode.None)
                {
                    await Task.Delay(400);
                    if (preview.VisiblePage != farPoint.Page) throw new Exception("None mode followed the source click");
                }
                else await WaitUntilAsync(() => preview.VisiblePage != farPoint.Page, $"{mode}: source click did not update the preview");
            }
            Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, sourceSide, int.MaxValue, false);
            Invoke(window, "SelectWorkspaceTab", sourceSide, TabWorkspace.PreviewKey);
            Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, target, int.MaxValue, true);
            if (!(bool)Field(window, "_followAwaitingSourceInteraction")!) throw new Exception("Repeated split did not defer following");
            await CallAsync(window, "ForwardAsync", true);
            if ((bool)Field(window, "_followAwaitingSourceInteraction")! || Field(preview, "_highlight") is not PdfLocation)
                throw new Exception($"{mode}: Ctrl+J did not locate and resume after split");
        }
        Invoke(window, "MoveWorkspaceTab", project.MainPath, TabSide.Left, int.MaxValue, false);
        Invoke(window, "MoveWorkspaceTab", TabWorkspace.PreviewKey, TabSide.Right, int.MaxValue, false);
        Console.WriteLine("PASS: real PDF split preserves both positions and resumes on source interaction or inverse lookup in every follow mode");
    }
}
