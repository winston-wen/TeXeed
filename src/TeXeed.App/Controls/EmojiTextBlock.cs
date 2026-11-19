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
    internal static readonly DependencyProperty BubbleInkAxisProperty = DependencyProperty.Register(
        "BubbleInkAxis", typeof(double), typeof(EmojiTextBlock), new PropertyMetadata(double.NaN, (d, _) => ((EmojiTextBlock)d).AlignBubble()));
    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    private Image? _bubble;

    private void AlignBubble()
    {
        if (_bubble is null) return;
        var inkAxis = (double)GetValue(BubbleInkAxisProperty);
        TextBlock.SetBaselineOffset(_bubble, double.IsNaN(inkAxis) ? FontSize : FontSize / 2 - inkAxis);
    }

    private void Update()
    {
        Inlines.Clear();
        _bubble = null;
        var plain = new StringBuilder();
        foreach (var (value, emoji) in EmojiSprite.Split(SourceText ?? ""))
        {
            var image = emoji ? EmojiSprite.Create(value, FontSize) : null;
            if (image is null) { plain.Append(value); continue; }
            image.SetBinding(WidthProperty, new Binding(nameof(FontSize)) { Source = this });
            image.SetBinding(HeightProperty, new Binding(nameof(FontSize)) { Source = this });
            if (plain.Length > 0) { Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
            var bubble = value == "🗨️" && Inlines.Count == 0;
            if (bubble) _bubble = image;
            Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = bubble ? BaselineAlignment.Baseline : BaselineAlignment.TextBottom });
        }
        if (plain.Length > 0) Inlines.Add(new Run(plain.ToString()));
        AlignBubble();
    }
}
