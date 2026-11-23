using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private Popup? _equationPreview;

    private sealed class EquationReference : Hyperlink
    {
        private readonly MarkdownView _view;
        private readonly string _label;
        private Point? _pressPosition;
        private MarkdownView Owner => _view._tableOwner ?? _view;

        static EquationReference() => EventManager.RegisterClassHandler(typeof(EquationReference), ClickEvent,
            new RoutedEventHandler((sender, e) =>
            {
                var link = (EquationReference)sender;
                link._view.ShowEquationPreview(link, link.Owner._equations.GetValueOrDefault(link._label));
                e.Handled = true;
            }));

        internal EquationReference(MarkdownView view, string label, string number) : base(new Run($"({number})"))
        {
            _view = view; _label = label;
            ToolTip = "单击预览公式，双击跳转。Enter 预览，Ctrl+Enter 跳转。";
            AutomationProperties.SetHelpText(this, (string)ToolTip);
            SetResourceReference(ForegroundProperty, "Accent");
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) { base.OnPreviewMouseDown(e); return; }
            if (e.ClickCount == 2 && _view._equationPreview?.Tag == this)
            {
                _pressPosition = null;
                Navigate();
            }
            else _pressPosition = e.GetPosition(_view);
            e.Handled = true;
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || _pressPosition is not Point down)
            {
                base.OnPreviewMouseUp(e);
                return;
            }
            _pressPosition = null;
            var delta = e.GetPosition(_view) - down;
            if (Math.Abs(delta.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) <= SystemParameters.MinimumVerticalDragDistance)
                OnClick();
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _pressPosition = null;
            base.OnMouseLeave(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control) Navigate();
                else OnClick();
                e.Handled = true;
            }
            else base.OnPreviewKeyDown(e);
        }

        private void Navigate()
        {
            if (Owner._equations.TryGetValue(_label, out var target))
            {
                if (_view._equationPreview is { } preview) preview.IsOpen = false;
                Owner.NavigateEquation(target);
            }
            else _view.ShowEquationPreview(this, null);
        }
    }

    private void ShowEquationPreview(EquationReference link, Paragraph? target)
    {
        if (_equationPreview is { IsOpen: true } old)
        {
            if (old.Tag == link) return;
            old.IsOpen = false;
        }
        if (Window.GetWindow(this) is not { } window || !link.ContentStart.HasValidLayout) return;
        var anchor = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (anchor.IsEmpty) return;
        var anchorPosition = TranslatePoint(anchor.TopLeft, window);
        var surface = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = Math.Max(100, Math.Min(window.ActualWidth - 16, ActualWidth)), MaxHeight = Math.Max(80, window.ActualHeight * .65) };
        surface.SetResourceReference(Border.BackgroundProperty, "EditorBackground");
        surface.SetResourceReference(Border.BorderBrushProperty, "Guide");
        if (target?.Inlines.FirstInline is InlineUIContainer { Child: ChatFormulaRow { Child: ChatFormulaHost host } })
        {
            var formula = host.Formula.PreviewCopy();
            formula.SetValue(TextElement.FontSizeProperty, host.Formula.GetValue(TextElement.FontSizeProperty));
            formula.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            var number = new TextBlock { Text = ((Run)link.Inlines.FirstInline!).Text, FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(ForegroundProperty, "Muted");
            var row = new DockPanel();
            DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
            row.Children.Add(new ScrollViewer { Content = formula, Focusable = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            surface.Child = row;
        }
        else
        {
            var missing = new TextBlock { Text = "未找到引用的公式", TextWrapping = TextWrapping.Wrap };
            missing.SetResourceReference(ForegroundProperty, "Ink"); surface.Child = missing;
        }

        // Keep mouse capture in the document so the second click reaches the same hyperlink.
        var popup = new Popup { Tag = link, Child = surface, PlacementTarget = this, PlacementRectangle = anchor,
            Placement = PlacementMode.Bottom, VerticalOffset = 3, StaysOpen = true, Focusable = false, AllowsTransparency = true };
        _equationPreview = popup;
        void Dismiss(object? sender, EventArgs e) => popup.IsOpen = false;
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible) popup.IsOpen = false;
        }
        void Outside(object sender, MouseButtonEventArgs e)
        {
            for (var node = e.OriginalSource as FrameworkContentElement; node is not null; node = node.Parent as FrameworkContentElement)
                if (node == link) return;
            popup.IsOpen = false;
        }
        void Escape(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false; e.Handled = true;
        }
        void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange == 0 && e.VerticalChange == 0) return;
            if (!IsLoaded || !link.ContentStart.HasValidLayout) return;
            var bounds = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (bounds.IsEmpty || (TranslatePoint(bounds.TopLeft, window) - anchorPosition).Length > 1)
                popup.IsOpen = false;
        }
        window.PreviewMouseDown += Outside; window.PreviewKeyDown += Escape;
        window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled), true);
        window.Deactivated += Dismiss; window.LocationChanged += Dismiss; window.SizeChanged += Dismiss;
        IsVisibleChanged += VisibilityChanged; Unloaded += Dismiss;
        popup.Closed += (_, _) =>
        {
            window.PreviewMouseDown -= Outside; window.PreviewKeyDown -= Escape;
            window.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
            window.Deactivated -= Dismiss; window.LocationChanged -= Dismiss; window.SizeChanged -= Dismiss;
            IsVisibleChanged -= VisibilityChanged; Unloaded -= Dismiss;
            if (ReferenceEquals(_equationPreview, popup)) _equationPreview = null;
        };
        popup.IsOpen = true;
    }
}
