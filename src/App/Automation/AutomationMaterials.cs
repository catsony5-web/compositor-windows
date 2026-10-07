using System.Text.Json.Nodes;
using System.Windows;

namespace Compositor.Windows;

public static class AutomationMaterials
{
    /// <summary>A built-in key (brick) or "custom:&lt;32 hex&gt;" for a user line pattern; null for an image material.</summary>
    public static string? PatternId(MaterialAsset asset) => LinePatterns.IsPattern(asset) ? LinePatterns.FavoriteKey(asset) : null;
    /// <summary>The palette group of a pattern: basic (line hatches), screentone or custom; null for an image material.</summary>
    public static string? PatternGroup(MaterialAsset asset) => HatchPatterns.TryGet(asset, out var p) ? Group(p) : LinePatterns.IsCustom(asset) ? "custom" : null;
    static string Group(HatchPattern p) => HatchPatterns.IsScreentone(p) ? "screentone" : "basic";

    // Gradient parameters of apply_material/update_material (dot-gradient and stipple-gradient only).
    public static readonly string[] GradientFields = ["gradientAngle", "gradientStart", "gradientEnd", "gradientSeed"];
    public const string GradientOnly = "gradientAngle, gradientStart, gradientEnd and gradientSeed apply to dot-gradient and stipple-gradient only.";
    public static bool HasGradient(JsonObject arguments) => GradientFields.Any(arguments.ContainsKey);
    public static JsonObject Gradient(ToneGradient gradient) => new()
    {
        ["angle"] = gradient.Angle, ["start"] = gradient.Start, ["end"] = gradient.End, ["seed"] = gradient.Seed,
        ["meaning"] = "Ink coverage runs linearly from start (0-1) at the region's first edge to end at its last edge along angle (degrees, 0 = left to right, 90 = top to bottom)."
    };

