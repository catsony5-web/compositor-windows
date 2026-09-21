using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    readonly StackPanel leftPanels = new();
    readonly ColumnDefinition leftPanelColumn = new() { Width = new GridLength(0) };
    readonly ColumnDefinition rightPanelColumn = new() { Width = new GridLength(396), MinWidth = 324, MaxWidth = 520 };
    readonly Border layersSlot = new();
    readonly List<StudioPane> movablePanels = [];
    StudioPane[] studioPanes = [];
    StudioPane? layersPane;
    bool closingPanels;

    StudioPane CreatePane(string name, int page, UIElement content)
    {
        var pane = new StudioPane(name, page, content, MovePane, DragPane); movablePanels.Add(pane); return pane;
    }
    void RemovePane(StudioPane pane)
    {
        if (ReferenceEquals(studioScroll.Content, pane)) studioScroll.Content = null;
        if (ReferenceEquals(layersSlot.Child, pane)) layersSlot.Child = null;
        leftPanels.Children.Remove(pane);
        if (pane.Floating is { } floating) { pane.Floating = null; floating.Content = null; floating.Close(); }
        pane.Height = double.NaN;
    }
    void MovePane(StudioPane pane, string destination)
    {
        if (pane.Pinned || pane.Location == destination && destination != "float") return;
        CommitFocusedInspectorField(); RemovePane(pane); pane.Location = destination;
        if (destination == "left")
        { pane.Height = pane.Page < 0 ? 440 : 600; pane.Margin = new Thickness(0, 0, 6, 6); leftPanels.Children.Add(pane); }
        else if (destination == "right")
        { pane.Margin = new Thickness(0); if (pane.Page < 0) layersSlot.Child = pane; }
        else
        {
            pane.Margin = new Thickness(0);
            var floating = new Window { Title = "Morupixel · " + pane.Caption, Owner = this, Content = pane, Width = 400, Height = pane.Page < 0 ? 560 : pane.Page is 1 or 2 ? 780 : 660, MinWidth = 340, MinHeight = 320,
                FontFamily = Theme.UiFont, FontSize = Theme.BodySize, UseLayoutRounding = true, SnapsToDevicePixels = true,
                Background = Theme.Header, Foreground = Theme.Text, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            pane.Floating = floating;
            floating.Closing += (_, e) =>
            {
                if (pane.Floating != floating || closingPanels) return;
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() => { pane.Unlock(); MovePane(pane, "right"); }));
            };
            floating.Show();
        }
        leftPanelColumn.Width = new GridLength(leftPanels.Children.Count > 0 ? 370 : 0);
        ShowStudioPage(studioPage, false);
        if (layersPane != null && layersPane.Location != "right")
            layersSlot.Child = new Border { Child = Theme.Button("레이어 패널을 오른쪽으로", () => { layersPane.Unlock(); MovePane(layersPane, "right"); }), VerticalAlignment = VerticalAlignment.Top };
    }
    void DragPane(StudioPane pane, Point screen)
    {
        if (pane.Pinned) return;
        if (pane.Location != "float") MovePane(pane, "float");
        if (pane.Floating is not { } window) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        window.Left = screen.X / dpi.DpiScaleX - 80; window.Top = screen.Y / dpi.DpiScaleY - 40;
        try { window.DragMove(); } catch (InvalidOperationException) { return; }
        var origin = PointToScreen(new Point(0, 0));
        double x = window.Left * dpi.DpiScaleX - origin.X, y = window.Top * dpi.DpiScaleY - origin.Y;
        if (y > -40 && y < ActualHeight * dpi.DpiScaleY && x > -160 && x < 180) MovePane(pane, "left");
        else if (y > -40 && y < ActualHeight * dpi.DpiScaleY && x > (ActualWidth - 380) * dpi.DpiScaleX && x < ActualWidth * dpi.DpiScaleX) MovePane(pane, "right");
    }
    void ResetPanelLayout()
    {
        foreach (var pane in movablePanels) { pane.Unlock(); if (pane.Location != "right") MovePane(pane, "right"); }
        ShowStudioPage(0);
    }
    void CloseFloatingPanels()
    {
        closingPanels = true;
        foreach (var pane in movablePanels) { if (pane.Floating is { } floating) { pane.Floating = null; floating.Close(); } }
    }
}
