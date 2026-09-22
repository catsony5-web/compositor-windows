using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public static partial class DesignRenderer
{
    static bool CanBatchPaths(Layer layer, Dictionary<Guid, Layer[]> children, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 16 || layer.Clipped || layer.Mask != null || layer.Warp != null || layer.Opacity != 1 || layer.Blend != BlendMode.Normal) return false;
        if (layer.Kind == LayerKind.Group)
            return (children.GetValueOrDefault(layer.Id) ?? []).All(child => CanBatchPaths(child, children, depth + 1, token));
        return layer.Kind == LayerKind.Vector && layer.Vector?.Format == VectorFormat.Paths;
    }

    static Raster DrawPathRun(Layer[] stack, int start, int end, Dictionary<Guid, Layer[]> children, Matrix parent,
        int width, int height, CancellationToken token)
    {
        // Keep WPF surfaces within the same limits as retained PDF/path renders.
        if ((long)width * height > 16_777_216 || width > 8192 || height > 8192)
        {
            var output = new Raster(width, height);
            for (int y = 0; y < height; y += 1536) for (int x = 0; x < width; x += 1536)
            {
                token.ThrowIfCancellationRequested(); var map = parent; map.OffsetX -= x; map.OffsetY -= y;
                int w = Math.Min(1536, width - x), h = Math.Min(1536, height - y);
                var tile = DrawPathRun(stack, start, end, children, map, w, h, token);
                for (int row = 0; row < h; row++) Buffer.BlockCopy(tile.Data, row * w * 4, output.Data, ((row + y) * width + x) * 4, w * 4);
            }
            return output;
        }
        return Imaging.Draw(width, height, dc =>
        {
            dc.PushTransform(new MatrixTransform(parent));
            void Draw(Layer layer)
            {
                token.ThrowIfCancellationRequested(); if (!layer.Visible) return;
                dc.PushTransform(new MatrixTransform(layer.Matrix));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height)));
                if (layer.Kind == LayerKind.Group)
                    foreach (var child in children.GetValueOrDefault(layer.Id) ?? []) Draw(child);
                else dc.DrawDrawing(layer.Vector!.Drawing);
                dc.Pop(); dc.Pop();
            }
            for (int i = start; i < end; i++) Draw(stack[i]);
            dc.Pop();
        });
    }
}