    public static JsonObject Asset(MaterialAsset asset)
    {
        bool builtIn = HatchPatterns.TryGet(asset, out _), custom = LinePatterns.IsCustom(asset);
        return new()
        {
            ["materialId"] = asset.Id.ToString(), ["name"] = asset.Name, ["width"] = asset.Pixels.Width, ["height"] = asset.Pixels.Height,
            ["source"] = asset.Source, ["tileable"] = asset.Tileable, ["kind"] = builtIn || custom ? "pattern" : "image", ["patternId"] = PatternId(asset),
            ["patternKind"] = builtIn ? "builtin" : custom ? "custom" : null, ["patternGroup"] = PatternGroup(asset),
            ["tileableMeaning"] = HatchPatterns.IsGradient(asset) ? "Built-in gradient screentone: its density runs across each region it fills, redrawn at display and export resolution; the stored tile is a fallback for older versions."
                : builtIn ? "Built-in seamless pattern, redrawn from vector geometry at display and export resolution."
                : custom ? "User line pattern converted from an image; its coverage tile is recolored with ink, line weight and background at display and export resolution."
                : "Caller-declared; no seamless-image generation or verification is performed."
        };
    }
    public static string Background(uint background) => background >> 24 == 0 ? "none" : $"#{background:X8}";
    /// <summary>"none" → 0; "#RRGGBB" (opaque) or "#AARRGGBB" → ARGB; a zero alpha also means none.</summary>
    public static uint ParseBackground(string text)
    {
        if (text == "none") return 0;
        string hex = text.TrimStart('#');
        uint value = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        value = hex.Length == 6 ? 0xFF000000 | value : value;
        return value >> 24 == 0 ? 0 : value;
    }
    public static string Ink(uint ink) => ink == 0 ? "default" : $"#{ink:X8}";
    /// <summary>"default" → 0; "#RRGGBB" (opaque) or "#AARRGGBB" → ARGB. A zero alpha is refused: 0 means default ink.</summary>
    public static uint ParseInk(string text)
    {
        if (text == "default") return 0;
        string hex = text.TrimStart('#');
        uint value = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        value = hex.Length == 6 ? 0xFF000000 | value : value;
        if (value >> 24 == 0) throw new ArgumentException("Invalid ink: a fully transparent ink draws nothing; use a nonzero alpha or default.");
        return value;
    }
    // Built-in hatch patterns in the order suited to a surface (general: catalog order), then the user's
    // line patterns: the 내 패턴 library first, then those only the document carries (the palette's order).
    public static JsonObject Patterns(string? nameContains, string surface, IReadOnlyList<MaterialAsset>? library = null,
        IReadOnlyList<MaterialAsset>? documentPatterns = null, IReadOnlyCollection<string>? favorites = null)
    {
        var hint = surface switch { "wall" => SurfaceHint.Wall, "floor" => SurfaceHint.Floor, "ground" => SurfaceHint.Ground, _ => SurfaceHint.General };
        bool Favorite(string key) => favorites?.Contains(key) == true;
        var builtIn = SelectionMaterials.PatternOrder(hint).Select(p => new JsonObject
        {
            ["patternId"] = HatchPatterns.Key(p), ["materialId"] = HatchPatterns.StableId(p).ToString(), ["kind"] = "builtin", ["name"] = HatchPatterns.Name(p),
            ["displayName"] = Loc.T(HatchPatterns.Name(p)), ["defaultInk"] = Ink(HatchPatterns.DefaultInk), ["favorite"] = Favorite(HatchPatterns.Key(p)),
            ["group"] = Group(p), ["coverage"] = HatchPatterns.Coverage(p), ["gradient"] = HatchPatterns.IsGradient(p),
            ["surfaces"] = new JsonArray(new[] { (SurfaceHint.Wall, "wall"), (SurfaceHint.Floor, "floor"), (SurfaceHint.Ground, "ground") }
                .Where(s => SelectionMaterials.LeadPatterns(s.Item1).Contains(p)).Select(s => (JsonNode?)JsonValue.Create(s.Item2)).ToArray()),
            ["cadKeywords"] = new JsonArray(DrawingCleanup.PatternKeywords(p).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray())
        });
        var mine = (library ?? []).Concat((documentPatterns ?? []).Where(a => library?.All(p => p.Id != a.Id) != false)).Where(LinePatterns.IsCustom)
            .Select(a => new JsonObject
            {
                ["patternId"] = LinePatterns.FavoriteKey(a), ["materialId"] = a.Id.ToString(), ["kind"] = "custom", ["name"] = a.Name, ["displayName"] = a.Name,
                ["defaultInk"] = Ink(HatchPatterns.DefaultInk), ["favorite"] = Favorite(LinePatterns.FavoriteKey(a)),
                ["group"] = "custom", ["coverage"] = null, ["gradient"] = false,
                ["inLibrary"] = library?.Any(p => p.Id == a.Id) == true, ["inDocument"] = documentPatterns?.Any(p => p.Id == a.Id) == true,
                ["width"] = a.Pixels.Width, ["height"] = a.Pixels.Height, ["surfaces"] = new JsonArray(), ["cadKeywords"] = new JsonArray()
            });
        var items = builtIn.Concat(mine);
        if (!string.IsNullOrEmpty(nameContains))
            items = items.Where(i => new[] { "patternId", "name", "displayName" }.Any(k => i[k]!.GetValue<string>().Contains(nameContains, StringComparison.OrdinalIgnoreCase)));
        var list = items.ToArray();
        return new JsonObject { ["count"] = list.Length, ["customCount"] = list.Count(i => i["kind"]!.GetValue<string>() == "custom"), ["surface"] = surface,
            ["patterns"] = new JsonArray(list.Cast<JsonNode?>().ToArray()),
            ["usage"] = "Pass patternId (or materialId) to apply_material or update_material; the pattern is added to the document library automatically. scale, verticalRatio, angle, ink, lineWeight, background and opacity tune it; gradient entries (dot-gradient, stipple-gradient) also take gradientAngle, gradientStart, gradientEnd and gradientSeed. group is the palette group (basic, screentone, custom); coverage is a uniform screentone's nominal ink coverage at lineWeight 1. favorite mirrors the user's starred patterns (read-only)." };
    }
    public static JsonObject Region(MaterialRegion region) => new()
    {
        ["regionId"] = region.Id.ToString(), ["name"] = region.Name, ["source"] = region.Source,
        ["sourceLayerId"] = region.SourceLayerId?.ToString(), ["bounds"] = Bounds(region.Path.Geometry.Bounds),
        ["coordinateSpace"] = "document", ["areaPixelsSquared"] = region.Path.Geometry.GetArea(),
        ["meaning"] = "Captured boundary template, not a semantic room or a live link to the original object."
    };
    /// <summary>A material layer's fill. With the document, scale is the repeat width relative to the default for that document (the app's size % / 100).</summary>
    public static JsonObject Fill(MaterialFill fill, Document? document = null) => new()
    {
        ["materialId"] = fill.Asset.Id.ToString(), ["materialName"] = fill.Asset.Name,
        ["sourceRegionId"] = fill.SourceRegionId.ToString(), ["regionName"] = fill.RegionName,
        ["tileWidth"] = fill.TileWidth, ["tileHeight"] = fill.TileHeight, ["angle"] = fill.Angle,
        ["offsetX"] = fill.OffsetX, ["offsetY"] = fill.OffsetY, ["patternSpace"] = "layer_local_pixels",
        ["ink"] = Ink(fill.Ink), ["lineWeight"] = fill.LineWeight, ["background"] = Background(fill.Background),
        ["verticalRatio"] = Math.Round(MaterialEditing.Stretch(fill), 6),
        ["scale"] = document == null ? null : Math.Round(fill.TileWidth / MaterialEditing.DefaultTile(document.Width, document.Height, fill.Asset), 6),
        ["patternId"] = PatternId(fill.Asset), ["patternKind"] = HatchPatterns.TryGet(fill.Asset, out _) ? "builtin" : LinePatterns.IsCustom(fill.Asset) ? "custom" : null,
        ["patternGroup"] = PatternGroup(fill.Asset), ["gradient"] = HatchPatterns.IsGradient(fill.Asset) ? Gradient(ToneGradient.Of(fill)) : null,
        ["rendering"] = LinePatterns.IsPattern(fill.Asset) ? "pattern_redrawn" : "image_tile",
        ["boundaryRetained"] = true, ["originalTextureRetained"] = true
    };
    static JsonObject Bounds(Rect r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
}

public static partial class AutomationCatalog
{
    public static readonly string[] DrawingRoleNames = ["structure", "opening", "furniture", "annotation", "hatch", "other"];
    public static readonly string[] DocumentExportFormats = ["pdf", "psd", "ai"];

