using System.Windows;
using System.Windows.Controls;
using TeXeed.App.Controls;
using TeXeed.App.Services;
using TeXeed.Core;

namespace TeXeed.App;

public partial class MainWindow
{
    private sealed class PreviewDock
    {
        internal required TabSide Side;
        internal required Dock Edge;
        internal required Grid Pane;
        internal required Grid Body;
        internal required EditorTabStrip Strip;
        internal Grid? Split;
        internal double Share = .5;
    }

    private readonly List<PreviewDock> _previewDocks = [];
    private EditorTabStrip? PreviewDockStrip(TabSide side) => _previewDocks.FirstOrDefault(d => d.Side == side)?.Strip;

    private void AttachPreviewContainer(string key, Grid body)
    {
        var container = PreviewContainerForKey(key);
        if (container.Parent is Panel old && old != body) old.Children.Remove(container);
        if (container.Parent is null) body.Children.Add(container);
    }

    private PreviewDock AddPreviewDock(Dock edge, double share = .5)
    {
        var side = _workspaceTabs.AddGroup();
        var pane = new Grid(); pane.SetResourceReference(BackgroundProperty, "EditorBackground");
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var strip = new EditorTabStrip(); InitializeTabStrip(strip, side);
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = strip };
        border.SetResourceReference(Border.BorderBrushProperty, "Line"); border.SetResourceReference(BackgroundProperty, "SidebarBackground");
        var body = new Grid(); Grid.SetRow(body, 1); pane.Children.Add(border); pane.Children.Add(body);
        pane.PreviewMouseDown += (_, _) => { if (!_updatingWorkspace) _workspaceTabs.Focus(side); };
        var dock = new PreviewDock { Side = side, Edge = edge, Pane = pane, Body = body, Strip = strip, Share = Math.Clamp(share, .1, .9) };
        _previewDocks.Add(dock); return dock;
    }

    private void CreatePreviewDock(string key, Dock edge)
    {
        if (_transitioning || _closing || !TabWorkspace.IsPreview(key) || _workspaceTabs.Owner(key) is null) return;
        if (new[] { TabSide.Left, TabSide.Right }.Concat(_workspaceTabs.ExtraGroups).Sum(side => _workspaceTabs.Tabs(side).Count) <= 1) return;
        CancelPendingNavigation();
        if (TabWorkspace.IsPreview(_workspaceTabs.FocusedTab ?? "")) _followAwaitingSourceInteraction = true;
        var dock = AddPreviewDock(edge);
        _workspaceTabs.Move(key, dock.Side);
        RemoveEmptyPreviewDocks(rebuild: false);
        RebuildPreviewDocks(); RefreshTabs(); ActivateMovedPreviewIfNeeded(dock.Side, key);
    }

    private void RemoveEmptyPreviewDocks(bool rebuild = true)
    {
        var empty = _previewDocks.Where(d => !_workspaceTabs.Has(d.Side)).ToArray();
        if (empty.Length == 0) return;
        foreach (var dock in empty)
        {
            if (dock.Pane.Parent is Panel parent) parent.Children.Remove(dock.Pane);
            _previewDocks.Remove(dock); _workspaceTabs.RemoveGroup(dock.Side);
        }
        if (rebuild) RebuildPreviewDocks();
    }

    private void ResetPreviewDocks()
    {
        foreach (var dock in _previewDocks)
            if (dock.Pane.Parent is Panel parent) parent.Children.Remove(dock.Pane);
        _previewDocks.Clear(); RebuildPreviewDocks();
    }

    private void RebuildPreviewDocks()
    {
        foreach (var dock in _previewDocks)
            if (dock.Split is { } split)
            {
                var horizontal = dock.Edge is Dock.Left or Dock.Right;
                var total = horizontal ? split.ActualWidth : split.ActualHeight;
                var size = horizontal ? dock.Pane.ActualWidth : dock.Pane.ActualHeight;
                if (total > 1 && size > 1) dock.Share = Math.Clamp(size / total, .1, .9);
            }
        if (Workspace.Parent is Panel oldWorkspace) oldWorkspace.Children.Remove(Workspace);
        foreach (var dock in _previewDocks)
            if (dock.Pane.Parent is Panel parent) parent.Children.Remove(dock.Pane);
        DockLayoutRoot.Children.Clear();
        var baseHasTabs = _workspaceTabs.Has(TabSide.Left) || _workspaceTabs.Has(TabSide.Right);
        UIElement child = !baseHasTabs && _previewDocks.Count > 0 ? _previewDocks[0].Pane : Workspace;
        if (!baseHasTabs && _previewDocks.Count > 0) _previewDocks[0].Split = null;
        foreach (var dock in !baseHasTabs && _previewDocks.Count > 0 ? _previewDocks.Skip(1) : _previewDocks)
        {
            var split = new Grid(); dock.Split = split;
            Grid.SetRow(child, 0); Grid.SetColumn(child, 0);
            Grid.SetRow(dock.Pane, 0); Grid.SetColumn(dock.Pane, 0);
            var horizontal = dock.Edge is Dock.Left or Dock.Right;
            var first = dock.Edge is Dock.Left or Dock.Top;
            var extra = new GridLength(dock.Share, GridUnitType.Star);
            var rest = new GridLength(1 - dock.Share, GridUnitType.Star);
            var splitter = new GridSplitter { Background = (System.Windows.Media.Brush)FindResource("Line"), ShowsPreview = false };
            if (horizontal)
            {
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = first ? extra : rest });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = first ? rest : extra });
                splitter.Width = 4; splitter.HorizontalAlignment = HorizontalAlignment.Stretch; splitter.VerticalAlignment = VerticalAlignment.Stretch;
                Grid.SetColumn(dock.Pane, first ? 0 : 2); Grid.SetColumn(child, first ? 2 : 0); Grid.SetColumn(splitter, 1);
            }
            else
            {
                split.RowDefinitions.Add(new RowDefinition { Height = first ? extra : rest });
                split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
                split.RowDefinitions.Add(new RowDefinition { Height = first ? rest : extra });
                splitter.Height = 4; splitter.VerticalAlignment = VerticalAlignment.Stretch; splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetRow(dock.Pane, first ? 0 : 2); Grid.SetRow(child, first ? 2 : 0); Grid.SetRow(splitter, 1);
            }
            split.Children.Add(child); split.Children.Add(dock.Pane); split.Children.Add(splitter); child = split;
        }
        Grid.SetRow(child, 0); Grid.SetColumn(child, 0); DockLayoutRoot.Children.Add(child);
    }

    private PreviewDockState[] CapturePreviewDocks() => _previewDocks.Select(d =>
    {
        var total = d.Edge is Dock.Left or Dock.Right ? d.Split?.ActualWidth ?? 0 : d.Split?.ActualHeight ?? 0;
        var size = d.Edge is Dock.Left or Dock.Right ? d.Pane.ActualWidth : d.Pane.ActualHeight;
        return new PreviewDockState { Edge = d.Edge.ToString(), Share = total > 1 && size > 1 ? Math.Clamp(size / total, .1, .9) : d.Share,
            Tabs = _workspaceTabs.Tabs(d.Side).ToArray(), Active = _workspaceTabs.Active(d.Side), Focused = _workspaceTabs.FocusedSide == d.Side };
    }).ToArray();
}
