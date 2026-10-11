using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>
/// Recolours map layers after they were made: a whole map to another theme (each layer by its class),
/// or one class group or layer to one colour. Geometry, line weights, positions and visibility stay.
/// Works on a candidate document; callers commit it as one undo step.
/// </summary>
public static class MapStyling
{
    /// <summary>The map folder that holds <paramref name="layer"/> (or is it).</summary>
    public static Layer? Root(Document doc, Layer? layer)
    {
        var index = doc.Layers.ToDictionary(l => l.Id);
        for (int depth = 0; layer != null && depth < 64; depth++)
        {
            if (layer.Map is { IsRoot: true }) return layer;
            layer = layer.ParentId is { } parent && index.TryGetValue(parent, out var next) ? next : null;
        }
        return null;
    }

    /// <summary>The layer and every layer inside it.</summary>
    public static List<Layer> Subtree(Document doc, Guid id)
    {
        var children = doc.Layers.ToLookup(l => l.ParentId); var result = new List<Layer>(); var pending = new Stack<Guid>([id]);
        var index = doc.Layers.ToDictionary(l => l.Id);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!index.TryGetValue(next, out var layer) || result.Contains(layer)) continue;
            result.Add(layer);
            foreach (var child in children[next]) pending.Push(child.Id);
        }
        return result;
    }

    // Colours of a class in a theme, primary first; multi-colour parts also use the theme's light colour.
    static uint[] Roles(MapTheme theme, string key) => key switch
    {
        MapClasses.Marker => [theme.Accent, theme.Light],
        MapClasses.NorthArrow or MapClasses.ScaleBar => [theme.Text, theme.Light],
        _ => [theme.Color(key)]
    };

    /// <summary>Recolours every map layer of the map folder <paramref name="rootId"/> to <paramref name="themeKey"/>. Returns the layers changed.</summary>
    public static int ApplyTheme(Document doc, Guid rootId, string themeKey, CancellationToken token = default)
    {
        var root = doc.Layers.Find(l => l.Id == rootId) ?? throw new InvalidOperationException("지도 그룹을 찾을 수 없습니다.");
        if (root.Map is not { IsRoot: true } tag) throw new InvalidOperationException("지도 포스터·대지 위치도 그룹을 선택하세요.");
        if (!MapThemes.Exists(themeKey)) throw new ArgumentException("알 수 없는 지도 테마입니다.", nameof(themeKey));
        var from = MapThemes.Get(tag.Theme); var to = MapThemes.Get(themeKey); int changed = 0;
        foreach (var layer in Subtree(doc, rootId))
        {
            token.ThrowIfCancellationRequested();
            if (layer.Map is not { IsRoot: false } part) continue;
            string key = part.Role; uint[] old = Roles(from, key), next = Roles(to, key);
            uint Map(uint color)
            {
                for (int i = 0; i < old.Length; i++) if ((color & 0xFFFFFF) == (old[i] & 0xFFFFFF)) return (color & 0xFF000000) | (next[i] & 0xFFFFFF);
                return old.Length == 1 ? (color & 0xFF000000) | (next[0] & 0xFFFFFF) : color;
            }
            if (Restyle(layer, Map, key == MapClasses.Attribution || layer.Text?.Outline == true ? to.Background : null)) changed++;
        }
        root.Map = tag with { Theme = to.Key };
        return changed;
    }

    /// <summary>
    /// Gives the layer <paramref name="id"/> and everything inside it one colour (alpha kept). Parts
    /// drawn in two colours (marker, north arrow, scale bar) change only their main colour.
    /// </summary>
    public static int Recolor(Document doc, Guid id, Color color, CancellationToken token = default)
    {
        if (doc.Layers.Find(l => l.Id == id) == null) throw new InvalidOperationException("레이어를 찾을 수 없습니다.");
        uint rgb = VectorShapes.Argb(color) & 0xFFFFFF; int changed = 0;
        var theme = MapThemes.Get(Root(doc, doc.Layers.Find(l => l.Id == id))?.Map?.Theme);
        foreach (var layer in Subtree(doc, id))
        {
            token.ThrowIfCancellationRequested();
            if (layer.Kind == LayerKind.Group) continue;
            uint[] keep = layer.Map is { Role: var key } && Roles(theme, key) is { Length: > 1 } roles ? roles.Skip(1).Select(c => c & 0xFFFFFF).ToArray() : [];
            uint Map(uint c) => keep.Contains(c & 0xFFFFFF) ? c : (c & 0xFF000000) | rgb;
            if (Restyle(layer, Map, null)) changed++;
        }
        return changed;
    }

    // Rewrites the colours of one layer's content and redraws its cache. True when it changed.
    static bool Restyle(Layer layer, Func<uint, uint> map, uint? outline)
    {
        switch (layer.Kind)
        {
            case LayerKind.Vector when layer.Vector is { Format: VectorFormat.Paths } vector:
                VectorContent.Scene? scene;
                using (var input = vector.Open()) scene = JsonSerializer.Deserialize<VectorContent.Scene>(input);
                if (scene?.Items == null || scene.Clips == null) throw new InvalidDataException("벡터 경로 정보가 없습니다.");
                bool different = false;
                var items = scene.Items.Select(item => { var next = item with { Color = map(item.Color) }; different |= next != item; return next; }).ToArray();
                if (!different) return false;
                using (var output = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new VectorContent.Scene(scene.Clips, items)), false))
                    layer.Vector = VectorContent.Read(VectorFormat.Paths, vector.Width, vector.Height, vector.Page, output);
                var drawn = layer.Vector; int width = layer.Pixels.Width, height = layer.Pixels.Height;
                layer.Pixels = Imaging.Draw(width, height, dc =>
                {
                    bool scaled = width != drawn.Width || height != drawn.Height;
                    if (scaled) dc.PushTransform(new ScaleTransform(width / (double)drawn.Width, height / (double)drawn.Height));
                    dc.DrawDrawing(drawn.Drawing);
                    if (scaled) dc.Pop();
                });
                return true;
            case LayerKind.Text when layer.Text is { } text:
                var spec = text with { ColorArgb = map(text.ColorArgb), OutlineArgb = outline is { } o && text.Outline ? (text.OutlineArgb & 0xFF000000) | (o & 0xFFFFFF) : text.OutlineArgb };
                if (spec == text) return false;
                DocumentFeatures.UpdateText(layer, spec); return true;
            case LayerKind.Shape when layer.Shape is { } shape:
                var next = shape with { FillArgb = map(shape.FillArgb), StrokeArgb = map(shape.StrokeArgb) };
                if (!shape.FillEnabled) next = next with { FillArgb = shape.FillArgb };
                if (!shape.StrokeEnabled) next = next with { StrokeArgb = shape.StrokeArgb };
                if (next == shape) return false;
                VectorShapes.Update(layer, next); return true;
            case LayerKind.Raster:
                // The edge fade: one colour with varying alpha.
                var source = layer.Pixels; var pixels = source.Clone(); bool changed = false;
                for (int i = 0; i < pixels.Data.Length; i += 4)
                {
                    if (pixels.Data[i + 3] == 0) continue;
                    uint c = map(0xFF000000u | (uint)pixels.Data[i + 2] << 16 | (uint)pixels.Data[i + 1] << 8 | pixels.Data[i]);
                    byte r = (byte)(c >> 16), g = (byte)(c >> 8), b = (byte)c;
                    changed |= r != pixels.Data[i + 2] || g != pixels.Data[i + 1] || b != pixels.Data[i];
                    pixels.Data[i] = b; pixels.Data[i + 1] = g; pixels.Data[i + 2] = r;
                }
                if (!changed) return false;
                layer.Pixels = pixels; return true;
            default: return false;
        }
    }
}
