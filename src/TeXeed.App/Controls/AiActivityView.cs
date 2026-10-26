using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using ICSharpCode.AvalonEdit;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Shared by live and settled turns; no chrome or separate disclosure button.
public sealed class AiActivityView : Expander
{
    private readonly Run _time = new(" "), _text = new(" ");
    private TextEditor? _details;
    private string? _detailText;
    private AiSegment? _segment;
    private static readonly ControlTemplate ActivityTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander">
             <StackPanel><ContentPresenter ContentSource="Header"/><ContentPresenter x:Name="Body" Visibility="Collapsed"/></StackPanel>
             <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    public AiActivityView()
    {
        FontSize = AiChatTypography.FontSize; Margin = new Thickness(0, 0, 0, 8.5); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(ForegroundProperty, "Muted");
        Template = ActivityTemplate;
        var header = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = AiChatTypography.LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        header.Inlines.Add(_time); header.Inlines.Add(new Run("  "));
        var link = new Hyperlink { TextDecorations = null }; link.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        link.Inlines.Add(_text); link.Click += (_, _) => IsExpanded = !IsExpanded; header.Inlines.Add(link);
        Header = header;
        Expanded += (_, _) => Update(); Collapsed += (_, _) => Update();
        DataContextChanged += (_, _) =>
        {
            if (_segment is not null) PropertyChangedEventManager.RemoveHandler(_segment, SegmentChanged, string.Empty);
            _segment = DataContext as AiSegment;
            if (_segment is not null) PropertyChangedEventManager.AddHandler(_segment, SegmentChanged, string.Empty);
            Update();
        };
    }
    private void SegmentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();
    private void Update()
    {
        if (_segment is null) return;
        _time.Text = _segment.DisplayTime;
        _text.Text = _segment.Text + (IsExpanded ? "（已展开）" : "（已收起）");
        if (!IsExpanded) return;
        if (_details is null)
        {
            _details = new TextEditor { IsReadOnly = true, ShowLineNumbers = true, WordWrap = false,
                MaxHeight = 320, FontFamily = new("Cascadia Mono, Consolas"), FontSize = AiChatTypography.FontSize,
                BorderThickness = new Thickness(0), Padding = new Thickness(8), Background = System.Windows.Media.Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            _details.Document.UndoStack.SizeLimit = 0;
            foreach (var numbers in _details.TextArea.LeftMargins.OfType<ICSharpCode.AvalonEdit.Editing.LineNumberMargin>())
                numbers.Margin = new Thickness(0, 0, 8, 0);
            _details.SetResourceReference(ForegroundProperty, "Muted");
            _details.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Muted");
            _details.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "TextSelectionBackground");
            _details.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionForegroundProperty, "TextSelectionForeground");
            var surface = new Border { Child = _details, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0) };
            surface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); Content = surface;
        }
        var text = string.IsNullOrEmpty(_segment.Details) ? _segment.Text : _segment.Details;
        if (_detailText != text)
        {
            _detailText = text; _details.Text = text;
            _details.Height = Math.Min(320, _details.Document.LineCount * _details.TextArea.TextView.DefaultLineHeight + 16 + SystemParameters.HorizontalScrollBarHeight);
        }
    }
}

internal static class AiChatTypography
{
    internal const double FontSize = 16, LineHeight = 25;
    // WPF's native RichTextBox text viewport adds 5 DIP before the first glyph.
    // Compensate at the rich-text boundary, not by indenting every other row.
    internal const double RichTextInset = 5;
}
