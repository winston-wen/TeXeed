using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using TeXeed.App.Controls;
using TeXeed.Core;

internal static partial class Program
{
    private static string ManyLines(int count = 200) => string.Join('\n', Enumerable.Range(1, count).Select(n => $"Line {n:000} sample text"));

    private static Window HostEditor(LatexEditor editor, double width = 600, double height = 360)
    {
        var window = new Window
        {
            Content = editor, Width = width, Height = height, Left = -10000, Top = -10000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Opacity = 0
        };
        window.Show();
        SettleLayout(window);
        return window;
    }

    private static void SettleLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static SourceLocation Anchor(LatexEditor editor)
        => editor.ViewportLocation ?? throw new InvalidOperationException("Laid-out editor has no viewport anchor.");

    private static void CenteredNavigation()
    {
        var editor = new LatexEditor();
        var content = ManyLines();
        editor.SetProjectFiles([File("center.tex", content), File("other.tex", content)]);
        editor.ShowFile(File("center.tex", content), content);
        var window = HostEditor(editor);
        try
        {
            var view = editor.TextArea.TextView;
            void Navigate(int line)
            {
                editor.NavigateTo(new SourceLocation("center.tex", line), center: true);
                SettleLayout(window);
                var y = view.GetVisualPosition(editor.TextArea.Caret.Position, VisualYPosition.LineMiddle).Y - view.ScrollOffset.Y;
                Require(Math.Abs(y - view.ActualHeight / 2) < 1,
                    $"A single navigation did not center line {line}: y={y:F1}, viewport center={view.ActualHeight / 2:F1}.");
            }
            Navigate(80);
            Navigate(82); // Already visible, only two rows away from the previous target.
            editor.ScrollToVerticalOffset(editor.VerticalOffset + view.DefaultLineHeight * 2);
            SettleLayout(window);
            Navigate(82); // Same caret, changed viewport: no position-change event.
            Navigate(81);
            editor.FontSize = 24;
            SettleLayout(window);
            Navigate(82);
            editor.ShowFile(File("other.tex", content), content);
            SettleLayout(window);
            Navigate(80); // Restore a file and center on its first navigation.
        }
        finally { window.Close(); }
    }

