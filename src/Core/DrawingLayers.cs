namespace Compositor.Windows;

public static class DrawingLayers
{
    // Imported drawing folders organize objects; their original page-sized
    // coordinate surface must not crop objects moved onto another artboard.
    public static bool IsContainer(Layer layer) => layer.Kind == LayerKind.Group && layer.Category == LayerCategory.Drawing && layer.Mask == null && layer.Warp == null;
    public static void Wrap(Document document)
    {
        if (document.Layers.Count == 0) return;
        var roots = document.Layers.Where(l => l.ParentId == null).ToArray();
        if (roots.Length == 1 && roots[0].Kind == LayerKind.Group && roots[0].Category == LayerCategory.Drawing) return;
        var pixels = document.Layers.FirstOrDefault(l => l.Kind == LayerKind.Group && l.Pixels.Width == document.Width && l.Pixels.Height == document.Height)?.Pixels
            ?? new Raster(document.Width, document.Height);
        var folder = new Layer { Name = document.Name, Kind = LayerKind.Group, Category = LayerCategory.Drawing, Pixels = pixels };
        // Validate capacity before changing any existing parent links.
        document.Add(folder);
        foreach (var root in roots) root.ParentId = folder.Id;
        document.Layers.Remove(folder); document.Layers.Insert(0, folder); document.ActiveId = folder.Id;
        document.Validate();
    }
    // A root decides the category of its whole subtree, except material (hatch fill) layers: they are
    // always photo layers, edited on the photo tab with opacity, blend and masks, even while they stay
    // inside a drawing folder below its linework and move with it.
    public static Dictionary<Guid, LayerCategory> Categories(Document doc)
    {
        var children = doc.Layers.ToLookup(l => l.ParentId); var result = new Dictionary<Guid, LayerCategory>();
        LayerCategory Kind(Layer layer)
        {
            if (result.TryGetValue(layer.Id, out var existing)) return existing;
            if (layer.Kind == LayerKind.Material) return result[layer.Id] = LayerCategory.Photo;
            var category = layer.Category;
            if (category == LayerCategory.Automatic) category = layer.Kind == LayerKind.Group
                ? (children[layer.Id].Any(l => Kind(l) == LayerCategory.Drawing) ? LayerCategory.Drawing : LayerCategory.Photo)
                : layer.Kind is LayerKind.Vector or LayerKind.Shape or LayerKind.Text ? LayerCategory.Drawing : LayerCategory.Photo;
            result[layer.Id] = category; return category;
        }
        void Assign(Layer layer, LayerCategory category)
        { result[layer.Id] = layer.Kind == LayerKind.Material ? LayerCategory.Photo : category; foreach (var child in children[layer.Id]) Assign(child, category); }
        foreach (var root in children[null]) Assign(root, Kind(root));
        return result;
    }
    /// <summary>A photo layer (hatch material) kept inside a drawing folder. The photo tab lists it on its own.</summary>
    public static bool IsNestedPhoto(Layer layer, IReadOnlyDictionary<Guid, LayerCategory> categories) =>
        layer.ParentId is { } parent && categories.GetValueOrDefault(layer.Id) == LayerCategory.Photo && categories.GetValueOrDefault(parent) == LayerCategory.Drawing;
}
