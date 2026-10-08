using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

// Workspace arrangement restored on the next launch: window bounds, right column width,
// photo/design mode, the active tab, docked or floating panes, collapsed sections and
// whether the histogram is shown.
// Stored per user in %LOCALAPPDATA%\Morupixel\workspace.json; never part of a document.
public sealed class WorkspaceLayout
{
    public int Version { get; init; } = 1;
    public WindowBounds? Window { get; init; }
    public double RightPanelWidth { get; init; } = 396;
    public bool DesignWorkspace { get; init; }
    public int StudioPage { get; init; }
    public PaneLayout[] Panes { get; init; } = [];
    public string[] CollapsedSections { get; init; } = [];
    public bool ShowHistogram { get; init; }
    public bool RibbonMode { get; init; }
    public bool RibbonCollapsed { get; init; }
    public string? RibbonTab { get; init; }
    public string[]? RibbonFavorites { get; init; }
    /// <summary>Recently used colors, newest first, as #AARRGGBB.</summary>
    public string[]? RecentColors { get; init; }
    /// <summary>Sections that start folded (the tone grid) but the user opened.</summary>
    public string[]? OpenedSections { get; init; }
    /// <summary>사용 목적 (UserProfiles id) the user chose; null until one is chosen.</summary>
    public string? Profile { get; init; }
    /// <summary>Starred hatch patterns, in the order they were starred: built-in keys or "custom:" + ID (LinePatterns.FavoriteKey).</summary>
    public string[]? PatternFavorites { get; init; }
    /// <summary>화면 스타일: "compact" for 간결한 화면; null (or anything else) is 친절한 화면.</summary>
    public string? ScreenStyle { get; init; }
    /// <summary>Width of the 간결한 화면 panel dock; null uses its default.</summary>
    public double? CompactDockWidth { get; init; }
    /// <summary>간결한 화면 panel groups in dock order: their tabs, share of the dock height, folded into the
    /// icon strip or floating in a tool window (with its bounds), and the open tab.</summary>
    public DockGroupLayout[]? DockGroups { get; init; }
    /// <summary>Shape of <see cref="DockGroups"/>: 0/1 (Preview 44) had fixed groups without Tabs; 2 adds
    /// tabs, order and floating. Older shapes are migrated when the layout is loaded.</summary>
    public int DockVersion { get; init; }
}

/// <summary>One 간결한 화면 panel group. Tabs lists its tab keys in order (null in Preview 44 layouts:
/// the group's default tabs); FloatBounds are the floating window's last bounds, kept after docking.</summary>
public sealed record DockGroupLayout(string Key, double Weight, bool Collapsed, string? Tab = null, string[]? Tabs = null, bool Floating = false, WindowBounds? FloatBounds = null);

public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record PaneLayout(string Key, string Location, bool Pinned = false, WindowBounds? Floating = null);

