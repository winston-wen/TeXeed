using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Rendering;

namespace TeXeed.App.Controls;

public sealed partial class LatexEditor
{
    /// <summary>Reflow display rows without changing text, caret or undo history.</summary>
    public void SetWordWrap(bool enabled)
    {
        if (WordWrap == enabled) return;
        var anchor = ViewportLocation;
        CloseCompletion();
        WordWrap = enabled;
        HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (!IsLoaded) return;
        UpdateLayout();
        if (anchor is not null && anchor.Path == CurrentFilePath)
            ScrollTo(anchor.Line, anchor.Column, VisualYPosition.LineMiddle, TextArea.TextView.ActualHeight / 2, 0);
        if (enabled) ScrollToHorizontalOffset(0);
        QueueViewportLocationChanged();
    }
}
