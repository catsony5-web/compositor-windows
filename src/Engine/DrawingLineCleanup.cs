using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Compositor.Windows;

// Re-applies the import-time line cleanup (DrawingCleanup weights and tones by layer role)
// to drawings already in a document, e.g. one imported without cleanup. The role comes from
// the CAD layer name kept on the layer: the nearest CAD-layer folder (object imports, also
// after objects were grouped inside it), otherwise its own name (layer imports), resolved
// like the import: the user's remembered role choices first, then the automatic guess.
// Layers without a recognized role, locked layers and PDF sources are left untouched. Hatch
// materials cannot be rebuilt here because the hatch regions are not retained after import;
// material layers can only be shown or hidden.
public static class DrawingLineCleanup
{
    public sealed record Result(int Restyled, int Unchanged, int Unknown, int Locked, IReadOnlyDictionary<DrawingRole, int> Roles);

    // Import cleanup (CadCompatibility.ApplyCleanup) names its hatch regions "해치 · …"; materials
    // the user fills selections with ("선택 영역") or maps by hand are not import hatches.
    const string HatchRegionPrefix = "해치 · ";
    public static bool IsHatchMaterial(Layer layer) => layer.Kind == LayerKind.Material && layer.Material?.RegionName.StartsWith(HatchRegionPrefix, StringComparison.Ordinal) == true;

    // The CAD layer a layer was drawn on. Object layers take it from the nearest folder that keeps
    // one and never from their generated names ('문자 7', '해치 5'), which import cleanup ignores too.
    // A folder's own name is the document name for layer imports, so only its source name counts.
    static string? CadLayerName(Layer layer, IReadOnlyDictionary<Guid, Layer> index)
    {
        if (!string.IsNullOrWhiteSpace(layer.SourceLayerName)) return layer.SourceLayerName;
        var parent = layer.ParentId;
        for (int depth = 0; parent is { } id && depth < 64 && index.TryGetValue(id, out var ancestor); depth++, parent = ancestor.ParentId)
            if (!string.IsNullOrWhiteSpace(ancestor.SourceLayerName)) return ancestor.SourceLayerName;
        return layer.Kind == LayerKind.Group || string.IsNullOrWhiteSpace(layer.Name) ? null : layer.Name;
    }

    /// <param name="roles">Roles the user chose in the import dialog, by CAD layer name (ImportSettings.CadRoles).</param>
    public static DrawingRole RoleOf(Document doc, Layer layer, IReadOnlyDictionary<Guid, Layer>? index = null, IReadOnlyDictionary<string, DrawingRole>? roles = null)
    {
        index ??= doc.Layers.ToDictionary(l => l.Id);
        return CadLayerName(layer, index) is { } name ? new CadCleanup(Roles: roles).RoleFor(name) : DrawingRole.Other;
    }

    // Vector path layers inside the scope (selected layers and their descendants); an empty
    // scope, or one without any drawing line, means the whole document.
    static Layer[] Candidates(Document doc, IReadOnlyCollection<Guid>? scope)
    {
        // Entourage keeps its own line weight and colour (EntourageSpec); it is not drawing line work.
        var paths = doc.Layers.Where(l => l.Kind == LayerKind.Vector && l.Vector is { Format: VectorFormat.Paths } && l.Entourage == null).ToArray();
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

    // Runs off the UI thread (an STA, like import): progress reports (done, total) candidates.
    public static Result Apply(Document doc, IReadOnlyCollection<Guid>? scope = null, IReadOnlyDictionary<string, DrawingRole>? roles = null,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken token = default)
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
        int restyled = 0, unchanged = 0, unknown = 0, locked = 0, done = 0; var counts = new Dictionary<DrawingRole, int>();
        var candidates = Candidates(doc, scope);
        foreach (var layer in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (++done % 256 == 0) progress?.Report((done, candidates.Length));
            var role = RoleOf(doc, layer, index, roles);
            if (role == DrawingRole.Other) { unknown++; continue; }
            if (Locked(layer)) { locked++; continue; }
            if (Restyle(layer.Vector!, role) is not { } vector) { unchanged++; continue; }
            layer.Vector = vector;
            layer.Pixels = Rasterize(vector, layer.Pixels.Width, layer.Pixels.Height);
            restyled++; counts[role] = counts.GetValueOrDefault(role) + 1;
        }
        return new(restyled, unchanged, unknown, locked, counts);
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
    public static Guid[] RoleLayers(Document doc, DrawingRole role, IReadOnlyDictionary<string, DrawingRole>? roles = null)
    {
        var index = doc.Layers.ToDictionary(l => l.Id); var categories = DrawingLayers.Categories(doc); var cleanup = new CadCleanup(Roles: roles);
        bool Match(Layer layer) => categories.GetValueOrDefault(layer.Id) == LayerCategory.Drawing && (layer.Kind == LayerKind.Group
            ? !string.IsNullOrWhiteSpace(layer.SourceLayerName) && cleanup.RoleFor(layer.SourceLayerName) == role
            : layer.Kind == LayerKind.Vector && layer.Entourage == null && RoleOf(doc, layer, index, roles) == role);
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
