using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

internal static partial class Program
{
    private static void CheckEditorTypography()
    {
        if (EditorTypography.FindLightWeight([]) != FontWeights.Normal) throw new Exception("Missing Sarasa Light did not fall back to regular weight");
        if (EditorTypography.WeightFor("Consolas") != FontWeights.Normal) throw new Exception("Custom editor font was forced to Light");
        if (new AppSettings().EditorFontFamily != EditorTypography.FamilyName) throw new Exception("Editor default does not match input defaults");
        foreach (var theme in new[] { "ristretto", "github-light-default" })
        {
            ThemeManager.Apply(theme);
            var textBox = new TextBox(); var richText = new RichTextBox(); var password = new PasswordBox(); var readOnly = new TextBox { IsReadOnly = true };
            var content = new StackPanel(); content.Children.Add(textBox); content.Children.Add(richText); content.Children.Add(password); content.Children.Add(readOnly);
            var testWindow = new Window { Content = content, Width = 200, Height = 150, ShowInTaskbar = false };
            testWindow.Show(); testWindow.UpdateLayout();
            if (textBox.FontFamily.Source != EditorTypography.FamilyName || textBox.FontWeight != EditorTypography.DefaultWeight)
                throw new Exception($"{theme} text box does not use preferred editor typography: {textBox.FontFamily.Source}, {textBox.FontWeight}");
            if (richText.FontFamily.Source != EditorTypography.FamilyName || richText.FontWeight != EditorTypography.DefaultWeight || password.FontFamily.Source != EditorTypography.FamilyName || password.FontWeight != EditorTypography.DefaultWeight)
                throw new Exception($"{theme} rich text or password input does not use preferred editor typography");
            if (readOnly.FontFamily.Source == EditorTypography.FamilyName) throw new Exception("Read-only text unexpectedly received editor typography");
            testWindow.Close();
            var editor = new LatexEditor();
            if (editor.FontFamily.Source != EditorTypography.FamilyName || editor.FontWeight != EditorTypography.DefaultWeight)
                throw new Exception($"{theme} source editor does not use preferred editor typography");
        }
        ThemeManager.UseDefault();
    }
}
