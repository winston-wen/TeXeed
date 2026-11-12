using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static void FixedGridEmojiInput()
    {
        const string source = "\\textbf{A😀B👩🏽‍💻C}\n";
        var editor = new LatexEditor { FontSize = 20 };
        editor.ShowFile(File("emoji.tex", source), source);
        var window = HostEditor(editor);
        try
        {
            SettleLayout(window);
            var view = editor.TextArea.TextView;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var colored = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i + 2] > pixels[i] + 55 && pixels[i + 1] > pixels[i] + 25 && pixels[i + 3] > 0) colored++;
            Require(colored > 10, "Emoji did not draw colored Noto pixels in the fixed grid.");
            var start = source.IndexOf("😀", StringComparison.Ordinal); var end = source.IndexOf('C');
            editor.Select(start, end - start);
            var selected = editor.TextArea.Selection.GetText();
            var data = editor.TextArea.Selection.CreateDataObject(editor.TextArea);
            Require(selected == source[start..end] && data.GetData(DataFormats.UnicodeText) as string == selected, "Selection/copy changed source Unicode.");
            editor.Select(source.Length, 0); editor.CaretOffset = source.Length; editor.TextArea.Focus();
            var input = new TextComposition(InputManager.Current, editor.TextArea, "7");
            TextCompositionManager.StartComposition(input);
            Require(editor.Text == source + "7", "TextComposition did not insert text.");
            editor.Undo(); Require(editor.Text == source, "Undo changed the emoji source.");
            Require(editor.SyntaxHighlighting is not null, "LaTeX syntax highlighting did not load.");
            if (Environment.GetEnvironmentVariable("TEXEED_EMOJI_EDITOR_SCREENSHOT") is { Length: > 0 } path)
            { using var stream = System.IO.File.Create(Path.GetFullPath(path)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        }
        finally { window.Close(); }
    }
}
