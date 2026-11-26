using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using TeXeed.App.AI;

namespace TeXeed.App.Controls;

public sealed partial class MarkdownView
{
    private Popup? _equationPreview;
    internal AiPanel? EquationPanel { get; set; }
    internal AiTurn? EquationSource { get; set; }
    private List<EquationReference>? _qualifiedReferences;

    internal void RefreshEquationReferences()
    {
        if (_qualifiedReferences is null) return;
        foreach (var link in _qualifiedReferences) link.Refresh();
    }

    private sealed class EquationReference : Hyperlink
    {
        private readonly MarkdownView _view;
        private readonly string _label;
        private readonly bool _parenthesize;
        private Point? _pressPosition;
        private MarkdownView Owner => _view._tableOwner ?? _view;
        internal string Number => _view.EquationNumber(_label);

        static EquationReference() => EventManager.RegisterClassHandler(typeof(EquationReference), ClickEvent,
            new RoutedEventHandler((sender, e) =>
            {
                var link = (EquationReference)sender;
                link.Preview();
                e.Handled = true;
            }));

        internal EquationReference(MarkdownView view, string label, string number, bool parenthesize) : base(new Run(parenthesize ? $"({number})" : number))
        {
            _view = view; _label = label; _parenthesize = parenthesize;
            if (AiEquationIndex.TrySplit(label, out _, out _)) (Owner._qualifiedReferences ??= []).Add(this);
            ToolTip = "单击预览公式，双击跳转。Enter 预览，Ctrl+Enter 跳转。";
            AutomationProperties.SetHelpText(this, (string)ToolTip);
            SetResourceReference(ForegroundProperty, "Accent");
        }

        internal void Refresh()
        {
            var number = Number;
            var text = _parenthesize ? $"({number})" : number;
            if (Inlines.FirstInline is Run run && run.Text != text) run.Text = text;
        }

        private void Preview()
        {
            if (AiEquationIndex.TrySplit(_label, out _, out var key) && Owner.EquationPanel is { } panel)
                _view.ShowEquationPreview(this, null, panel.ResolveEquation(Owner.EquationSource, _label, key, out _));
            else _view.ShowEquationPreview(this, Owner._equations.GetValueOrDefault(_label));
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
            if (AiEquationIndex.TrySplit(_label, out _, out var key) && Owner.EquationPanel is { } panel)
            {
                if (panel.ResolveEquation(Owner.EquationSource, _label, key, out _) is not { } equation) { Preview(); return; }
                if (_view._equationPreview is { } opened) opened.IsOpen = false;
                panel.NavigateConversationEquation(equation);
            }
            else if (Owner._equations.TryGetValue(_label, out var target))
            {
                if (_view._equationPreview is { } preview) preview.IsOpen = false;
                Owner.NavigateEquation(target);
            }
            else _view.ShowEquationPreview(this, null);
        }
    }

    private void ShowEquationPreview(EquationReference link, Paragraph? target, AiEquationTarget? indexed = null)
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
        var local = (target?.Inlines.FirstInline as InlineUIContainer)?.Child is ChatFormulaRow { Child: ChatFormulaHost host } ? host.Formula : null;
        var formula = local?.PreviewCopy() ?? (indexed is null ? null :
            new ChatFormula(indexed.Equation.Source, true, asset: FormulaAsset?.Invoke(indexed.Equation.Source, true)));
        if (formula is not null)
        {
            formula.SetValue(TextElement.FontSizeProperty, local?.GetValue(TextElement.FontSizeProperty) ?? FontSize);
            formula.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            var number = new ChatEquationNumber(link.Number) { Margin = new Thickness(12, 0, 0, 0) };
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
