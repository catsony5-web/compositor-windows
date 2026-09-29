namespace Compositor.Windows;

// Cheap change detection for per-frame caches. Pan, zoom and pointer hover run
// on every input event, so they must not hash, snapshot or re-index tens of
// thousands of drawing objects each time. Update compares every layer's
// render- and pick-relevant fields with the previous call's copy, without
// allocating, and advances Version only when something differs. Gestures may
// change layers in place without a history revision, so callers still check
// on each use; this only makes that check proportionate.
public sealed class SceneState
{
    struct Entry
    {
        public Layer Layer; public Raster Pixels; public byte[]? Mask; public VectorContent? Vector; public MaterialFill? Material; public Guid? ParentId;
        public double X, Y, Scale, ScaleX, ScaleY, Rotation, Opacity; public int Flags, SpecHash;
    }

    Entry[] entries = [];
    int count = -1;
    Document? document;
    int width, height;
    public long Version { get; private set; }

    public long Update(Document doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var layers = doc.Layers;
        bool changed = !ReferenceEquals(document, doc) || count != layers.Count || width != doc.Width || height != doc.Height;
        if (entries.Length < layers.Count) Array.Resize(ref entries, layers.Count);
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i]; ref var e = ref entries[i];
            int flags = Flags(l), spec = SpecHash(l);
            if (!changed && ReferenceEquals(e.Layer, l) && ReferenceEquals(e.Pixels, l.Pixels) && ReferenceEquals(e.Mask, l.Mask) &&
                ReferenceEquals(e.Vector, l.Vector) && ReferenceEquals(e.Material, l.Material) && e.ParentId == l.ParentId &&
                e.X.Equals(l.X) && e.Y.Equals(l.Y) && e.Scale.Equals(l.Scale) && e.ScaleX.Equals(l.ScaleX) && e.ScaleY.Equals(l.ScaleY) &&
                e.Rotation.Equals(l.Rotation) && e.Opacity.Equals(l.Opacity) && e.Flags == flags && e.SpecHash == spec) continue;
            changed = true;
            e.Layer = l; e.Pixels = l.Pixels; e.Mask = l.Mask; e.Vector = l.Vector; e.Material = l.Material; e.ParentId = l.ParentId;
            e.X = l.X; e.Y = l.Y; e.Scale = l.Scale; e.ScaleX = l.ScaleX; e.ScaleY = l.ScaleY; e.Rotation = l.Rotation; e.Opacity = l.Opacity;
            e.Flags = flags; e.SpecHash = spec;
        }
        if (changed)
        {
            // Release references to removed layers so a closed document can be collected.
            if (count > layers.Count) Array.Clear(entries, layers.Count, count - layers.Count);
            document = doc; count = layers.Count; width = doc.Width; height = doc.Height; Version++;
        }
        return Version;
    }

    public void Reset() { entries = []; count = -1; document = null; Version++; }

    static int Flags(Layer l) => (int)l.Kind | (int)l.Blend << 4 | (l.Visible ? 1 << 9 : 0) | (l.Locked ? 1 << 10 : 0) |
        (l.Clipped ? 1 << 11 : 0) | (l.FlipX ? 1 << 12 : 0) | (l.FlipY ? 1 << 13 : 0);
    // Value records can be replaced by equal copies; hash their values, as the
    // previous per-frame state hash did. Few layers carry any of them.
    static int SpecHash(Layer l) => l.Text == null && l.Shape == null && l.Adjustment == null && l.Warp == null ? 0 : HashCode.Combine(l.Text, l.Shape, l.Adjustment, l.Warp);
}
