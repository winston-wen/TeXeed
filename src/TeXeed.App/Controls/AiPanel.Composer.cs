using System.Windows;
using System.Windows.Controls.Primitives;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private const double ComposerLineHeight = 20;
    private void InitializeComposer()
    {
        ComposerLines.Attach(Composer);
        Composer.MinHeight = ComposerLineHeight + Composer.Padding.Top + Composer.Padding.Bottom;
        Composer.Height = ComposerLineHeight * 2 + Composer.Padding.Top + Composer.Padding.Bottom;
    }
    private void LimitComposerHeight()
    {
        if (ActualHeight <= 0) return;
        Composer.MaxHeight = Math.Max(Composer.MinHeight, ActualHeight * .65);
        Composer.Height = Math.Clamp(Composer.Height, Composer.MinHeight, Composer.MaxHeight);
    }
    private void ComposerResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Composer.Height = Math.Clamp(Composer.ActualHeight - e.VerticalChange, Composer.MinHeight, Composer.MaxHeight);
        e.Handled = true;
    }
}
