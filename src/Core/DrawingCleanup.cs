using System.Windows.Media;

namespace Compositor.Windows;

// Role of a CAD layer in a plan drawing. Structure reads darkest and thickest;
// furniture and annotation step back so the plan hierarchy is legible.
public enum DrawingRole { Structure, Opening, Furniture, Annotation, Hatch, Other }

// How hatches are treated on import: boundaries only, a recommended material per
// hatch, or one user-supplied material image for every hatch.
// Pattern: each hatch becomes the matching built-in line pattern (sand, lawn, brick, …).
public enum HatchTreatment { Keep, Suggest, Image, Pattern }

public enum MaterialKind { Concrete, Brick, Wood, Tile, Stone, Insulation, Gravel, Diagonal, Solid }

public sealed record CadCleanup(bool LineWeights = true, HatchTreatment Hatches = HatchTreatment.Suggest, string? MaterialImage = null,
    IReadOnlyDictionary<string, DrawingRole>? Roles = null)
{
    public DrawingRole RoleFor(string layer) =>
        Roles != null && Roles.TryGetValue(layer, out var role) ? role : DrawingCleanup.Classify(layer);
}

public sealed record CadLayerInfo(string Name, DrawingRole Role, int Objects, int Hatches);
public sealed record CadDrawingInfo(IReadOnlyList<CadLayerInfo> Layers, IReadOnlyDictionary<MaterialKind, int> HatchMaterials,
    IReadOnlyDictionary<HatchPattern, int>? HatchPatternCounts = null);

public static class DrawingCleanup
{
    // Output line weight in document pixels and line tone for each role.
    public static double Weight(DrawingRole role) => role switch
    {
        DrawingRole.Structure => 2.6, DrawingRole.Opening => 1.3, DrawingRole.Furniture => .7,
        DrawingRole.Annotation => .6, DrawingRole.Hatch => .5, _ => 1.0
    };

    public static Color Tone(DrawingRole role) => role switch
    {
        DrawingRole.Structure => Color.FromRgb(0x14, 0x16, 0x1A), DrawingRole.Opening => Color.FromRgb(0x33, 0x38, 0x40),
        DrawingRole.Furniture => Color.FromRgb(0x9A, 0xA0, 0xA8), DrawingRole.Annotation => Color.FromRgb(0x7A, 0x80, 0x8A),
        DrawingRole.Hatch => Color.FromRgb(0xB4, 0xB8, 0xBE), _ => Color.FromRgb(0x4A, 0x50, 0x58)
    };

    public static string RoleName(DrawingRole role) => role switch
    {
        DrawingRole.Structure => "구조·벽 (진하게)", DrawingRole.Opening => "창호·문", DrawingRole.Furniture => "가구·집기 (연하게)",
        DrawingRole.Annotation => "치수·문자", DrawingRole.Hatch => "해치·마감", _ => "기타"
    };

    // "구조·벽 3, 창호·문 1": roles are separated by commas (role names themselves contain "·")
    // and each count is tied to its role with a no-break space. For text shown as it is, unbreakable
    // also joins every character with a word joiner, so a line wraps only between roles.
    public static string RoleSummary(IEnumerable<DrawingRole> roles, Func<string, string>? translate = null, bool unbreakable = false) =>
        string.Join(", ", roles.GroupBy(r => r).OrderBy(g => g.Key).Select(g =>
        {
            string item = (translate ?? (s => s))(RoleName(g.Key).Split(' ')[0]).Replace(' ', '\u00A0') + "\u00A0" + g.Count().ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            return unbreakable ? string.Join("\u2060", item.EnumerateRunes()) : item;
        }));

    public static string MaterialName(MaterialKind kind) => kind switch
    {
        MaterialKind.Concrete => "콘크리트", MaterialKind.Brick => "벽돌", MaterialKind.Wood => "목재 마루", MaterialKind.Tile => "타일",
        MaterialKind.Stone => "석재", MaterialKind.Insulation => "단열재", MaterialKind.Gravel => "자갈·토사", MaterialKind.Diagonal => "사선 패턴", _ => "단색 채움"
    };

    static readonly (DrawingRole Role, string[] Keys)[] rules =
    [
        (DrawingRole.Hatch, ["HATCH", "PATT", "PAT", "해치", "마감", "FINISH", "FLOR-PATT"]),
        (DrawingRole.Annotation, ["DIM", "TEXT", "TXT", "ANNO", "NOTE", "TAG", "SYMB", "GRID", "AXIS", "LEADER", "TITLE", "치수", "문자", "주석", "기호", "그리드", "통심"]),
        (DrawingRole.Furniture, ["FURN", "FUR", "EQPM", "EQUIP", "FIXT", "APPL", "SANI", "PLUMB", "PLNT", "LAND", "CASE", "가구", "집기", "위생", "설비", "기구", "조경"]),
        (DrawingRole.Opening, ["DOOR", "WIND", "WIN", "GLAZ", "OPEN", "CURT", "창호", "창문", "창", "문", "개구"]),
        (DrawingRole.Structure, ["WALL", "CONC", "COLS", "COL", "COLUMN", "STRS", "STRU", "STR", "BEAM", "SLAB", "CORE", "MASN", "벽", "콘크리트", "기둥", "구조", "옹벽", "슬래브", "조적"])
    ];

