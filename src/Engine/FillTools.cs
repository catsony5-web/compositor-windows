using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Raster paint bucket. The sampled image is in document space; only the chosen raster layer is changed.</summary>
public static class FillTools
{
    public static Raster RenderLayerSample(Document document, Guid layerId, CancellationToken token = default)
    {
        var isolated = document.Snapshot();
        var keep = new HashSet<Guid> { layerId };
        var layer = isolated.Layers.Find(l => l.Id == layerId) ?? throw new ArgumentException("레이어가 없습니다.", nameof(layerId));
        for (Guid? parent = layer.ParentId; parent is { } id;)
        {
            token.ThrowIfCancellationRequested();
            if (!keep.Add(id)) throw new InvalidOperationException("그룹 계층에 순환이 있습니다.");
            var group = isolated.Layers.Find(l => l.Id == id) ?? throw new InvalidOperationException("부모 그룹이 없습니다.");
            parent = group.ParentId;
        }
        isolated.Layers.RemoveAll(l => !keep.Contains(l.Id));
        // A clipped layer without its base has no visible pixels, but bucket
        // sampling the active layer should still read its own painted pixels.
        layer.Clipped = false;
        return Imaging.Render(isolated, token);
    }

    public static Raster? Fill(
        Document document, Guid layerId, Selection? selection, Raster sample, Point seed,
        Color color, double tolerance = 32, bool contiguous = true, double opacity = 1,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sample);
        token.ThrowIfCancellationRequested();
        if (sample.Width != document.Width || sample.Height != document.Height)
            throw new ArgumentException("샘플 이미지는 문서 크기여야 합니다.", nameof(sample));
        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (opacity == 0 || color.A == 0 || !double.IsFinite(seed.X) || !double.IsFinite(seed.Y) ||
            seed.X < 0 || seed.Y < 0 || seed.X >= document.Width || seed.Y >= document.Height ||
            selection != null && selection.Weight(seed.X, seed.Y) <= 0) return null;

        var layer = document.Layers.Find(l => l.Id == layerId) ?? throw new ArgumentException("레이어가 없습니다.", nameof(layerId));
        if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("픽셀 레이어를 선택하세요.");
        if (layer.Locked || !layer.Visible) throw new InvalidOperationException("잠겼거나 숨겨진 레이어는 채울 수 없습니다.");
        var seen = new HashSet<Guid>();
        var transform = layer.Matrix;
        bool affine = layer.Warp == null;
        var steps = new List<(System.Windows.Media.Matrix Matrix, ProjectiveMap? Warp, int Width, int Height)>
        { (layer.Matrix, layer.Warp?.Map(), layer.Pixels.Width, layer.Pixels.Height) };
        for (Guid? parent = layer.ParentId; parent is { } id;)
        {
            if (!seen.Add(id)) throw new InvalidOperationException("그룹 계층에 순환이 있습니다.");
            var group = document.Layers.Find(l => l.Id == id) ?? throw new InvalidOperationException("부모 그룹이 없습니다.");
            if (group.Locked || !group.Visible) throw new InvalidOperationException("잠겼거나 숨겨진 그룹의 레이어는 채울 수 없습니다.");
            affine &= group.Warp == null;
            transform.Append(group.Matrix);
            steps.Add((group.Matrix, group.Warp?.Map(), group.Pixels.Width, group.Pixels.Height));
            parent = group.ParentId;
        }
        Point ToDocument(Point point)
        {
            foreach (var step in steps)
            {
                if (step.Warp is { } warp) point = warp.Transform(new Point(point.X / step.Width, point.Y / step.Height));
                point = step.Matrix.Transform(point);
            }
            return point;
        }

        int w = sample.Width, h = sample.Height, start = (int)seed.Y * w + (int)seed.X;
        var region = new byte[w * h];
        bool Eligible(int index)
        {
            int x = index % w, y = index / w;
            return (selection == null || selection.Weight(x + .5, y + .5) > 0) &&
                SelectionTools.ColorDistance(sample.Data, start * 4, index * 4) <= tolerance;
        }
        if (contiguous)
        {
            var visited = new byte[region.Length];
            var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = 1;
            int checkedCount = 0;
            while (queue.TryDequeue(out int index))
            {
                if ((++checkedCount & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!Eligible(index)) continue;
                region[index] = 1;
                int x = index % w, y = index / w;
                void Add(int next) { if (visited[next] == 0) { visited[next] = 1; queue.Enqueue(next); } }
                if (x > 0) Add(index - 1);
                if (x + 1 < w) Add(index + 1);
                if (y > 0) Add(index - w);
                if (y + 1 < h) Add(index + w);
            }
        }
        else
        {
            for (int index = 0; index < region.Length; index++)
            {
                if ((index & 16383) == 0) token.ThrowIfCancellationRequested();
                if (Eligible(index)) region[index] = 1;
            }
        }
        token.ThrowIfCancellationRequested();

        var output = layer.Pixels.Clone();
        bool changed = false;
        for (int y = 0; y < output.Height; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            for (int x = 0; x < output.Width; x++)
            {
                // Local pixel centers are mapped through both the layer and its
                // parent groups. This also keeps the stored pixels in local space.
                var local = new Point(x + .5, y + .5);
                var p = affine ? transform.Transform(local) : ToDocument(local);
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h) continue;
                if (region[(int)p.Y * w + (int)p.X] == 0) continue;
                double weight = (selection?.Weight(p.X, p.Y) ?? 1) * opacity * color.A / 255.0;
                if (weight <= 0) continue;
                int i = (y * output.Width + x) * 4;
                byte b = output.Data[i], g = output.Data[i + 1], r = output.Data[i + 2], a = output.Data[i + 3];
                Imaging.Over(output.Data, i, color.B / 255.0, color.G / 255.0, color.R / 255.0, weight);
                changed |= b != output.Data[i] || g != output.Data[i + 1] || r != output.Data[i + 2] || a != output.Data[i + 3];
            }
        }
        token.ThrowIfCancellationRequested();
        return changed ? output : null;
    }
}
