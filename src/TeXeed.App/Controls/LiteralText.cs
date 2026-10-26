using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace TeXeed.App.Controls;

/// <summary>UI data is literal; mnemonic parsing is an explicit opt-in for command captions.</summary>
public static class LiteralText
{
    public static readonly DependencyProperty RecognizesAccessKeyProperty = DependencyProperty.RegisterAttached(
        "RecognizesAccessKey", typeof(bool), typeof(LiteralText), new FrameworkPropertyMetadata(false));
    public static bool GetRecognizesAccessKey(DependencyObject element) => (bool)element.GetValue(RecognizesAccessKeyProperty);
    public static void SetRecognizesAccessKey(DependencyObject element, bool value) => element.SetValue(RecognizesAccessKeyProperty, value);

    internal static string MenuLabel(MenuItem? item)
    {
        var text = item?.Header?.ToString() ?? "";
        if (item is null || !GetRecognizesAccessKey(item)) return text;
        var label = new StringBuilder(); var markerRemoved = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i + 1 < text.Length)
            {
                if (text[i + 1] == '_') { label.Append('_'); i++; continue; }
                if (!markerRemoved) { markerRemoved = true; continue; }
            }
            label.Append(text[i]);
        }
        return label.ToString();
    }
}
