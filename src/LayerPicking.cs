using System.Windows;

namespace Compositor.Windows;

public static class LayerPicking
{
    const double MinimumEffectiveAlpha = 1.0 / 255;
    public static Layer? Pick(Document doc, Point documentPoint)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!double.IsFinite(documentPoint.X) || !double.IsFinite(documentPoint.Y) || documentPoint.X < 0 || documentPoint.Y < 0 || documentPoint.X >= doc.Width || documentPoint.Y >= doc.Height) return null;
        var roots = doc.Layers.Where(l => l.ParentId == null).ToArray();
        var children = doc.Layers.Where(l => l.ParentId != null).GroupBy(l => l.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        Layer[] Children(Layer l) => children.GetValueOrDefault(l.Id) ?? [];
        double LayerAlpha(Layer layer, Point parentPoint, int depth)
        {
            if (depth > 16 || !layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) return 0;
            var local = layer.Local(parentPoint);
            if (!double.IsFinite(local.X) || !double.IsFinite(local.Y)) return 0;
            if (layer.Kind != LayerKind.Group) return Sample(layer, local, false) * layer.Opacity;
            double alpha = 0;
            foreach (var child in Children(layer))
            {
                if (child.Clipped || child.Kind == LayerKind.Adjustment) continue;
                double a = LayerAlpha(child, local, depth + 1); alpha = a + alpha * (1 - a);
            }
            return alpha * Sample(layer, local, true) * layer.Opacity;
        }
        Layer? PickStack(Layer[] stack, Point parentPoint, double inheritedAlpha, int depth)
        {
            if (depth > 16) return null;
            for (int i = stack.Length - 1; i >= 0; i--)
            {
                var layer = stack[i]; if (!layer.Visible || layer.Opacity <= 0 || layer.Kind == LayerKind.Adjustment) continue;
                double clip = 1;
                if (layer.Clipped)
                {
                    int baseIndex = i - 1; while (baseIndex >= 0 && stack[baseIndex].Clipped) baseIndex--;
                    if (baseIndex < 0) continue;
                    clip = LayerAlpha(stack[baseIndex], parentPoint, depth);
                }
                double effective = LayerAlpha(layer, parentPoint, depth) * inheritedAlpha * clip;
                if (effective <= MinimumEffectiveAlpha) continue;
                if (layer.Kind == LayerKind.Group && !layer.Locked)
                {
                    var local = layer.Local(parentPoint);
                    var picked = PickStack(Children(layer), local, inheritedAlpha * clip * layer.Opacity * Sample(layer, local, true), depth + 1);
                    if (picked != null) return picked;
                }
                return layer;
            }
            return null;
        }
        return PickStack(roots, documentPoint, 1, 0);
    }
    static double Sample(Layer layer, Point local, bool maskOnly)
    {
        var raster = layer.Pixels;
        double sx = local.X - .5, sy = local.Y - .5;
        if (sx < -1 || sy < -1 || sx > raster.Width || sy > raster.Height) return 0;
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy); double fx = sx - x0, fy = sy - y0;
        double At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= raster.Width || y >= raster.Height) return 0;
            int index = y * raster.Width + x;
            return (maskOnly ? 1 : raster.Data[index * 4 + 3] / 255.0) * (layer.Mask == null ? 1 : layer.Mask[index] / 255.0);
        }
        return At(x0, y0) * (1 - fx) * (1 - fy) + At(x0 + 1, y0) * fx * (1 - fy) + At(x0, y0 + 1) * (1 - fx) * fy + At(x0 + 1, y0 + 1) * fx * fy;
    }
}