    private static void ViewportScroll()
    {
        var editor = new LatexEditor();
        editor.ShowFile(File("viewport.tex", ManyLines()), ManyLines());
        Require(editor.ViewportLocation is null, "An editor without layout reported a guessed anchor.");
        var observed = new List<SourceLocation>();
        editor.ViewportLocationChanged += (_, location) =>
        {
            Require(location == editor.ViewportLocation, "Viewport event fired before the rendered layout was stable.");
            observed.Add(location);
        };
        var window = HostEditor(editor);
        try
        {
            var initial = Anchor(editor);
            Require(initial.Line > 3 && initial.Line < 30, "Initial anchor does not reflect viewport center.");
            Require(observed.Count == 1 && observed[0] == initial, "Initial layout did not emit one coalesced anchor.");
            var caret = editor.CaretOffset;
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 40);
            SettleLayout(window);
            var scrolled = Anchor(editor);
            Require(scrolled.Line >= initial.Line + 38 && editor.CaretOffset == caret,
                "Scrolling failed to advance the anchor without moving the caret.");
            Require(observed.Count == 2 && observed[^1] == scrolled, "Scrolling did not emit the new viewport anchor once.");

            editor.CaretOffset = editor.Document.GetLineByNumber(scrolled.Line).Offset;
            SettleLayout(window);
            editor.CaretOffset = editor.Document.GetLineByNumber(scrolled.Line + 1).Offset + 3;
            SettleLayout(window);
            Require(Anchor(editor) == scrolled && observed.Count == 2,
                "Moving the caret within an unchanged viewport affected preview synchronization.");
            editor.TextArea.TextView.Redraw();
            SettleLayout(window);
            Require(observed.Count == 2, "An unchanged layout emitted a duplicate viewport anchor.");
        }
        finally { window.Close(); }
    }

    private static void ViewportResizeAndFiles()
    {
        var editor = new LatexEditor();
        var content = ManyLines();
        var first = File("first.tex", content);
        editor.ShowFile(first, content);
        var observed = new List<SourceLocation>();
        editor.ViewportLocationChanged += (_, location) => observed.Add(location);
        var window = HostEditor(editor, height: 240);
        try
        {
            var initial = Anchor(editor);
            window.Height = 520;
            SettleLayout(window);
            Require(Anchor(editor).Line > initial.Line + 3 && observed[^1] == Anchor(editor),
                "Resizing the viewport did not refresh its center anchor.");
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 35);
            SettleLayout(window);
            var firstAnchor = Anchor(editor);
            editor.ShowFile(File("short.tex", "one\ntwo"), "one\ntwo");
            SettleLayout(window);
            Require(Anchor(editor) == new SourceLocation("short.tex", 2, 1) && observed[^1] == Anchor(editor),
                "Switching to a short file retained the old anchor or escaped the document bounds.");
            editor.ShowFile(first, content);
            SettleLayout(window);
            Require(Anchor(editor) == firstAnchor && observed[^1] == firstAnchor,
                "Returning to a file lost the viewport's saved scroll position.");
            editor.ShowFile(File("empty.tex", ""), "");
            SettleLayout(window);
            Require(Anchor(editor) == new SourceLocation("empty.tex", 1, 1), "An empty document has an invalid anchor.");
            editor.ClearSession();
            SettleLayout(window);
            Require(editor.ViewportLocation is null, "Closing a project retained its viewport source location.");
        }
        finally { window.Close(); }
    }

    private static void ViewportWrapAndFold()
    {
        var editor = new LatexEditor { WordWrap = true };
        var content = string.Join(' ', Enumerable.Repeat("a wrapped paragraph", 300)) + "\nend";
        editor.ShowFile(File("wrapped.tex", content), content);
        var window = HostEditor(editor, width: 300, height: 230);
        try
        {
            var initial = Anchor(editor);
            Require(initial.Line == 1 && initial.Column > 1, "Wrapped rows were treated as unwrapped source lines.");
            editor.ScrollToVerticalOffset(editor.TextArea.TextView.DefaultLineHeight * 12);
            SettleLayout(window);
            var scrolled = Anchor(editor);
            Require(scrolled.Line == 1 && scrolled.Column > initial.Column && editor.CaretOffset == 0,
                "Scrolling inside one wrapped paragraph did not advance the source column.");
            window.Width = 550;
            SettleLayout(window);
            Require(Anchor(editor).Line == 1 && Anchor(editor).Column != scrolled.Column,
                "Reflow after a width change retained a stale wrapped-row anchor.");

            editor.WordWrap = false;
            content = ManyLines();
            editor.ShowFile(File("folded.tex", content), content);
            SettleLayout(window);
            var manager = FoldingManager.Install(editor.TextArea);
            try
            {
                var fold = manager.CreateFolding(editor.Document.GetLineByNumber(2).Offset,
                    editor.Document.GetLineByNumber(101).Offset);
                fold.IsFolded = true;
                SettleLayout(window);
                Require(Anchor(editor).Line > 101, "The viewport center counted hidden folded lines as visible text.");
                editor.ScrollToEnd();
                SettleLayout(window);
                var final = Anchor(editor);
                Require(final.Line <= editor.Document.LineCount && final.Line > 180,
                    "Document-end scrolling produced an out-of-range or stale anchor.");
            }
            finally { FoldingManager.Uninstall(manager); }
        }
        finally { window.Close(); }
    }
    private static void ViewportTopBoundary()
    {
        var editor = new LatexEditor { WordWrap = true };
        var text = "\\chapter{Chapter}\n% comment\n\n" + string.Join(' ', Enumerable.Repeat("long wrapped text", 300));
        editor.ShowFile(File("chapter.tex", text), text);
        var window = HostEditor(editor, width: 350, height: 280);
        try
        {
            var center = Anchor(editor);
            Require(center.Line > 1, "Top-boundary fixture must have a center below its chapter heading.");
            Require(editor.ViewportFollowLocation == new SourceLocation("chapter.tex", 1, 1), "File start was unreachable when source was at the top.");
            var events = 0;
            editor.ViewportLocationChanged += (_, _) => events++;
            editor.ScrollToVerticalOffset(.1); SettleLayout(window);
            Require(editor.ViewportFollowLocation == editor.ViewportLocation && events == 1,
                "Leaving the top edge must notify follow even if the center row is unchanged.");
            editor.ScrollToVerticalOffset(0); SettleLayout(window);
            Require(editor.ViewportFollowLocation == new SourceLocation("chapter.tex", 1, 1) && events == 2,
                "Returning to the top edge did not resume following the chapter's beginning.");
            Require(editor.CaretOffset == 0 && editor.Text == text, "Top-boundary tracking altered source or caret.");
        }
        finally { window.Close(); }
    }
}
