using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

// Shared by live and settled turns; no chrome or separate disclosure button.
public sealed class AiActivityView : Expander
{
    private readonly Run _time = new(" "), _text = new(" ");
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Cascadia Code, Consolas"), FontSize = 12,
        BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0), Background = System.Windows.Media.Brushes.Transparent };
    private AiSegment? _segment;
    private static readonly ControlTemplate ActivityTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander">
             <StackPanel><ContentPresenter ContentSource="Header"/><ContentPresenter x:Name="Body" Visibility="Collapsed"/></StackPanel>
             <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    public AiActivityView()
    {
        FontSize = 12; Margin = new Thickness(0, 0, 0, 17); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(ForegroundProperty, "Muted"); _details.SetResourceReference(ForegroundProperty, "Muted");
        Template = ActivityTemplate;
        var header = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        _time.FontSize = 10; header.Inlines.Add(_time); header.Inlines.Add(new Run("  "));
        var link = new Hyperlink { TextDecorations = null }; link.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        link.Inlines.Add(_text); link.Click += (_, _) => IsExpanded = !IsExpanded; header.Inlines.Add(link);
        Header = header; Content = _details;
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
        _details.Text = string.IsNullOrEmpty(_segment.Details) ? _segment.Text : _segment.Details;
    }
}
