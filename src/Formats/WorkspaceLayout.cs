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
}

public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record PaneLayout(string Key, string Location, bool Pinned = false, WindowBounds? Floating = null);

public static class WorkspaceLayoutStore
{
    public static readonly string[] PaneKeys = ["page0", "page1", "page2", "page3", "layers"];
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
        static WindowBounds? Bounds(WindowBounds? b) => b != null && double.IsFinite(b.Left) && double.IsFinite(b.Top)
            && double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width >= 100 && b.Height >= 100 && b.Width <= 20000 && b.Height <= 20000 ? b : null;
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
            PatternFavorites = layout.PatternFavorites?.Where(LinePatterns.IsFavoriteKey).Distinct(StringComparer.Ordinal).Take(MaxPatternFavorites).ToArray()
        };
    }
}
