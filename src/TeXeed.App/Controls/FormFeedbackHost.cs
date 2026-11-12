using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TeXeed.App.Controls;

internal sealed record FormFieldFeedback(string Result, string Comment);

internal sealed class FormFeedbackHost : Grid
{
    private readonly Func<FormFieldFeedback?>? _feedback;
    private TextBlock? _mark;
    private readonly string _originalHelp;
    internal Control Field { get; }
    internal string FieldType { get; }
    internal string FieldId { get; }

    internal FormFeedbackHost(FrameworkElement surface, Control field, bool stretch, string type, string id, Func<FormFieldFeedback?>? feedback)
    {
        Field = field; FieldType = type; FieldId = id; _feedback = feedback; _originalHelp = AutomationProperties.GetHelpText(field);
        ColumnDefinitions.Add(new ColumnDefinition { Width = stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(surface);
        Refresh();
    }
    internal void StretchToWidth(double width)
    {
        ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        if (Field is TextBox text) text.Width = double.NaN;
        Width = width;
    }
    internal void Refresh()
    {
        var grade = _feedback?.Invoke();
        if (grade is null)
        {
            if (_mark is not null) { _mark.Visibility = Visibility.Collapsed; _mark.ToolTip = null; }
            AutomationProperties.SetHelpText(Field, _originalHelp); return;
        }
        if (_mark is null)
        {
            _mark = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(5, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            _mark.SetResourceReference(TextBlock.ForegroundProperty, "ChatHeading"); Grid.SetColumn(_mark, 1); Children.Add(_mark);
        }
        var (symbol, label) = grade.Result switch { "correct" => ("✓", "正确"), "incorrect" => ("✕", "错误"), _ => ("?", "待核实") };
        var detail = "AI 判定：" + label + (string.IsNullOrWhiteSpace(grade.Comment) ? "" : "\n" + grade.Comment);
        _mark.Text = symbol; _mark.Visibility = Visibility.Visible; _mark.ToolTip = detail;
        AutomationProperties.SetName(_mark, detail);
        AutomationProperties.SetHelpText(Field, string.IsNullOrWhiteSpace(_originalHelp) ? detail : _originalHelp + "\n" + detail);
    }
}
