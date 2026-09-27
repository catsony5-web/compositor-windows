using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    WorkspaceLayout? savedLayout;
    bool persistWorkspace;

    // Called only by the application entry point. Headless checks and offscreen previews
    // construct windows without it, so they never read or write the user's layout.
    internal void RestoreWorkspace()
    {
        persistWorkspace = true;
        savedLayout = WorkspaceLayoutStore.Load();
        if (savedLayout?.Window is { } bounds) ApplyWindowBounds(bounds);
    }

    static Rect VirtualScreen() => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    // A window saved on a monitor that is no longer attached falls back to the default placement.
    static bool OnScreen(Rect rect, double minWidth, double minHeight)
    {
        var visible = Rect.Intersect(VirtualScreen(), rect);
        return !visible.IsEmpty && visible.Width >= minWidth && visible.Height >= minHeight;
    }

    void ApplyWindowBounds(WindowBounds bounds)
    {
        var rect = new Rect(bounds.Left, bounds.Top, Math.Max(MinWidth, bounds.Width), Math.Max(MinHeight, bounds.Height));
        if (!OnScreen(rect, 240, 160)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left; Top = rect.Top; Width = rect.Width; Height = rect.Height;
        if (bounds.Maximized) WindowState = WindowState.Maximized;
    }

    string PaneKey(StudioPane pane) => ReferenceEquals(pane, layersPane) ? "layers" : "page" + pane.Page;

    internal WorkspaceLayout CaptureLayout()
    {
        WindowBounds? window = null;
        if (IsLoaded)
        {
            var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!normal.IsEmpty) window = new WindowBounds(normal.Left, normal.Top, normal.Width, normal.Height, WindowState == WindowState.Maximized);
        }
        var panes = movablePanels.Where(p => p.Location != "right" || p.Pinned).Select(p => new PaneLayout(PaneKey(p), p.Location, p.Pinned,
            p.Floating is { } f ? new WindowBounds(f.Left, f.Top, f.ActualWidth > 0 ? f.ActualWidth : f.Width, f.ActualHeight > 0 ? f.ActualHeight : f.Height, false) : null)).ToArray();
        return new WorkspaceLayout
        {
            Window = window,
            RightPanelWidth = rightPanelColumn.ActualWidth > 0 ? rightPanelColumn.ActualWidth : rightPanelColumn.Width.Value,
            DesignWorkspace = designWorkspace,
            StudioPage = studioPage,
            Panes = panes
        };
    }

    internal void ApplyPaneLayout(WorkspaceLayout layout)
    {
        rightPanelColumn.Width = new GridLength(Math.Clamp(layout.RightPanelWidth, rightPanelColumn.MinWidth, rightPanelColumn.MaxWidth));
        if (layout.DesignWorkspace != designWorkspace) SetWorkspaceMode(layout.DesignWorkspace);
        foreach (var saved in layout.Panes)
        {
            var pane = movablePanels.FirstOrDefault(p => PaneKey(p) == saved.Key);
            if (pane == null) continue;
            if (saved.Location != "right" && pane.Location != saved.Location) MovePane(pane, saved.Location);
            if (saved.Location == "float" && pane.Floating is { } floating && saved.Floating is { } bounds)
            {
                var rect = new Rect(bounds.Left, bounds.Top, Math.Max(floating.MinWidth, bounds.Width), Math.Max(floating.MinHeight, bounds.Height));
                if (OnScreen(rect, 120, 80)) { floating.Left = rect.Left; floating.Top = rect.Top; floating.Width = rect.Width; floating.Height = rect.Height; }
            }
            if (saved.Pinned) pane.SetPinned(true);
        }
        ShowStudioPage(Math.Clamp(layout.StudioPage, 0, 3), false);
    }

    void SaveWorkspace()
    {
        if (persistWorkspace) WorkspaceLayoutStore.Save(CaptureLayout());
    }
}
