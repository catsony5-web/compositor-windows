using System.Text.Json.Nodes;
using System.Windows;

namespace Compositor.Windows;

public static class AutomationMaterials
{
    public static JsonObject Asset(MaterialAsset asset) => new()
    {
        ["materialId"] = asset.Id.ToString(), ["name"] = asset.Name, ["width"] = asset.Pixels.Width, ["height"] = asset.Pixels.Height,
        ["source"] = asset.Source, ["tileable"] = asset.Tileable,
        ["tileableMeaning"] = "Caller-declared; no seamless-image generation or verification is performed."
    };
    public static JsonObject Region(MaterialRegion region) => new()
    {
        ["regionId"] = region.Id.ToString(), ["name"] = region.Name, ["source"] = region.Source,
        ["sourceLayerId"] = region.SourceLayerId?.ToString(), ["bounds"] = Bounds(region.Path.Geometry.Bounds),
        ["coordinateSpace"] = "document", ["areaPixelsSquared"] = region.Path.Geometry.GetArea(),
        ["meaning"] = "Captured boundary template, not a semantic room or a live link to the original object."
    };
    public static JsonObject Fill(MaterialFill fill) => new()
    {
        ["materialId"] = fill.Asset.Id.ToString(), ["materialName"] = fill.Asset.Name,
        ["sourceRegionId"] = fill.SourceRegionId.ToString(), ["regionName"] = fill.RegionName,
        ["tileWidth"] = fill.TileWidth, ["tileHeight"] = fill.TileHeight, ["angle"] = fill.Angle,
        ["offsetX"] = fill.OffsetX, ["offsetY"] = fill.OffsetY, ["patternSpace"] = "layer_local_pixels",
        ["boundaryRetained"] = true, ["originalTextureRetained"] = true
    };
    static JsonObject Bounds(Rect r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
}

public static partial class AutomationCatalog
{
    public static readonly string[] DrawingRoleNames = ["structure", "opening", "furniture", "annotation", "hatch", "other"];

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
        if (type == "points")
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
