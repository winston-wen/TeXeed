using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace TeXeed.App.Controls;

internal sealed class EmojiTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(EmojiTextBlock), new PropertyMetadata("", (d, _) => ((EmojiTextBlock)d).Update()));
    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }

    private void Update()
    {
        Inlines.Clear();
        var plain = new StringBuilder();
        foreach (var (value, emoji) in EmojiSprite.Split(SourceText ?? ""))
        {
            var image = emoji ? EmojiSprite.Create(value, FontSize) : null;
            if (image is null) { plain.Append(value); continue; }
            image.SetBinding(WidthProperty, new Binding(nameof(FontSize)) { Source = this });
            image.SetBinding(HeightProperty, new Binding(nameof(FontSize)) { Source = this });
            if (plain.Length > 0) { Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
            Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.TextBottom });
        }
        if (plain.Length > 0) Inlines.Add(new Run(plain.ToString()));
    }
}
