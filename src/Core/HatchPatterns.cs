namespace Compositor.Windows;

// Built-in line-art hatch patterns for drawings (lawn, sand, pavers, brick, …). The values are
// stored in documents through the asset ID, so they are explicit and append-only.
public enum HatchPattern
{
    GrassSparse = 0, Meadow = 1, Sand = 2, DotsSparse = 3, DashDiagonal = 4, DashHorizontal = 5, Dots = 6, PavingSmall = 7,
    Lines = 8, Stipple = 9, Flagstone = 10, Cobble = 11, Gravel = 12, Concrete = 13, Brick = 14, Diagonal = 15, Crosshatch = 16,
    Grid = 17, Insulation = 18
}

// Catalog of the built-in patterns: keys, Korean names and stable material IDs. A pattern is an
// ordinary MaterialAsset with a reserved ID and Source; the engine (HatchPatternRenderer) draws it.
public static class HatchPatterns
{
    public const string SourcePrefix = "morupixel:pattern/";
    public const int GeneratorVersion = 1;
    public const uint DefaultInk = 0xFF262626;
    // Asset names are a Korean constant (asset identity compares names); the UI translates on display.
    public const string NamePrefix = "패턴 · ";

    static readonly HatchPattern[] all = Enum.GetValues<HatchPattern>().OrderBy(p => (int)p).ToArray();
    public static IReadOnlyList<HatchPattern> All => all;

    public static string Key(HatchPattern p) => p switch
    {
        HatchPattern.GrassSparse => "grass-sparse", HatchPattern.Meadow => "meadow", HatchPattern.Sand => "sand",
        HatchPattern.DotsSparse => "dots-sparse", HatchPattern.DashDiagonal => "dash-diagonal", HatchPattern.DashHorizontal => "dash-horizontal",
        HatchPattern.Dots => "dots", HatchPattern.PavingSmall => "paving-small", HatchPattern.Lines => "lines", HatchPattern.Stipple => "stipple",
        HatchPattern.Flagstone => "flagstone", HatchPattern.Cobble => "cobble", HatchPattern.Gravel => "gravel", HatchPattern.Concrete => "concrete",
        HatchPattern.Brick => "brick", HatchPattern.Diagonal => "diagonal", HatchPattern.Crosshatch => "crosshatch", HatchPattern.Grid => "grid",
        HatchPattern.Insulation => "insulation", _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    // Korean display name; also the translation key.
    public static string Name(HatchPattern p) => p switch
    {
        HatchPattern.GrassSparse => "잔디", HatchPattern.Meadow => "풀밭", HatchPattern.Sand => "모래", HatchPattern.DotsSparse => "성긴 점",
        HatchPattern.DashDiagonal => "짧은 빗금", HatchPattern.DashHorizontal => "짧은 가로선", HatchPattern.Dots => "둥근 점",
        HatchPattern.PavingSmall => "작은 포장석", HatchPattern.Lines => "가로줄", HatchPattern.Stipple => "고운 점", HatchPattern.Flagstone => "판석",
        HatchPattern.Cobble => "자연석", HatchPattern.Gravel => "자갈", HatchPattern.Concrete => "콘크리트", HatchPattern.Brick => "벽돌",
        HatchPattern.Diagonal => "사선", HatchPattern.Crosshatch => "교차 사선", HatchPattern.Grid => "사각 격자", HatchPattern.Insulation => "단열재",
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    // Repeat size relative to the default material size. Large stones hold four times the cells in a
    // repeat twice as wide, so the stones keep their size and the repeat does not read as a lattice.
    public static double RepeatScale(HatchPattern p) => p is HatchPattern.Flagstone or HatchPattern.Cobble ? 2 : 1;

    public static string AssetName(HatchPattern p) => NamePrefix + Name(p);
    public static string Source(HatchPattern p) => SourcePrefix + Key(p) + "@" + GeneratorVersion;
    public static Guid StableId(HatchPattern p) =>
        new(0x4d6f7275, 0x7069, 0x7865, (byte)'l', (byte)'p', (byte)'a', (byte)'t', (byte)'t', (byte)'n', (byte)GeneratorVersion, (byte)p);

    public static bool TryGet(Guid id, out HatchPattern p)
    {
        foreach (var candidate in all)
            if (StableId(candidate) == id) { p = candidate; return true; }
        p = default; return false;
    }

    // A built-in pattern needs both its reserved ID and its Source; a spoofed Source alone is an image.
    public static bool TryGet(MaterialAsset? asset, out HatchPattern p)
    {
        if (asset != null && TryGet(asset.Id, out p) && asset.Source == Source(p)) return true;
        p = default; return false;
    }

    public static bool TryParseKey(string? key, out HatchPattern p)
    {
        foreach (var candidate in all)
            if (string.Equals(Key(candidate), key, StringComparison.Ordinal)) { p = candidate; return true; }
        p = default; return false;
    }

    // Built-in swatches (presets and patterns) show translated names; a user's image keeps its own.
    public static bool IsBuiltInMaterial(MaterialAsset asset) =>
        asset.Source.StartsWith("morupixel:preset/", StringComparison.Ordinal) || TryGet(asset, out _);
}
