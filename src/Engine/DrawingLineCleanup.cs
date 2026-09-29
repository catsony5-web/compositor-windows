using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Compositor.Windows;

// Re-applies the import-time line cleanup (DrawingCleanup weights and tones by layer role)
// to drawings already in a document, e.g. one imported without cleanup. The role comes from
// the CAD layer name kept on the layer: its CAD-layer folder (object imports), its source
// name, then its own name (layer imports). Layers without a recognized role, locked layers
// and PDF sources are left untouched. Hatch materials cannot be rebuilt here because the
// hatch regions are not retained after import; material layers can only be shown or hidden.
public static class DrawingLineCleanup
{
    public sealed record Result(int Restyled, int Unchanged, int Unknown, int Locked, IReadOnlyDictionary<DrawingRole, int> Roles);

    public static DrawingRole RoleOf(Document doc, Layer layer, IReadOnlyDictionary<Guid, Layer>? index = null)
    {
        index ??= doc.Layers.ToDictionary(l => l.Id);
        var parent = layer.ParentId is { } id && index.TryGetValue(id, out var found) ? found : null;
        // A folder's own name is the document name for layer imports, so only its source name counts.
        foreach (var name in new[] { parent?.SourceLayerName, layer.SourceLayerName, layer.Kind == LayerKind.Group ? null : layer.Name })
            if (!string.IsNullOrWhiteSpace(name) && DrawingCleanup.Classify(name) is var role && role != DrawingRole.Other) return role;
        return DrawingRole.Other;
    }

    // Vector path layers inside the scope (selected layers and their descendants); an empty
    // scope, or one without any drawing line, means the whole document.
    static Layer[] Candidates(Document doc, IReadOnlyCollection<Guid>? scope)
    {
        var paths = doc.Layers.Where(l => l.Kind == LayerKind.Vector && l.Vector is { Format: VectorFormat.Paths }).ToArray();
        if (scope is not { Count: > 0 }) return paths;
        var children = doc.Layers.ToLookup(l => l.ParentId);
        var within = new HashSet<Guid>();
        var pending = new Stack<Guid>(scope);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!within.Add(id)) continue;
            foreach (var child in children[id]) pending.Push(child.Id);
        }
        var scoped = paths.Where(l => within.Contains(l.Id)).ToArray();
        return scoped.Length > 0 ? scoped : paths;
    }

    public static Result Apply(Document doc, IReadOnlyCollection<Guid>? scope = null)
    {
        var index = doc.Layers.ToDictionary(l => l.Id);
        bool Locked(Layer layer)
        {
            Layer? current = layer;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                if (current.Locked) return true;
                current = current.ParentId is { } parent && index.TryGetValue(parent, out var next) ? next : null;
            }
            return false;
        }
        int restyled = 0, unchanged = 0, unknown = 0, locked = 0; var roles = new Dictionary<DrawingRole, int>();
        foreach (var layer in Candidates(doc, scope))
        {
            var role = RoleOf(doc, layer, index);
            if (role == DrawingRole.Other) { unknown++; continue; }
            if (Locked(layer)) { locked++; continue; }
            if (Restyle(layer.Vector!, role) is not { } vector) { unchanged++; continue; }
            layer.Vector = vector;
            layer.Pixels = Rasterize(vector, layer.Pixels.Width, layer.Pixels.Height);
            restyled++; roles[role] = roles.GetValueOrDefault(role) + 1;
        }
        return new(restyled, unchanged, unknown, locked, roles);
    }

    // Same weight and tone as the import cleanup gives the role (DrawingCleanup), alpha kept.
    // Null when every path already has them.
    internal static VectorContent? Restyle(VectorContent source, DrawingRole role)
    {
        VectorContent.Scene? scene;
        using (var input = source.Open()) scene = JsonSerializer.Deserialize<VectorContent.Scene>(input);
        if (scene?.Items == null || scene.Clips == null) throw new InvalidDataException("벡터 경로 정보가 없습니다.");
        uint tone = VectorShapes.Argb(DrawingCleanup.Tone(role)) & 0x00FFFFFF; double weight = DrawingCleanup.Weight(role);
        bool changed = false;
        var items = scene.Items.Select(item =>
        {
            var next = item with { Color = (item.Color & 0xFF000000) | tone, StrokeWidth = weight };
            changed |= next != item;
            return next;
        }).ToArray();
        if (!changed) return null;
        using var output = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new VectorContent.Scene(scene.Clips, items)), false);
        return VectorContent.Read(VectorFormat.Paths, source.Width, source.Height, source.Page, output);
    }

    static Raster Rasterize(VectorContent vector, int width, int height) => Imaging.Draw(width, height, dc =>
    {
        bool scaled = width != vector.Width || height != vector.Height;
        if (scaled) dc.PushTransform(new ScaleTransform(width / (double)vector.Width, height / (double)vector.Height));
        dc.DrawDrawing(vector.Drawing);
        if (scaled) dc.Pop();
    });

    // Top-most drawing layers with the role: CAD-layer folders (by their source name) and
    // vector layers, so hiding one hides its objects once.
    public static Guid[] RoleLayers(Document doc, DrawingRole role)
    {
        var index = doc.Layers.ToDictionary(l => l.Id); var categories = DrawingLayers.Categories(doc);
        bool Match(Layer layer) => categories.GetValueOrDefault(layer.Id) == LayerCategory.Drawing && (layer.Kind == LayerKind.Group
            ? layer.SourceLayerName is { } source && DrawingCleanup.Classify(source) == role
            : layer.Kind == LayerKind.Vector && RoleOf(doc, layer, index) == role);
        var matches = doc.Layers.Where(Match).Select(l => l.Id).ToHashSet();
        bool UnderMatch(Layer layer)
        {
            var parent = layer.ParentId;
            for (int depth = 0; parent is { } id && depth < 64; depth++)
            {
                if (matches.Contains(id)) return true;
                parent = index.TryGetValue(id, out var next) ? next.ParentId : null;
            }
            return false;
        }
        return doc.Layers.Where(l => matches.Contains(l.Id) && !UnderMatch(l)).Select(l => l.Id).ToArray();
    }
}
