using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Shared process details for live and settled turns.
public sealed class AiActivityView : Expander
{
    private readonly Run _text = new(" ");
    private readonly Hyperlink _link = new() { TextDecorations = null };
    private readonly TextBlock _header;
    private TextEditor? _details;
    private TextBlock? _time;
    private StackPanel? _detailContent;
    private Border? _detailSurface;
    private string? _detailText;
    private double _horizontalBarHeight;
    private AiSegment? _segment;
    private static readonly FontFamily DetailFont = new() { FamilyMaps = { new FontFamilyMap { Target = "Cascadia Mono, Consolas" } }, LineSpacing = AiChatTypography.MetadataLineHeight / AiChatTypography.MetadataFontSize };
    private static readonly ControlTemplate ActivityTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander">
             <StackPanel><ContentPresenter ContentSource="Header"/><ContentPresenter x:Name="Body" Visibility="Collapsed"/></StackPanel>
             <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    public AiActivityView()
    {
        FontSize = AiChatTypography.MetadataFontSize; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(ForegroundProperty, "Muted");
        Template = ActivityTemplate;
        _header = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = AiChatTypography.ExpandedLineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        _link.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        _link.Inlines.Add(_text); _link.Click += (_, _) => IsExpanded = !IsExpanded; _header.Inlines.Add(_link);
        Header = _header;
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
        _text.Text = _segment.Text;
        var text = string.IsNullOrEmpty(_segment.Details) ? _segment.Text : _segment.Details;
        var hasDetails = text.Contains('\n') || text.Contains('\r') || text != _segment.Text;
        if (!IsExpanded) return;
        if (_detailContent is null)
        {
            _time = new TextBlock { FontSize = AiChatTypography.MetadataFontSize, Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
            _time.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _detailContent = new StackPanel(); _detailContent.Children.Add(_time);
            _detailSurface = new Border { Child = _detailContent, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0) };
            _detailSurface.SetResourceReference(Border.BackgroundProperty, "PanelBackground"); Content = _detailSurface;
        }
        _time!.Text = _segment.DisplayTime;
        if (!hasDetails)
        {
            if (_details is not null) _details.Visibility = Visibility.Collapsed;
            return;
        }
        if (_details is null)
        {
            _details = new TextEditor { IsReadOnly = true, ShowLineNumbers = true, WordWrap = false,
                MaxHeight = 320, FontFamily = DetailFont, FontSize = AiChatTypography.MetadataFontSize, FontWeight = FontWeights.Light,
                BorderThickness = new Thickness(0), Padding = new Thickness(8), Background = System.Windows.Media.Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            _details.Document.UndoStack.SizeLimit = 0;
            _details.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not ScrollViewer scroll) return;
                var bar = (FrameworkElement)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
                _horizontalBarHeight = scroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? bar.ActualHeight : 0;
                _details.Height = Math.Min(320, _details.Document.LineCount * AiChatTypography.MetadataLineHeight + 16 + _horizontalBarHeight);
            }));
            foreach (var numbers in _details.TextArea.LeftMargins.OfType<ICSharpCode.AvalonEdit.Editing.LineNumberMargin>())
                numbers.Margin = new Thickness(0, 0, 8, 0);
            _details.SetResourceReference(ForegroundProperty, "Muted");
            _details.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Muted");
            _details.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "TextSelectionBackground");
            _details.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionForegroundProperty, "TextSelectionForeground");
            _detailContent.Children.Add(_details);
        }
        _details.Visibility = Visibility.Visible;
        if (_detailText != text)
        {
            _detailText = text; _details.Text = text;
            _details.Height = Math.Min(320, _details.Document.LineCount * AiChatTypography.MetadataLineHeight + 16 + _horizontalBarHeight);
        }
    }
}

internal static class AiChatTypography
{
    internal const double FontSize = 16, MetadataFontSize = 13, MetadataLineHeight = 20, TextLeft = 36;
    internal const double ExpandedLineHeight = MetadataLineHeight + MetadataFontSize / 2;
    // Align the RichTextBox viewport with the surrounding text.
    internal const double RichTextInset = 5;
}