    /// <summary>Which material source, boundary and size fields of apply_material/update_material belong together.</summary>
    static void ValidateMaterialFields(string command, JsonObject arguments)
    {
        bool Has(string key) => arguments.ContainsKey(key);
        if (Has("materialId") && Has("patternId")) throw new ArgumentException("Use materialId or patternId, not both.");
        if (Has("patternId") && arguments["patternId"]!.GetValue<string>() is var key && !key.StartsWith(LinePatterns.CustomKeyPrefix, StringComparison.Ordinal) && !HatchPatterns.TryParseKey(key, out _))
            throw new ArgumentException($"Unknown patternId '{key}': use a key from query_patterns.");
        // A patternId names the material up front; with materialId or a kept material the editor checks on apply.
        if (AutomationMaterials.HasGradient(arguments) && Has("patternId") && !(HatchPatterns.TryParseKey(arguments["patternId"]!.GetValue<string>(), out var named) && HatchPatterns.IsGradient(named)))
            throw new ArgumentException(AutomationMaterials.GradientOnly);
        if (Has("scale") && Has("tileWidth")) throw new ArgumentException("Use tileWidth (pixels) or scale (relative), not both.");
        if (Has("verticalRatio") && Has("tileHeight")) throw new ArgumentException("Use tileHeight (pixels) or verticalRatio (relative), not both.");
        if (command != "apply_material") return;
        if (!Has("materialId") && !Has("patternId")) throw new ArgumentException("apply_material requires materialId or patternId.");
        int boundaries = new[] { "regionId", "points", "boundaryLayerId" }.Count(Has);
        if (boundaries != 1) throw new ArgumentException("apply_material requires exactly one boundary: regionId, points (with optional holes) or boundaryLayerId.");
        if (Has("holes") && !Has("points")) throw new ArgumentException("holes belong to points only.");
        if (Has("regionName") && Has("regionId")) throw new ArgumentException("regionName names a new region from points or boundaryLayerId; an existing regionId keeps its name.");
        int count = (arguments["points"] as JsonArray)?.Count ?? 0;
        count += (arguments["holes"] as JsonArray)?.Sum(h => (h as JsonArray)?.Count ?? 0) ?? 0;
        if (count > 2048) throw new ArgumentException("A region supports at most 2048 points across all contours.");
    }

