namespace Compositor.Windows;

// Small renders of the current document in each design style, for the gallery. The proxy is a
// miniature of the document itself (style folders left out, root layers scaled down), so a recipe
// that reads layers (the screentone plan: line work, hatch fills) sees the same structure as when the
// style is applied. The others only read how the document looks: they style a flattened copy of the
// miniature, drawn once, so a large drawing is not drawn again for every card and setting. Work runs
// on an STA thread and is cancellable.
public sealed class StylePreview
{
    public Document Proxy { get; }
    public bool IsDrawing { get; }
    RegionMap? regions;
    readonly Lazy<Document> flat;
    StylePreview(Document proxy, bool drawing, StyleServices services, double reduction)
    {
        Proxy = proxy; IsDrawing = drawing;
        // The poster's subject cut-out is remembered per image (pass the same memoized services to every
        // miniature of a gallery to share it), and the line work does not change while the gallery is open:
        // its rooms are found once (previews have no targets).
        var memoized = services.Memoized();
        Services = new StyleServices
        {
            Year = services.Year, Reduction = reduction, SubjectMask = memoized.SubjectMask, Drawing = drawing,
            LineRegions = context => regions ??= services.LineRegions(context),
        };
        flat = new(() => Flatten(proxy), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The services used for preview renders (subject cut-outs and rooms are found once, the document kind is the original's).</summary>
    public StyleServices Services { get; }

    // One plain photo already is its own flat copy; anything else is drawn once into a single image.
    static Document Flatten(Document proxy)
    {
        if (proxy.Layers.Count == 1 && proxy.Layers[0] is { Kind: LayerKind.Raster, ParentId: null, Mask: null, Warp: null, Clipped: false, Blend: BlendMode.Normal, Visible: true } only && only.Opacity >= 1) return proxy;
        var copy = new Document { Name = proxy.Name, Width = proxy.Width, Height = proxy.Height, Dpi = proxy.Dpi };
        copy.Add(new Layer { Name = proxy.Name, Pixels = DesignRenderer.RenderOutput(proxy) });
        return copy;
    }

    /// <summary>A miniature of <paramref name="document"/> whose long side is at most <paramref name="maxSide"/>.</summary>
    public static StylePreview Create(Document document, int maxSide, StyleServices? services = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var proxy = document.Snapshot();
        var styled = proxy.Layers.Where(DesignStyles.IsStyleGroup).Select(l => l.Id).ToList();
        foreach (var id in styled) DocumentFeatures.Remove(proxy, id);
        bool drawing = DesignStyleEngine.IsDrawing(proxy, null);
        double factor = Math.Min(1, maxSide / (double)Math.Max(document.Width, document.Height));
        proxy.Width = Math.Max(1, (int)Math.Round(document.Width * factor)); proxy.Height = Math.Max(1, (int)Math.Round(document.Height * factor));
        if (factor < 1)
            foreach (var root in proxy.Layers.Where(l => l.ParentId == null))
            {
                // Same as an image resize: positions and scales follow the canvas (a tiny photo stays visible).
                root.X *= factor; root.Y *= factor; root.Scale = Math.Clamp(root.Scale * factor, .01, 20);
            }
        proxy.Artboards = [];
        return new StylePreview(proxy, drawing, services ?? StyleServices.Default, 1 / factor);
    }

    /// <summary>The proxy (or its flattened copy, for a style that does not read layers) with the style applied, rendered.</summary>
    public Raster Render(string styleId, IReadOnlyDictionary<string, double>? values, CancellationToken token = default)
    {
        var copy = (DesignStyles.Find(styleId)?.ReadsLayers != false ? Proxy : flat.Value).Snapshot();
        DesignStyleEngine.Apply(copy, new StyleRequest(styleId, values), Services, token);
        token.ThrowIfCancellationRequested();
        return DesignRenderer.RenderOutput(copy, token);
    }

    /// <summary>The proxy as it is (no style).</summary>
    public Raster RenderOriginal(CancellationToken token = default) => DesignRenderer.RenderOutput(Proxy, token);
}
