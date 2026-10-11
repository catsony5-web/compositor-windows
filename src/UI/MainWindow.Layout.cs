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
        // 간결한 화면 docks every pane; the friendly placements it will restore are the ones to keep.
        if (screenCompact) panes = friendlyPanes.Where(p => p.Location != "right" || p.Pinned).Select(p => new PaneLayout(PaneKey(p.Pane), p.Location, p.Pinned,
            p.Bounds is { } b ? new WindowBounds(b.Left, b.Top, b.Width, b.Height, false) : null)).ToArray();
        return new WorkspaceLayout
        {
            Window = window,
            RightPanelWidth = FriendlyPanelWidthNow,
            DesignWorkspace = designWorkspace,
            StudioPage = studioPage,
            Panes = panes,
            CollapsedSections = SectionHeader.CollapsedKeys,
            ShowHistogram = showHistogram,
            RibbonMode = ribbonMode, RibbonCollapsed = ribbonCollapsed, RibbonTab = ribbonTab, RibbonFavorites = ribbonFavorites.ToArray(),
            RecentColors = ColorPalettePanel.RecentColors.Select(c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}").ToArray(),
            OpenedSections = SectionHeader.OpenedDefaultKeys,
            Profile = userProfileChosen ? userProfile.Id : null,
            PatternFavorites = patternFavorites.ToArray(),
            ScreenStyle = screenCompact ? WorkspaceLayoutStore.CompactStyle : null,
            CompactDockWidth = Math.Round(CompactDockWidthNow, 1),
            DockGroups = CaptureDockGroups(),
            DockVersion = WorkspaceLayoutStore.DockLayoutVersion
        };
    }

    internal void ApplyPaneLayout(WorkspaceLayout layout)
    {
        // Panes are placed on the friendly screen; 간결한 화면 (if saved) docks them at the end.
        if (screenCompact) SetScreenStyle(false);
        rightPanelColumn.Width = new GridLength(Math.Clamp(layout.RightPanelWidth, rightPanelColumn.MinWidth, rightPanelColumn.MaxWidth));
        // The saved purpose first, without its default mode: the saved mode below wins.
        if (UserProfiles.Find(layout.Profile) is { } profile) SetUserProfile(profile.Id, pickedByUser: false);
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
        SectionHeader.SetCollapsedKeys(layout.CollapsedSections, layout.OpenedSections);
        if (layout.RecentColors != null) { ColorPalettePanel.SetRecent(layout.RecentColors.Select(ColorPalettePanel.ParseStored)); studioPalette?.RefreshRecent(); }
        SetHistogramVisible(layout.ShowHistogram);
        ribbonMode = layout.RibbonMode; ribbonCollapsed = layout.RibbonCollapsed;
        if (layout.RibbonTab != null && RibbonTabNames().Contains(layout.RibbonTab)) ribbonTab = layout.RibbonTab;
        if (layout.RibbonFavorites != null) ribbonFavorites = MigrateFavorites(layout.RibbonFavorites);
        if (layout.PatternFavorites != null) patternFavorites = layout.PatternFavorites.Where(LinePatterns.IsFavoriteKey).Distinct(StringComparer.Ordinal).ToList();
        RebuildRibbon();
        if (layout.CompactDockWidth is { } dock) compactDockWidth = Math.Clamp(dock, MinDockWidth, MaxDockWidth);
        ApplyDockGroups(layout.DockGroups);
        if (layout.ScreenStyle == WorkspaceLayoutStore.CompactStyle) SetScreenStyle(true);
    }

    // Keeps a window (and its minimum size) inside the work area of the screen it opens on,
    // so small or scaled displays never cut off its edges.
    internal static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width); window.MinHeight = Math.Min(window.MinHeight, area.Height);
        if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, area.Width);
        if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, area.Height);
        window.MaxHeight = Math.Min(window.MaxHeight, area.Height); window.MaxWidth = Math.Min(window.MaxWidth, area.Width);
        if (double.IsFinite(window.Left) && double.IsFinite(window.Top) && window.WindowState == WindowState.Normal)
        {
            double width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width, height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
            window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - width));
            window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        }
    }

    // Every owned window (dialogs, floating panels, palette) is fitted once it has its size.
    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(Window), LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { Owner: not null } owned && sender is not MainWindow) FitToWorkArea(owned);
        }));
    }

    void SaveWorkspace()
    {
        if (persistWorkspace) WorkspaceLayoutStore.Save(CaptureLayout());
    }
}
