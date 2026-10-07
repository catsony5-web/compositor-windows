namespace Compositor.Windows;

// Small renders of the current document in each design style, for the gallery. The proxy is a
// miniature of the document itself (style folders left out, root layers scaled down), so the
// recipes read the same structure — drawing layers, roles, materials — as they will when the
// style is applied, at a fraction of the cost. Work runs on an STA thread and is cancellable.
public sealed class StylePreview
{
    public Document Proxy { get; }
    public bool IsDrawing { get; }
    readonly Lazy<byte[]> subject;
    RegionMap? regions;
    StylePreview(Document proxy, bool drawing, StyleServices services, double reduction)
    {
        Proxy = proxy; IsDrawing = drawing;
        // The subject cut-out of the poster is found once per proxy and reused for every parameter change.
        subject = new(() => services.SubjectMask(DesignRenderer.RenderOutput(DesignStyleEngine.AnalysisDocument(proxy, null)), CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication);
        Services = new StyleServices
        {
            Year = services.Year, Reduction = reduction,
            SubjectMask = (image, token) => image.Width == proxy.Width && image.Height == proxy.Height ? subject.Value : services.SubjectMask(image, token),
            // The line work does not change while the gallery is open: its rooms are found once (previews have no targets).
            LineRegions = context => regions ??= services.LineRegions(context),
        };
    }

    /// <summary>The services used for preview renders (the subject cut-out is cached).</summary>
    public StyleServices Services { get; }

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

    /// <summary>The proxy with the style applied, rendered.</summary>
    public Raster Render(string styleId, IReadOnlyDictionary<string, double>? values, CancellationToken token = default)
    {
        var copy = Proxy.Snapshot();
        DesignStyleEngine.Apply(copy, new StyleRequest(styleId, values), Services, token);
        token.ThrowIfCancellationRequested();
        return DesignRenderer.RenderOutput(copy, token);
    }

    /// <summary>The proxy as it is (no style).</summary>
    public Raster RenderOriginal(CancellationToken token = default) => DesignRenderer.RenderOutput(Proxy, token);
}
