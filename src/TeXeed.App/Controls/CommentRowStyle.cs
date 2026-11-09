using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TeXeed.App.Controls;

/// <summary>A left-aligned text row for comments in editors and read-only previews.</summary>
internal static class CommentRowStyle
{
    internal static readonly ControlTemplate Template = CreateTemplate();
    private static ControlTemplate CreateTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Chrome");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Hover"), "Chrome"));
        template.Triggers.Add(hover);
        return template;
    }
}
