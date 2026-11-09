using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal sealed record AiReadOnlyCommentRow(string Label, IReadOnlyList<AiDocumentReply> Replies, Func<Task> Locate);

/// <summary>Displays read-only comments with virtualized rows and source navigation.</summary>
internal sealed class AiReadOnlyCommentList : UserControl
{
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new() { Text = "暂无评论。", Margin = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed };
    internal AiReadOnlyCommentList()
    {
        _list.BorderThickness = new Thickness(0); _list.Background = Brushes.Transparent;
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        _list.ItemContainerStyle = itemStyle;
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(AiReadOnlyCommentRowView)) };
        var grid = new Grid(); grid.Children.Add(_list); grid.Children.Add(_empty); Content = grid;
    }
    internal void SetRows(IReadOnlyList<AiReadOnlyCommentRow> rows)
    {
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.ItemsSource = rows;
    }
    internal void Clear() { _list.ItemsSource = null; _empty.Visibility = Visibility.Collapsed; }
}

public sealed class AiReadOnlyCommentRowView : ContentControl
{
    public AiReadOnlyCommentRowView() => DataContextChanged += (_, _) =>
    {
        if (DataContext is not AiReadOnlyCommentRow row) { Content = null; return; }
        var panel = new StackPanel();
        var button = new Button
        {
            Content = new TextBlock { Text = row.Label, TextWrapping = TextWrapping.Wrap },
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 2, 8, 2),
            Padding = new Thickness(0), Template = CommentRowStyle.Template
        };
        button.Click += async (_, _) => await row.Locate();
        panel.Children.Add(button);
        foreach (var reply in row.Replies)
            panel.Children.Add(new TextBlock { Text = (string.IsNullOrWhiteSpace(reply.Author) ? "用户" : reply.Author) + "：" + reply.Text,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 8, 2) });
        Content = panel;
    };
}