    static JsonObject MaterialArraySchema(string type)
    {
        if (type == "roles")
            return new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 512,
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["required"] = Strings(["layer", "role"]), ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["layer"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                        ["role"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(DrawingRoleNames) }
                    }
                }
            };
        var coordinate = new JsonObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 };
        var point = new JsonObject
        {
            ["type"] = "object", ["required"] = Strings(["x", "y"]), ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["x"] = coordinate.DeepClone(), ["y"] = coordinate.DeepClone() }
        };
        if (type == "corners") return new JsonObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = point };
        var contour = new JsonObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 2048, ["items"] = point };
        return type == "points" ? contour : new JsonObject { ["type"] = "array", ["maxItems"] = 16, ["items"] = contour };
    }
    internal static Point[] MaterialPoints(JsonArray array) => array.Select(item =>
    {
        if (item is not JsonObject point || point.Count != 2 || !point.ContainsKey("x") || !point.ContainsKey("y"))
            throw new ArgumentException("Each region point requires exactly numeric x and y.");
        foreach (string key in new[] { "x", "y" })
            if (point[key]?.GetValueKind() != System.Text.Json.JsonValueKind.Number || !double.IsFinite(NumberValue(point, key)) || Math.Abs(NumberValue(point, key)) > 100000)
                throw new ArgumentException("Region point coordinates must be finite numbers in -100000..100000.");
        return new Point(NumberValue(point, "x"), NumberValue(point, "y"));
    }).ToArray();
    /// <summary>Layer-name → role overrides from a validated cadLayerRoles array.</summary>
    internal static Dictionary<string, DrawingRole> LayerRoles(JsonArray array)
    {
        var roles = new Dictionary<string, DrawingRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.OfType<JsonObject>())
            roles[item["layer"]!.GetValue<string>()] = (DrawingRole)Array.IndexOf(DrawingRoleNames, item["role"]!.GetValue<string>());
        return roles;
    }

    static void ValidateMaterialArray(JsonArray array, string type)
    {
        if (type == "roles")
        {
            if (array.Count > 512) throw new ArgumentException("At most 512 layer roles are supported.");
            foreach (var item in array)
            {
                if (item is not JsonObject entry || entry.Count != 2 || entry["layer"] is not JsonValue layer || !layer.TryGetValue<string>(out var name)
                    || string.IsNullOrWhiteSpace(name) || name.Length > 256 || entry["role"] is not JsonValue role || !role.TryGetValue<string>(out var value)
                    || !DrawingRoleNames.Contains(value, StringComparer.Ordinal))
                    throw new ArgumentException($"Each cadLayerRoles item needs layer and role ({string.Join(", ", DrawingRoleNames)}).");
            }
            return;
        }
        if (type == "corners")
        {
            if (array.Count != 4) throw new ArgumentException("corners needs exactly four points: top-left, top-right, bottom-right, bottom-left.");
            _ = MaterialPoints(array);
        }
        else if (type == "points")
        {
            if (array.Count is < 3 or > 2048) throw new ArgumentException("A contour needs 3..2048 points.");
            _ = MaterialPoints(array);
        }
        else
        {
            if (array.Count > 16) throw new ArgumentException("At most 16 inner contours are supported.");
            foreach (var contour in array)
            {
                if (contour is not JsonArray points) throw new ArgumentException("Inner contours must be arrays of points.");
                ValidateMaterialArray(points, "points");
            }
        }
    }
}
