using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        int width, int height, PathPasses passes, CancellationToken token)
    {
        // Keep WPF surfaces within the same limits as retained PDF/path renders.
        if ((long)width * height > 16_777_216 || width > 8192 || height > 8192)
        {
            var output = new Raster(width, height);
            for (int y = 0; y < height; y += 1536) for (int x = 0; x < width; x += 1536)
            {
                token.ThrowIfCancellationRequested(); var map = parent; map.OffsetX -= x; map.OffsetY -= y;
                int w = Math.Min(1536, width - x), h = Math.Min(1536, height - y);
                var tile = DrawPathRun(stack, start, end, children, map, w, h, passes, token);
                for (int row = 0; row < h; row++) Buffer.BlockCopy(tile.Data, row * w * 4, output.Data, ((row + y) * width + x) * 4, w * 4);
            }
            return output;
        }
        // Every object here paints only inside its own clip rectangle, so one whose
        // rectangle misses the output (plus an antialiasing margin) is skipped
        // without decoding or drawing it. A zoomed-in viewport then costs the
        // objects in view rather than the whole drawing.
        var viewport = new Rect(-2, -2, width + 4, height + 4);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Groups entered but not yet left, re-applied when a new pass begins.
        var open = new List<(Matrix Transform, Rect? Clip)>();
        DrawingVisual? visual = null; DrawingContext? dc = null; int pushed = 0, pending = 0;
        int passSize = passes.Size > 0 ? passes.Size : PathPassSize;
        void Push(Matrix transform, Rect? clip)
        {
            dc!.PushTransform(new MatrixTransform(transform)); pushed++;
            if (clip is { } bounds) { dc.PushClip(new RectangleGeometry(bounds)); pushed++; }
        }
        void Pop(Rect? clip) { dc!.Pop(); pushed--; if (clip != null) { dc.Pop(); pushed--; } }
        void Begin()
        {
            visual = new DrawingVisual(); dc = visual.RenderOpen(); pushed = 0;
            Push(parent, null); foreach (var (transform, clip) in open) Push(transform, clip);
        }
        // Render in passes of PathPassSize objects. RenderTargetBitmap paints each
        // pass over the previous ones in the same order, so the result equals one
        // pass, while a canceled viewport stops between passes instead of
        // finishing a whole-drawing render nobody will see.
        void Flush()
        {
            if (dc == null) return;
            while (pushed > 0) { dc.Pop(); pushed--; }
            dc.Close(); dc = null; bitmap.Render(visual); visual = null; pending = 0;
            passes.Rendered?.Invoke(); token.ThrowIfCancellationRequested();
        }
        void Draw(Layer layer, Matrix world)
        {
            token.ThrowIfCancellationRequested(); if (!layer.Visible) return;
            var local = layer.Matrix; var device = local; device.Append(world);
            Rect? clip = DrawingLayers.IsContainer(layer) ? null : new Rect(0, 0, layer.Pixels.Width, layer.Pixels.Height);
            if (clip is { } area) { area.Transform(device); if (!area.IntersectsWith(viewport)) return; }
            if (layer.Kind == LayerKind.Group)
            {
                if (dc != null) Push(local, clip);
                open.Add((local, clip));
                foreach (var child in children.GetValueOrDefault(layer.Id) ?? []) Draw(child, device);
                open.RemoveAt(open.Count - 1);
                if (dc != null) Pop(clip);
                return;
            }
            if (dc == null) Begin();
            Push(local, clip); dc!.DrawDrawing(layer.Vector!.Drawing); Pop(clip);
            if (++pending >= passSize) Flush();
        }
        for (int i = start; i < end; i++) Draw(stack[i], parent);
        Flush();
        return Raster.FromBitmap(bitmap);
    }

    const int PathPassSize = 1024;
}