public static class WorkspaceLayoutStore
{
    public static readonly string[] PaneKeys = ["page0", "page1", "page2", "page3", "layers"];
    public const string CompactStyle = "compact";
    /// <summary>The 간결한 화면 dock's default groups: their tabs, share of the dock height and whether they
    /// start folded (MainWindow.Dock.cs builds them in this order; 패널 배치 초기화 returns to it).</summary>
    public static readonly (string Key, string[] Tabs, double Weight, bool Collapsed)[] DockGroupKeys =
    [
        ("color", ["color", "swatches", "gradients", "patterns", "entourage"], 1.2, false), ("properties", ["properties", "adjustments"], 1.25, false),
        ("navigator", ["navigator", "histogram", "info"], .85, false), ("layers", ["layers", "artboards", "history"], 1.4, false), ("tools", ["work", "brush"], 1.2, true)
    ];
    public static readonly string[] DockTabKeys = DockGroupKeys.SelectMany(k => k.Tabs).ToArray();
    public const double MinDockWeight = .05, MaxDockWeight = 20;
    public const int DockLayoutVersion = 2;
    const int MaxStoreBytes = 64 * 1024;
    public const int MaxPatternFavorites = 96;
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "workspace.json");

    public static WorkspaceLayout? Load(string? store = null)
    {
        try
        {
            var file = new FileInfo(store ?? DefaultStorePath);
            if (!file.Exists || file.Length > MaxStoreBytes) return null;
            var layout = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(file.FullName), options);
            return layout == null ? null : Sanitize(layout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public static void Save(WorkspaceLayout layout, string? store = null)
    {
        try
        {
            string path = store ?? DefaultStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Sanitize(layout), options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // Unknown versions, panes and locations are dropped; numbers must be finite and positive.
    public static WorkspaceLayout? Sanitize(WorkspaceLayout layout)
    {
        if (layout.Version != 1) return null;
        var panes = (layout.Panes ?? [])
            .Where(p => p != null && PaneKeys.Contains(p.Key) && p.Location is "right" or "left" or "float")
            .GroupBy(p => p.Key).Select(g => g.First() with { Floating = Bounds(g.First().Floating) }).ToArray();
        var sections = (layout.CollapsedSections ?? []).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 80).Distinct().Take(64).ToArray();
        return new WorkspaceLayout
        {
            Window = Bounds(layout.Window),
            RightPanelWidth = double.IsFinite(layout.RightPanelWidth) ? layout.RightPanelWidth : 396,
            DesignWorkspace = layout.DesignWorkspace,
            StudioPage = Math.Clamp(layout.StudioPage, 0, 3),
            Panes = panes,
            CollapsedSections = sections,
            ShowHistogram = layout.ShowHistogram,
            RibbonMode = layout.RibbonMode,
            RibbonCollapsed = layout.RibbonCollapsed,
            RibbonTab = layout.RibbonTab is { Length: > 0 and <= 80 } tab ? tab : null,
            RibbonFavorites = layout.RibbonFavorites?.Where(f => f is { Length: > 5 and <= 200 } && f.StartsWith("menu:", StringComparison.Ordinal)).Distinct().Take(64).ToArray(),
            RecentColors = layout.RecentColors?.Where(c => c is { Length: 9 } && c[0] == '#' && uint.TryParse(c.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
                .Select(c => c.ToUpperInvariant()).Distinct().Take(ColorPalettePanel.RecentLimit).ToArray(),
            OpenedSections = layout.OpenedSections?.Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 80).Distinct().Take(64).ToArray(),
            Profile = UserProfiles.Find(layout.Profile)?.Id,
            PatternFavorites = layout.PatternFavorites?.Where(LinePatterns.IsFavoriteKey).Distinct(StringComparer.Ordinal).Take(MaxPatternFavorites).ToArray(),
            ScreenStyle = layout.ScreenStyle == CompactStyle ? CompactStyle : null,
            CompactDockWidth = layout.CompactDockWidth is { } dock && double.IsFinite(dock) && dock > 0 && dock <= 4000 ? dock : null,
            DockGroups = SanitizeDockGroups(layout.DockGroups),
            DockVersion = layout.DockGroups == null ? 0 : DockLayoutVersion
        };
    }

    /// <summary>A key the dock can give a group: a default group's, or "group1"… for groups split off later.</summary>
    public static bool IsDockGroupKey(string? key) => key != null && (DockGroupKeys.Any(k => k.Key == key)
        || key.Length is > 5 and <= 8 && key.StartsWith("group", StringComparison.Ordinal) && key[5] != '0' && key[5..].All(char.IsAsciiDigit));

    // Groups keep their order. Unknown keys and tabs are dropped, a tab counts once (first group
    // wins) and an emptied group goes. Preview 44 entries (no Tabs) hold their default tabs, which
    // is the whole migration: their weights, folds and open tabs carry over as they were.
    static DockGroupLayout[]? SanitizeDockGroups(DockGroupLayout[]? groups)
    {
        if (groups == null) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal); var tabs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DockGroupLayout>();
        foreach (var group in groups)
        {
            if (group == null || !IsDockGroupKey(group.Key) || !double.IsFinite(group.Weight) || !keys.Add(group.Key)) continue;
            var own = (group.Tabs ?? DockGroupKeys.FirstOrDefault(k => k.Key == group.Key).Tabs ?? []).Where(t => t != null && DockTabKeys.Contains(t) && tabs.Add(t)).ToArray();
            if (own.Length == 0) continue;
            result.Add(group with
            {
                Weight = Math.Clamp(group.Weight, MinDockWeight, MaxDockWeight),
                Tab = own.Contains(group.Tab) ? group.Tab : null,
                Tabs = own,
                Collapsed = group.Collapsed && !group.Floating,
                FloatBounds = Bounds(group.FloatBounds)
            });
        }
        return result.ToArray();
    }

    /// <summary>The default arrangement (패널 배치 초기화).</summary>
    public static DockGroupLayout[] DefaultDockGroups() => DockGroupKeys.Select(k => new DockGroupLayout(k.Key, k.Weight, k.Collapsed, null, k.Tabs)).ToArray();

    /// <summary>Every dock tab exactly once: tabs a saved arrangement does not place go back to their
    /// default group, which comes back at its default place when the arrangement has none.</summary>
    public static DockGroupLayout[] CompleteDockGroups(DockGroupLayout[]? groups)
    {
        var result = (SanitizeDockGroups(groups) ?? DefaultDockGroups()).ToList();
        var placed = result.SelectMany(g => g.Tabs!).ToHashSet(StringComparer.Ordinal);
        for (int d = 0; d < DockGroupKeys.Length; d++)
        {
            var (key, defaults, weight, collapsed) = DockGroupKeys[d];
            var missing = defaults.Where(t => !placed.Contains(t)).ToArray();
            if (missing.Length == 0) continue;
            placed.UnionWith(missing);
            int index = result.FindIndex(g => g.Key == key);
            if (index >= 0) { result[index] = result[index] with { Tabs = [.. result[index].Tabs!, .. missing] }; continue; }
            // Before the first group that comes later in the default order.
            var later = DockGroupKeys.Skip(d + 1).Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
            int at = result.FindIndex(g => later.Contains(g.Key));
            result.Insert(at < 0 ? result.Count : at, new DockGroupLayout(key, weight, collapsed, null, missing));
        }
        return result.ToArray();
    }

    static WindowBounds? Bounds(WindowBounds? b) => b != null && double.IsFinite(b.Left) && double.IsFinite(b.Top)
        && double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width >= 100 && b.Height >= 100 && b.Width <= 20000 && b.Height <= 20000 ? b : null;
}
