using System.IO;
using System.Windows;

namespace Compositor.Windows;

public sealed record SelectedLayerImage(Raster Image, Int32Rect CanvasBounds, int IndependentClippingCount);

/// <summary>
/// Exports a selection in document coordinates without changing the live document. Ancestor
/// groups preserve transforms, masks and opacity, but never bring along unselected siblings.
/// A selected group includes its subtree. Missing clipping bases are deliberately detached,
/// rather than silently exporting an unselected image or clipping against the wrong sibling.
/// </summary>
public static class SelectedLayerExport
{
    public static SelectedLayerImage Render(Document document, IEnumerable<Guid> selectedIds,
        bool trimTransparent = true, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedIds);
        token.ThrowIfCancellationRequested();
        document.Validate();
        var snapshot = document.Snapshot();
        var lookup = snapshot.Layers.ToDictionary(layer => layer.Id);
        var selected = selectedIds.ToHashSet();
        if (selected.Count == 0) throw new InvalidOperationException("내보낼 레이어를 먼저 선택하세요.");
        if (selected.Any(id => !lookup.ContainsKey(id))) throw new InvalidOperationException("선택한 레이어가 현재 문서에 없습니다.");

        var keep = new HashSet<Guid>(selected);
        // Expand only selected groups, before adding ancestors. Otherwise selecting a child
        // would accidentally expand its ancestor and include every unselected sibling.
        bool expanded;
        do
        {
            expanded = false;
            foreach (var layer in snapshot.Layers)
                if (layer.ParentId is { } parent && keep.Contains(parent)) expanded |= keep.Add(layer.Id);
        } while (expanded);

        foreach (var id in selected)
        {
            var layer = lookup[id]; layer.Visible = true;
            while (layer.ParentId is { } parent)
            {
                layer = lookup[parent]; keep.Add(parent); layer.Visible = true;
            }
        }

        int detached = 0;
        foreach (var siblings in snapshot.Layers.GroupBy(layer => layer.ParentId))
        {
            Guid? originalBase = null;
            foreach (var layer in siblings)
            {
                if (!layer.Clipped) { originalBase = layer.Id; continue; }
                if (keep.Contains(layer.Id) && (originalBase == null || !keep.Contains(originalBase.Value)))
                {
                    layer.Clipped = false;
                    detached++;
                }
            }
        }
        snapshot.Layers.RemoveAll(layer => !keep.Contains(layer.Id));
        snapshot.ActiveId = selected.First();
        snapshot.Validate();
        var rendered = DesignRenderer.RenderOutput(snapshot, token);
        return trimTransparent ? Trim(rendered, detached, token)
            : new SelectedLayerImage(rendered, new Int32Rect(0, 0, rendered.Width, rendered.Height), detached);
    }

    public static SelectedLayerImage Trim(Raster source, int independentClippingCount = 0, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        for (int y = 0; y < source.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
            {
                if (source.Data[(y * source.Width + x) * 4 + 3] == 0) continue;
                left = Math.Min(left, x); top = Math.Min(top, y);
                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        }
        if (right < left) throw new InvalidOperationException("선택한 레이어에 내보낼 픽셀이 없습니다. 조정 레이어는 이미지 레이어와 함께 선택하거나, 전체 캔버스 크기를 선택하세요.");
        var bounds = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        if (left == 0 && top == 0 && bounds.Width == source.Width && bounds.Height == source.Height)
            return new SelectedLayerImage(source, bounds, independentClippingCount);
        var output = new Raster(bounds.Width, bounds.Height);
        for (int y = 0; y < output.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Buffer.BlockCopy(source.Data, ((top + y) * source.Width + left) * 4, output.Data, y * output.Width * 4, output.Width * 4);
        }
        return new SelectedLayerImage(output, bounds, independentClippingCount);
    }

    public static void Export(Document document, IEnumerable<Guid> selectedIds, string path,
        bool trimTransparent = true, int quality = 95, CancellationToken token = default)
    {
        // Capture DPI with the same snapshot as the pixels.
        var snapshot = document.Snapshot();
        var result = Render(snapshot, selectedIds, trimTransparent, token);
        token.ThrowIfCancellationRequested();
        ProjectStore.AtomicWrite(path, stream => ImportExport.Write(result.Image, stream, Path.GetExtension(path), quality, snapshot.Dpi));
    }
}
