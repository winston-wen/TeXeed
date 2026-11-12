using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Syntax.Inlines;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

internal sealed record AiFormContext(string TurnId, Func<string, string, string> Read, Action<string, string, string> Write, Func<bool> IsLocked,
    Action<string, string, string>? PickAttachment = null, Func<string, AiFormAttachmentState>? AttachmentState = null,
    Action<string, string, Control, string?>? Observe = null, Func<string, string, string?>? Ambiguous = null,
    Func<string, string, FormFieldFeedback?>? Feedback = null);

public sealed partial class MarkdownView
{
    private AiFormContext? _forms;
    private readonly List<Control> _formControls = [];
    private readonly List<FormFeedbackHost> _formFeedbackHosts = [];
    private readonly List<FrameworkElement> _formBlockWidths = [];
    private readonly Dictionary<FormFeedbackHost, string> _formCellCandidates = [];
    private readonly Dictionary<FormFeedbackHost, int> _formCellColumns = [];
    private bool _standaloneFormLine;
    private bool IsStandaloneFormLine(ContainerInline source, LiteralInline literal)
    {
        if ((_forms ?? _tableOwner?._forms) is null || source.Parent is not null) return false;
        static bool Boundary(Markdig.Syntax.Inlines.Inline? item, bool previous)
        {
            while (item is LiteralInline whitespace && string.IsNullOrWhiteSpace(whitespace.Content.ToString()))
                item = previous ? item.PreviousSibling : item.NextSibling;
            return item is null or LineBreakInline;
        }
        if (!Boundary(literal.PreviousSibling, true) || !Boundary(literal.NextSibling, false)) return false;
        var text = literal.Content.ToString().Trim();
        if (!text.StartsWith("{{text:", StringComparison.Ordinal) && !text.StartsWith("{{textarea:", StringComparison.Ordinal)) return false;
        var match = AiForms.Token.Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length && match.Groups["tid"].Success;
    }
    internal void ConfigureForms(AiFormContext? forms) { _forms = forms; if (!string.IsNullOrEmpty(Markdown)) Render(); }
    internal void RefreshFormEnabled()
    {
        var locked = (_forms ?? _tableOwner?._forms)?.IsLocked() != false;
        foreach (var control in _formControls)
            if (control is TextBox text) { text.IsEnabled = true; text.IsReadOnly = locked; }
            else if (control is FormAttachmentButton file) { file.IsEnabled = !locked; file.Refresh(); }
            else control.IsEnabled = !locked;
        foreach (var host in _formFeedbackHosts) host.Refresh();
        foreach (var table in _tableViews) table.View.RefreshFormEnabled();
    }
    internal void RefreshFormFeedback(string type, string id)
    {
        foreach (var host in _formFeedbackHosts)
            if (host.FieldType == type && host.FieldId == id) host.Refresh();
        foreach (var table in _tableViews) table.View.RefreshFormFeedback(type, id);
    }
    private void AddFormText(InlineCollection target, string text)
    {
        var forms = _forms ?? _tableOwner?._forms;
        if (forms is null || !text.Contains("{{", StringComparison.Ordinal)) { AddMathText(target, text); return; }
        var offset = 0;
        foreach (Match match in AiForms.Token.Matches(text))
        {
            if (!_standaloneFormLine) AddMathText(target, text[offset..match.Index]);
            var id = match.Groups["tid"].Success ? match.Groups["tid"].Value : match.Groups["rid"].Success ? match.Groups["rid"].Value : match.Groups["cid"].Success ? match.Groups["cid"].Value : match.Groups["fid"].Value;
            var type = match.Groups["tid"].Success ? match.Groups["text"].Value : match.Groups["rid"].Success ? "radio" : match.Groups["cid"].Success ? "checkbox" : "file";
            Control control;
            if (match.Groups["tid"].Success)
            {
                var multiline = _standaloneFormLine && _tableOwner is null && match.Groups["text"].Value == "textarea";
                var box = new TextBox { Text = forms.Read(type, id), IsReadOnly = forms.IsLocked(), MinWidth = 90, MinHeight = 25, Width = _standaloneFormLine && _tableOwner is null ? double.NaN : 120, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top };
                System.Windows.Automation.AutomationProperties.SetName(box, id);
                box.TextChanged += (_, _) => { if (!forms.IsLocked()) forms.Write(type, id, box.Text); };
                if (forms.Ambiguous?.Invoke(type, id) is { } oldText) box.ToolTip = "旧记录的同名不同类型字段有冲突，无法判断旧值归属；请核对。\n保留的旧值：\n" + oldText;
                forms.Observe?.Invoke(type, id, box, null);
                control = box;
                if (_standaloneFormLine && _tableOwner is null)
                {
                    FrameworkElement surface = box;
                    if (multiline)
                    {
                        var lineHeight = ChatLineHeight;
                        TextBlock.SetLineHeight(box, lineHeight);
                        TextBlock.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
                        box.MinLines = 3; box.MinHeight = 3 * lineHeight + 6; box.Padding = new Thickness(4, 1, 4, 1);
                        box.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                        box.BorderThickness = new Thickness(0);
                        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                        var numbers = new TextBoxLineNumbers { MinimumLineCount = 3, ReserveOnlyEmptyVisualLines = true, ClipToAncestorViewport = true };
                        numbers.SetResourceReference(Control.ForegroundProperty, "Muted");
                        numbers.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
                        numbers.SetResourceReference(Control.BorderBrushProperty, "Guide");
                        numbers.Attach(box);
                        Grid.SetColumn(box, 1); grid.Children.Add(numbers); grid.Children.Add(box);
                        var border = new Border { Child = grid, BorderThickness = new Thickness(1) };
                        border.SetResourceReference(Border.BorderBrushProperty, "Guide");
                        border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
                        surface = border;
                    }
                    var host = new FormFeedbackHost(surface, control, true, type, id, () => forms.Feedback?.Invoke(type, id)) { Width = MediaWidth };
                    _formFeedbackHosts.Add(host); _formBlockWidths.Add(host);
                    _formControls.Add(control);
                    target.Add(new InlineUIContainer(host) { BaselineAlignment = BaselineAlignment.Center, Tag = match.Value });
                    offset = match.Index + match.Length;
                    continue;
                }
            }
            else if (match.Groups["fid"].Success)
            {
                control = CreateFormAttachmentControl(forms, id, match.Groups["flabel"].Value);
            }
            else if (match.Groups["rid"].Success)
            {
                var value = match.Groups["value"].Value;
                var group = "texeed-form-" + forms.TurnId + "-" + id + (forms.Observe is null ? "" : "-" + Guid.NewGuid().ToString("N"));
                var radio = new RadioButton { GroupName = group, Content = match.Groups["rlabel"].Value, IsChecked = forms.Read("radio", id) == value, IsEnabled = !forms.IsLocked(), Margin = new Thickness(3, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                radio.Checked += (_, _) => { if (!forms.IsLocked()) forms.Write("radio", id, value); };
                if (forms.Ambiguous?.Invoke("radio", id) is { } oldRadio) radio.ToolTip = "旧记录的同名不同类型字段有冲突，无法判断旧值归属；请核对。\n保留的旧值：\n" + oldRadio;
                forms.Observe?.Invoke("radio", id, radio, value);
                control = radio;
            }
            else
            {
                var check = new CheckBox { Content = match.Groups["clabel"].Value, IsChecked = forms.Read("checkbox", id) == "true", IsEnabled = !forms.IsLocked(), Margin = new Thickness(3, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                check.Checked += (_, _) => { if (!forms.IsLocked()) forms.Write("checkbox", id, "true"); };
                check.Unchecked += (_, _) => { if (!forms.IsLocked()) forms.Write("checkbox", id, "false"); };
                if (forms.Ambiguous?.Invoke("checkbox", id) is { } oldCheck) check.ToolTip = "旧记录的同名不同类型字段有冲突，无法判断旧值归属；请核对。\n保留的旧值：\n" + oldCheck;
                forms.Observe?.Invoke("checkbox", id, check, null);
                control = check;
            }
            _formControls.Add(control);
            var inlineHost = new FormFeedbackHost(control, control, false, type, id, () => forms.Feedback?.Invoke(type, id));
            _formFeedbackHosts.Add(inlineHost);
            if (_standaloneFormLine && _tableOwner is not null && control is TextBox) _formCellCandidates[inlineHost] = match.Value;
            target.Add(new InlineUIContainer(inlineHost) { BaselineAlignment = BaselineAlignment.Center, Tag = match.Value });
            offset = match.Index + match.Length;
        }
        if (!_standaloneFormLine) AddMathText(target, text[offset..]);
    }
}
