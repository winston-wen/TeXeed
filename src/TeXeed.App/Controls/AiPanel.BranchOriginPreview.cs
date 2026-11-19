using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public partial class AiPanel
{
    private string? _branchPreviewConversation;
    private AiAnswerAnchor? _branchPreviewAnchor;

    private void RefreshBranchOriginPreview()
    {
        var conversation = _book.Active;
        var anchor = conversation.Turns.Count == 0 && conversation.LastSentAt is null &&
            conversation.ParentConversationId is not null ? conversation.BranchAnchor : null;
        EmptyConversationPrompt.Visibility = anchor is null ? Visibility.Visible : Visibility.Collapsed;
        BranchOriginPreview.Visibility = anchor is null ? Visibility.Collapsed : Visibility.Visible;
        if (anchor is null)
        {
            BranchOriginPreview.Content = null;
            _branchPreviewConversation = null;
            _branchPreviewAnchor = null;
            return;
        }
        if (_branchPreviewConversation == conversation.Id && _branchPreviewAnchor == anchor) return;
        _branchPreviewConversation = conversation.Id;
        _branchPreviewAnchor = anchor;

        var preview = new StackPanel { Margin = new Thickness(24, 12, 12, 30) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var source = new Button { Content = "返回原文", Padding = new Thickness(6, 2, 6, 2), Cursor = Cursors.Hand };
        source.Click += (_, _) => BranchSourceRequested?.Invoke(this, conversation);
        DockPanel.SetDock(source, Dock.Right); header.Children.Add(source);
        var label = new TextBlock { Text = "追问出处", VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        header.Children.Add(label); preview.Children.Add(header);

        if (anchor.PreviewKind == "formula") anchor = AiReferencePresentation.NormalizePreview(anchor);
        if (anchor.PreviewKind == "image" && Uri.TryCreate(anchor.PreviewImageUri, UriKind.Absolute, out var uri) &&
            uri.Scheme is "file" or "http" or "https")
        {
            preview.Children.Add(new MarkdownPicture(uri, anchor.PreviewSource ?? "", () => { })
            { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 700 });
        }
        else if (anchor.PreviewTableMarkdown is not null || anchor.PreviewKind == "formula" && !string.IsNullOrWhiteSpace(anchor.PreviewSource))
        {
            var markdown = anchor.PreviewTableMarkdown ?? (anchor.PreviewKind == "formula" && !string.IsNullOrWhiteSpace(anchor.PreviewSource)
                ? "$$\n" + anchor.PreviewSource + "\n$$" : anchor.Quote);
            var content = new MarkdownView { ChatMode = true, UseChatEmphasis = true, Padding = new Thickness(0),
                ImageBaseDirectory = Path.GetDirectoryName(_project), Markdown = markdown };
            preview.Children.Add(content);
        }
        else preview.Children.Add(LiteralBranchOrigin(anchor));
        BranchOriginPreview.Content = preview;
    }

    private static MarkdownView LiteralBranchOrigin(AiAnswerAnchor anchor)
    {
        var view = new MarkdownView { ChatMode = true, Padding = new Thickness(0) };
        var document = new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            FontFamily = view.FontFamily, FontSize = view.FontSize, LineHeight = Math.Max(25, view.FontSize * 25 / 16) };
        Typography.SetStylisticSet4(document, true);
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        IReadOnlyList<AiReferencePresentation.ExcerptPart> parts = anchor.PreviewParts is { Count: > 0 }
            ? AiReferencePresentation.Excerpt(anchor) : [new AiReferencePresentation.ExcerptPart(anchor.Quote)];
        foreach (var part in parts)
        {
            if (part.Formula && !string.IsNullOrWhiteSpace(part.Text))
            {
                paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                var formula = new ChatFormula(part.Text, part.Display);
                formula.SetValue(TextElement.FontSizeProperty, view.FontSize);
                var tex = part.CopyTex ?? part.Text;
                paragraph.Inlines.Add(new InlineUIContainer(formula) { Tag = part.Display ? "$$" + tex + "$$" : "$" + tex + "$", BaselineAlignment = BaselineAlignment.Baseline });
                continue;
            }
            var lines = part.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    document.Blocks.Add(paragraph);
                    paragraph = new Paragraph { Margin = new Thickness(0) };
                }
                paragraph.Inlines.Add(new Run(lines[i]));
            }
        }
        document.Blocks.Add(paragraph); view.Document = document;
        return view;
    }
}