    // Keyword match on the layer name (AIA-style "A-WALL-FULL", Korean names), longest first per role order.
    public static DrawingRole Classify(string layer)
    {
        string name = layer.ToUpperInvariant();
        int bar = name.LastIndexOf('|'); if (bar >= 0) name = name[(bar + 1)..];
        foreach (var (role, keys) in rules)
            foreach (var key in keys)
                if (Matches(name, key)) return role;
        return DrawingRole.Other;
    }

    static bool Matches(string name, string key)
    {
        if (key.Any(c => c >= '가' && c <= '힣')) return name.Contains(key, StringComparison.Ordinal);
        // Latin keys match whole tokens or token prefixes (A-WALL, WALL_EXT, CONC1) so "WINDOW" is not "WIN" + noise.
        foreach (var token in name.Split(['-', '_', ' ', '.', '$', '|', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith(key, StringComparison.Ordinal) && (token.Length == key.Length || key.Length >= 3)) return true;
        return false;
    }

    // Recommended material from the hatch pattern name first, then the layer name and role.
    public static MaterialKind Suggest(string pattern, string layer, DrawingRole role)
    {
        string p = pattern.ToUpperInvariant(), l = layer.ToUpperInvariant();
        if (p == "SOLID") return MaterialKind.Solid;
        foreach (var (kind, keys) in materialRules)
            if (keys.Any(key => p.Contains(key, StringComparison.Ordinal))) return kind;
        foreach (var (kind, keys) in materialRules)
            if (keys.Any(key => l.Contains(key, StringComparison.Ordinal))) return kind;
        return role == DrawingRole.Structure ? MaterialKind.Concrete : MaterialKind.Diagonal;
    }

    // Line pattern for a hatch: the pattern name first, then the layer name. SOLID stays a solid fill (null).
    // Layer names match whole tokens (OUTLINE is not LINE), like layer roles.
    public static HatchPattern? SuggestPattern(string pattern, string layer, DrawingRole role)
    {
        string p = pattern.ToUpperInvariant(), l = layer.ToUpperInvariant();
        if (p == "SOLID") return null;
        foreach (var (kind, keys) in patternRules)
            if (keys.Any(key => p.Contains(key, StringComparison.Ordinal))) return kind;
        foreach (var (kind, keys) in patternRules)
            if (keys.Any(key => Matches(l, key))) return kind;
        return role == DrawingRole.Structure ? HatchPattern.Concrete : HatchPattern.Diagonal;
    }

    // Pattern and layer name keywords that choose this pattern on CAD import.
    public static IReadOnlyList<string> PatternKeywords(HatchPattern pattern) => patternRules.Where(r => r.Kind == pattern).SelectMany(r => r.Keys).ToArray();

    static readonly (HatchPattern Kind, string[] Keys)[] patternRules =
    [
        (HatchPattern.Concrete, ["CONC", "콘크리트", "몰탈", "MORTAR"]),
        (HatchPattern.Sand, ["SAND", "모래"]),
        (HatchPattern.Gravel, ["GRAVEL", "EARTH", "자갈", "토사", "흙"]),
        (HatchPattern.GrassSparse, ["GRASS", "LAWN", "TURF", "잔디", "PLNT", "조경"]),
        (HatchPattern.Meadow, ["MEADOW", "풀밭"]),
        (HatchPattern.Brick, ["BRICK", "BRSTD", "B816", "B88", "벽돌", "조적", "MASN"]),
        (HatchPattern.Cobble, ["COBBLE", "자연석"]),
        (HatchPattern.Flagstone, ["STONE", "MARBLE", "GRANITE", "RSHKE", "FLAG", "석재", "판석", "대리석", "화강"]),
        (HatchPattern.PavingSmall, ["PAVE", "PAVING", "포장", "보도"]),
        (HatchPattern.Dots, ["DOTS", "점무늬"]),
        (HatchPattern.Lines, ["WOOD", "PARQ", "HBONE", "FLOOR", "목재", "마루", "원목", "합판", "LINE"]),
        (HatchPattern.Crosshatch, ["ANSI37", "CROSS", "NET", "교차"]),
        (HatchPattern.Grid, ["TILE", "SQUARE", "BOX", "GRID", "타일"]),
        (HatchPattern.Insulation, ["INSUL", "BATT", "단열"]),
        (HatchPattern.Diagonal, ["ANSI31", "DIAG", "사선"])
    ];

    static readonly (MaterialKind Kind, string[] Keys)[] materialRules =
    [
        (MaterialKind.Concrete, ["CONC", "콘크리트", "몰탈", "MORTAR"]),
        (MaterialKind.Brick, ["BRICK", "BRSTD", "B816", "B88", "벽돌", "조적", "MASN"]),
        (MaterialKind.Wood, ["WOOD", "PARQ", "HBONE", "FLOOR", "목재", "마루", "원목", "합판"]),
        (MaterialKind.Tile, ["TILE", "SQUARE", "BOX", "NET", "타일", "GRID"]),
        (MaterialKind.Stone, ["STONE", "MARBLE", "GRANITE", "RSHKE", "석재", "대리석", "화강"]),
        (MaterialKind.Insulation, ["INSUL", "BATT", "단열"]),
        (MaterialKind.Gravel, ["EARTH", "GRAVEL", "SAND", "자갈", "토사", "모래", "흙"])
    ];
}
