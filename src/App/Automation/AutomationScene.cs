using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Compositor.Windows;

/// <summary>Bounded observations of the existing document graph; never interprets names as commands.</summary>
public sealed class AutomationScene
{
    readonly Document document;
    readonly Dictionary<Guid, Layer> layers;
    readonly Dictionary<Guid, int> childCounts;
    readonly Dictionary<Guid, int> indices;
    readonly Dictionary<Guid, LayerCategory> categories;

    public AutomationScene(Document document)
    {
        this.document = document;
        layers = document.Layers.ToDictionary(l => l.Id);
        categories = DrawingLayers.Categories(document);
        indices = document.Layers.Select((l, i) => (l.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index);
        childCounts = document.Layers.Where(l => l.ParentId.HasValue).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
    }

    public bool Contains(Guid id) => layers.ContainsKey(id);

    public JsonObject Query(JsonObject args, IReadOnlySet<Guid> selected)
    {
        IEnumerable<Layer> found = document.Layers;
        if (args["selectedOnly"]?.GetValue<bool>() == true) found = found.Where(l => selected.Contains(l.Id));
        if (args["parentId"] is { } parent) found = found.Where(l => l.ParentId == Guid.Parse(parent.GetValue<string>()));
        if (args["rootsOnly"]?.GetValue<bool>() == true) found = found.Where(l => l.ParentId == null);
        if (args["nameContains"] is { } name) found = found.Where(l => l.Name.Contains(name.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        if (args["kind"] is { } kind) found = found.Where(l => l.Kind.ToString() == kind.GetValue<string>());
        if (args["category"] is { } category) found = found.Where(l => categories[l.Id].ToString() == category.GetValue<string>());
        if (args["visible"] is { } visible) found = found.Where(l => l.Visible == visible.GetValue<bool>());
        if (args["locked"] is { } locked) found = found.Where(l => l.Locked == locked.GetValue<bool>());
        var matches = found.ToArray();
        int offset = args["offset"] == null ? 0 : (int)JsonSerializer.Deserialize<double>(args["offset"]!.ToJsonString());
        int limit = args["limit"] == null ? 50 : (int)JsonSerializer.Deserialize<double>(args["limit"]!.ToJsonString());
        var items = new JsonArray(matches.Skip(offset).Take(limit).Select(l => (JsonNode?)Describe(l.Id, false)).ToArray());
        return new JsonObject
        {
            ["revision"] = document.Revision.ToString(), ["totalMatches"] = matches.Length,
            ["offset"] = offset, ["limit"] = limit, ["returned"] = items.Count,
            ["nextOffset"] = offset + items.Count < matches.Length ? offset + items.Count : null,
            ["order"] = "back_to_front", ["layers"] = items
        };
    }

    public JsonObject Describe(Guid id, bool detail = true)
    {
        var layer = layers[id]; var ancestors = new List<Layer>();
        for (var parent = layer.ParentId; parent is { } p; parent = layers[p].ParentId) ancestors.Add(layers[p]);
        var corners = new[] { new Point(), new Point(layer.Pixels.Width, 0), new Point(layer.Pixels.Width, layer.Pixels.Height), new Point(0, layer.Pixels.Height) };
        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = layer.Document(corners[i]);
            foreach (var ancestor in ancestors) corners[i] = ancestor.Document(corners[i]);
        }
        bool finite = corners.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y));
        JsonObject? bounds = null;
        if (finite)
        {
            double left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
            bounds = new JsonObject { ["x"] = left, ["y"] = top, ["width"] = corners.Max(p => p.X) - left, ["height"] = corners.Max(p => p.Y) - top };
        }
        bool ancestorLocked = ancestors.Any(p => p.Locked);
        var result = new JsonObject
        {
            ["layerId"] = layer.Id.ToString(), ["name"] = layer.Name, ["kind"] = layer.Kind.ToString(),
            ["parentId"] = layer.ParentId?.ToString(), ["depth"] = ancestors.Count, ["stackIndex"] = indices[id],
            ["childCount"] = childCounts.GetValueOrDefault(id), ["visible"] = layer.Visible, ["locked"] = layer.Locked,
            ["category"] = categories[id].ToString(), ["sourceLayerName"] = layer.SourceLayerName,
            ["visibleInHierarchy"] = layer.Visible && ancestors.All(p => p.Visible), ["lockedInHierarchy"] = layer.Locked || ancestorLocked,
            ["frameBounds"] = bounds
        };
        if (!detail)
        {
            if (layer.Text is { } text) result["textPreview"] = text.Content[..Math.Min(120, text.Content.Length)];
            return result;
        }
        result["parentChain"] = new JsonArray(ancestors.AsEnumerable().Reverse().Select(p => (JsonNode?)JsonValue.Create(p.Id.ToString())).ToArray());
        result["lockedAncestorIds"] = new JsonArray(ancestors.Where(p => p.Locked).Select(p => (JsonNode?)JsonValue.Create(p.Id.ToString())).ToArray());
        result["width"] = layer.Pixels.Width; result["height"] = layer.Pixels.Height;
        result["storedCategory"] = layer.Category.ToString();
        result["x"] = layer.X; result["y"] = layer.Y; result["positionSpace"] = "parent";
        result["scaleX"] = layer.ScaleX * layer.Scale; result["scaleY"] = layer.ScaleY * layer.Scale;
        result["rotation"] = layer.Rotation; result["flipX"] = layer.FlipX; result["flipY"] = layer.FlipY;
        result["opacity"] = layer.Opacity; result["blend"] = layer.Blend.ToString();
        result["hasMask"] = layer.Mask != null; result["clipped"] = layer.Clipped;
        result["warp"] = layer.Warp == null ? null : JsonSerializer.SerializeToNode(layer.Warp);
        result["frameBoundsMeaning"] = "Transformed surface corners before ancestor clipping; not ink bounds or a semantic room boundary.";
        if (finite) result["documentCorners"] = new JsonArray(corners.Select(p => (JsonNode?)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray());
        if (layer.Text != null) result["text"] = JsonSerializer.SerializeToNode(layer.Text);
        if (layer.Material != null) result["material"] = AutomationMaterials.Fill(layer.Material);
        if (layer.Shape != null) result["shape"] = JsonSerializer.SerializeToNode(layer.Shape);
        if (layer.Adjustment != null) result["adjustment"] = JsonSerializer.SerializeToNode(layer.Adjustment);
        if (layer.Vector != null) result["vector"] = new JsonObject
        {
            ["format"] = layer.Vector.Format.ToString(), ["width"] = layer.Vector.Width,
            ["height"] = layer.Vector.Height, ["page"] = layer.Vector.Page, ["bytes"] = layer.Vector.ByteLength
        };
        result["editing"] = new JsonObject
        {
            ["properties"] = !layer.Locked && !ancestorLocked,
            ["unlockOnly"] = layer.Locked && !ancestorLocked,
            ["text"] = layer.Kind == LayerKind.Text && !layer.Locked && !ancestorLocked,
            ["material"] = layer.Kind == LayerKind.Material && !layer.Locked && !ancestorLocked,
            ["vectorGeometryViaMcp"] = false,
            ["shapeDefinitionViaMcp"] = false,
            ["positionNote"] = "Set x/y in parent coordinates. Use the parent chain and documentCorners to reason about placement."
        };
        return result;
    }
}
