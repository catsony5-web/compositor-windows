using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Coverage for a layer of width × height pixels, each covering <c>scale</c> document pixels.</summary>
public delegate byte[] MaskSource(int width, int height, double scale);

/// <summary>Replaceable services a style uses: the subject cut-out (local AI background removal) and the year for placeholders.</summary>
public sealed class StyleServices
{
    public static readonly StyleServices Default = new();
    public Func<Raster, CancellationToken, byte[]> SubjectMask { get; init; } = (image, token) => BackgroundRemoval.CreateMask(image, cancellationToken: token);
    public int Year { get; init; } = DateTime.Now.Year;
}

/// <summary>What a recipe works from: the document as it looks without style folders (only the targets shown), its kind and the parameter values.</summary>
public sealed class StyleContext(Document source, bool drawing, IReadOnlyDictionary<string, double> values, StyleServices services, CancellationToken token)
{
    public Document Source { get; } = source;
    public int Width => Source.Width;
    public int Height => Source.Height;
    public bool IsDrawing { get; } = drawing;
    public IReadOnlyDictionary<string, double> Values { get; } = values;
    public StyleServices Services { get; } = services;
    public CancellationToken Token { get; } = token;
    public List<string> Notes { get; } = [];
    public double Value(string key) => Values.TryGetValue(key, out var value) ? value : 0;
    /// <summary>Slider value as 0–1.</summary>
    public double Unit(string key) => Math.Clamp(Value(key) / 100, 0, 1);
    Raster? composite;
    /// <summary>The targets rendered at document size, as the style folder sees them below it.</summary>
    public Raster Composite => composite ??= DesignRenderer.RenderOutput(Source, Token);
}

// Every primitive a design style recipe uses goes through this kit: adjustment layers, pattern and
// material fills of detected regions, texture layers, text, shapes and the subject cut-out. The
// recipes (StyleRecipes) only describe a look in these terms. New effects being built on other
// branches replace the bodies here; each such place carries an "upgrade:" note naming the primitive.
public sealed class StyleKit
{
    readonly Raster blank;
    readonly Guid groupId;
    public StyleContext Context { get; }
    /// <summary>The layers made so far, bottom to top, all children of the style folder.</summary>
    public List<Layer> Layers { get; } = [];
    /// <summary>Existing layers the style changed, with the values to restore.</summary>
    public List<StyleEdit> Edits { get; } = [];
    readonly Document document;
    readonly string seedKey;

    public StyleKit(Document document, Guid groupId, Raster blank, StyleContext context, string seedKey)
    {
        this.document = document; this.groupId = groupId; this.blank = blank; Context = context; this.seedKey = seedKey;
    }

    int Width => Context.Width;
    int Height => Context.Height;
    CancellationToken Token => Context.Token;

    Layer Add(Layer layer, string name, BlendMode blend = BlendMode.Normal, double opacity = 1)
    {
        layer.Name = Loc.T(name); layer.ParentId = groupId; layer.Blend = blend; layer.Opacity = Math.Clamp(opacity, 0, 1);
        if (layer.Kind != LayerKind.Text && layer.Kind != LayerKind.Shape) layer.Category = LayerCategory.Photo;
        Layers.Add(layer); return layer;
    }

    // ---- Adjustment layers --------------------------------------------------------------------

    // Adjustments share the folder's empty surface: it is never drawn into, only its size is used as coverage.
    Layer Adjustment(string name, AdjustmentSpec spec, double opacity, BlendMode blend, bool clipped, byte[]? mask)
    {
        spec.Validate();
        var layer = new Layer { Kind = LayerKind.Adjustment, Adjustment = spec.Snapshot(), Pixels = blank, Clipped = clipped };
        if (mask != null) layer.Mask = mask.Length == (long)Width * Height ? mask : throw new ArgumentException("마스크 크기가 문서와 다릅니다.");
        return Add(layer, name, blend, opacity);
    }

    public Layer AddGradientMap(string name, uint dark, uint light, double opacity = 1, bool clipped = false, BlendMode blend = BlendMode.Normal) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.GradientMap, DarkColor = dark, LightColor = light }, opacity, blend, clipped, null);

    public Layer AddLevels(string name, double black, double white, double gamma = 1, double outputBlack = 0, double outputWhite = 255, double opacity = 1) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.Levels, Black = Math.Clamp(black, 0, 254), White = Math.Clamp(white, black + 1, 255), Gamma = Math.Clamp(gamma, .1, 9.99),
            OutputBlack = Math.Clamp(outputBlack, 0, 255), OutputWhite = Math.Clamp(outputWhite, 0, 255) }, opacity, BlendMode.Normal, false, null);

    public Layer AddCurves(string name, CurvePoint[] master, CurvePoint[]? red = null, CurvePoint[]? green = null, CurvePoint[]? blue = null, bool clipped = false, double opacity = 1) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.Curves, Curve = master, RedCurve = red ?? [new(0, 0), new(1, 1)], GreenCurve = green ?? [new(0, 0), new(1, 1)], BlueCurve = blue ?? [new(0, 0), new(1, 1)] },
            opacity, BlendMode.Normal, clipped, null);

    public Layer AddHueSaturation(string name, double hue, double saturation, double lightness = 0, double opacity = 1) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.HueSaturation, Hue = hue, Saturation = Math.Clamp(saturation, -100, 100), Lightness = Math.Clamp(lightness, -100, 100) }, opacity, BlendMode.Normal, false, null);

    public Layer AddExposure(string name, double ev, byte[]? mask = null, double opacity = 1) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.Exposure, Exposure = Math.Clamp(ev, -20, 20) }, opacity, BlendMode.Normal, false, mask);

    /// <summary>A soft S-curve: 0 keeps the image, 1 is strong contrast.</summary>
    public static CurvePoint[] Contrast(double amount)
    {
        double a = Math.Clamp(amount, 0, 1) * .16;
        return [new(0, 0), new(.25, Math.Max(.01, .25 - a)), new(.5, .5), new(.75, Math.Min(.99, .75 + a)), new(1, 1)];
    }

    // ---- Black and white --------------------------------------------------------------------

    /// <summary>Line work and screens snapped toward pure black and white.</summary>
    // upgrade: 한계값 (threshold) adjustment layer from codex/style-effects, with the level as its parameter.
    public Layer AddThreshold(string name, double level, double softness = 24) =>
        AddLevels(name, Math.Clamp(level - softness, 0, 250), Math.Clamp(level + softness, 4, 255));

    // ---- Texture layers -------------------------------------------------------------------------

    /// <summary>A seeded texture layer at document resolution (half resolution for very large canvases).</summary>
    public Layer AddTexture(string name, TextureKind kind, double amount, BlendMode blend, double opacity = 1)
    {
        int factor = (long)Width * Height > 24_000_000 ? 2 : 1;
        int w = (Width + factor - 1) / factor, h = (Height + factor - 1) / factor;
        var pixels = StyleTextures.Render(kind, w, h, factor, amount, Seed(name), Token);
        var layer = new Layer { Kind = LayerKind.Raster, Pixels = pixels, Scale = factor };
        return Add(layer, name, blend, opacity);
    }

    // upgrade: 종이·인쇄 질감 adjustment (paper fibres, photocopy toner, rough edges) from codex/style-effects.
    public Layer AddGrain(string name, TextureKind kind, double amount, BlendMode blend, double opacity = 1) => AddTexture(name, kind, amount, blend, opacity);

    // ---- Copies of the image ------------------------------------------------------------------

    /// <summary>The targets blurred by <paramref name="radius"/> document pixels, kept at a reduced size (it is soft anyway), shown through <paramref name="mask"/> (document size).</summary>
    public Layer AddBlurredCopy(string name, double radius, MaskSource? mask = null)
    {
        int factor = Math.Clamp((int)Math.Floor(radius / 6), 1, 8);
        int w = Math.Max(1, (Width + factor - 1) / factor), h = Math.Max(1, (Height + factor - 1) / factor);
        var small = factor == 1 ? Context.Composite.Clone() : ImportExport.Resize(Context.Composite, w, h);
        var blurred = StyleTextures.BoxBlur(small, Math.Max(1, radius / factor), Token);
        var layer = new Layer { Kind = LayerKind.Raster, Pixels = blurred, Scale = factor };
        if (mask != null) layer.Mask = mask(w, h, factor);
        return Add(layer, name);
    }

    /// <summary>The subject cut out with the local AI background removal: the layer (cropped to it) and its confident core in document pixels.</summary>
    public sealed record Subject(Layer Layer, Int32Rect Core);

    /// <summary>The main subject of the image cut out with the local AI background removal, cropped to it.</summary>
    // upgrade: "피사체를 글자 앞으로" from codex/text-poster (cut-out placed above a text layer).
    public Subject? CutOutSubject(string name)
    {
        var image = Context.Composite; int width = image.Width, height = image.Height;
        var raw = Context.Services.SubjectMask(image, Token);
        if (raw.Length != width * height) throw new InvalidOperationException("피사체 마스크 크기가 이미지와 다릅니다.");
        // Tighten the model's soft matte so the cut-out has a printed edge.
        var mask = new byte[raw.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                double t = Math.Clamp((raw[i] / 255d - .42) / .2, 0, 1); t = t * t * (3 - 2 * t);
                mask[i] = Imaging.Byte(t * image.Data[i * 4 + 3]);
            }
        });
        // Keep the main subject: the largest confident area and those at least a sixth of its size.
        var label = new int[raw.Length]; var areas = new List<int> { 0 }; var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] < 128 || label[start] != 0) continue;
            int id = areas.Count, area = 0; label[start] = id; stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % width; area++;
                void Visit(int j) { if (mask[j] >= 128 && label[j] == 0) { label[j] = id; stack.Push(j); } }
                if (x > 0) Visit(i - 1); if (x < width - 1) Visit(i + 1); if (i >= width) Visit(i - width); if (i + width < mask.Length) Visit(i + width);
            }
            areas.Add(area);
        }
        int largest = areas.Max();
        if (largest < (long)width * height / 300) { Context.Notes.Add("사진에서 피사체를 찾지 못해 제목 앞 피사체 레이어는 만들지 않았습니다."); return null; }
        var kept = areas.Select(a => a >= largest / 6).ToArray();
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        int coreLeft = int.MaxValue, coreTop = int.MaxValue, coreRight = -1, coreBottom = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            // Soft edges stay with the kept areas; everything else is dropped.
            if (mask[i] == 0) continue;
            bool core = label[i] != 0 && kept[label[i]];
            if (!core && !NearKept(x, y)) { mask[i] = 0; continue; }
            if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
            if (core) { if (x < coreLeft) coreLeft = x; if (x > coreRight) coreRight = x; if (y < coreTop) coreTop = y; if (y > coreBottom) coreBottom = y; }
        }
        bool NearKept(int x, int y)
        {
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
            {
                int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                int l = label[yy * width + xx]; if (l != 0 && kept[l]) return true;
            }
            return false;
        }
        int w = right - left + 1, h = bottom - top + 1; var pixels = new Raster(w, h); var cropped = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(image.Data, ((top + y) * width + left) * 4, pixels.Data, y * w * 4, w * 4);
            Buffer.BlockCopy(mask, (top + y) * width + left, cropped, y * w, w);
        }
        var layer = new Layer { Kind = LayerKind.Raster, Pixels = pixels, Mask = cropped, X = left, Y = top };
        Add(layer, name);
        return new Subject(layer, new Int32Rect(coreLeft, coreTop, coreRight - coreLeft + 1, coreBottom - coreTop + 1));
    }

    /// <summary>Moves a layer made by this kit directly below another one.</summary>
    public void MoveBelow(Layer layer, Layer anchor)
    {
        if (!Layers.Remove(layer)) return;
        Layers.Insert(Math.Max(0, Layers.IndexOf(anchor)), layer);
    }

    // ---- Pattern fills of regions ---------------------------------------------------------------

    /// <summary>Solid ink coverage tile: black poché.</summary>
    // upgrade: solid black poché screentone pattern from codex/screentone-patterns.
    public static MaterialAsset Poche => poche.Value;
    static readonly Lazy<MaterialAsset> poche = new(() => LinePatterns.Create("포셰", Raster.Solid(16, 16, Colors.Black), StableId("poche")));

    /// <summary>One round dot per tile; its size follows the fill's line weight (see <see cref="DotWeight"/>).</summary>
    // upgrade: regular dot screens by density from codex/screentone-patterns.
    public static MaterialAsset DotScreen => dots.Value;
    static readonly Lazy<MaterialAsset> dots = new(() =>
    {
        var tile = Imaging.Draw(DotTile, DotTile, dc => dc.DrawEllipse(Brushes.Black, null, new Point(DotTile / 2d, DotTile / 2d), DotRadius, DotRadius));
        return LinePatterns.Create("망점", tile, StableId("dots"));
    });
    const int DotTile = 48; const double DotRadius = 12;

    /// <summary>Line weight that gives the dot screen about <paramref name="coverage"/> (0–1) ink.</summary>
    public static double DotWeight(double coverage)
    {
        double radius = DotTile * Math.Sqrt(Math.Clamp(coverage, .01, .7) / Math.PI);
        double stroke = LinePatternRenderer.Stroke(DotScreen);
        return Math.Clamp(1 + 2 * (radius - DotRadius) / stroke, .1, 8);
    }

    static Guid StableId(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("morupixel:style-asset/" + key + "@1"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// A material layer filling the outlines (document pixels, even-odd) with a pattern. Returns
    /// null when nothing is left to fill. Outline detail is reduced until the region fits the
    /// material region limits; very large canvases are filled at a reduced scale.
    /// </summary>
    public Layer? FillRegions(string name, IReadOnlyList<Point[]> outlines, MaterialAsset asset, double tile, double angle = 0, double lineWeight = 1, uint ink = 0xFF000000,
        MaskSource? mask = null, BlendMode blend = BlendMode.Multiply, double opacity = 1)
    {
        if (outlines.Count == 0) return null;
        double left = outlines.Min(o => o.Min(p => p.X)), top = outlines.Min(o => o.Min(p => p.Y));
        double right = outlines.Max(o => o.Max(p => p.X)), bottom = outlines.Max(o => o.Max(p => p.Y));
        left = Math.Max(0, Math.Floor(left)); top = Math.Max(0, Math.Floor(top));
        right = Math.Min(Width, Math.Ceiling(right)); bottom = Math.Min(Height, Math.Ceiling(bottom));
        if (right - left < 1 || bottom - top < 1) return null;
        int factor = 1;
        while (Math.Ceiling((right - left) / factor) > MaterialEditing.MaxSide || Math.Ceiling((bottom - top) / factor) > MaterialEditing.MaxSide ||
            Math.Ceiling((right - left) / factor) * Math.Ceiling((bottom - top) / factor) > MaterialEditing.MaxPixels) factor++;
        int width = Math.Max(1, (int)Math.Ceiling((right - left) / factor)), height = Math.Max(1, (int)Math.Ceiling((bottom - top) / factor));
        string? data = null;
        foreach (double detail in new[] { .1, .5, 1.5, 4 })
        {
            data = PathData(outlines, left, top, factor, width, height, detail);
            if (data != null && Encoding.UTF8.GetByteCount(data) <= MaterialEditing.MaxPathBytes) break;
            data = null;
        }
        if (data == null)
        {
            // Too many separate areas for one region: keep the largest ones.
            var largest = outlines.OrderByDescending(o => Math.Abs(RegionDetection.SignedArea(o))).Take(400).ToArray();
            data = PathData(largest, left, top, factor, width, height, 4);
            if (data == null || Encoding.UTF8.GetByteCount(data) > MaterialEditing.MaxPathBytes) { Context.Notes.Add($"{Loc.T(name)}: 영역이 너무 복잡해 채우지 못했습니다."); return null; }
            Context.Notes.Add($"{Loc.T(name)}: 작은 영역 일부는 채우지 않았습니다.");
        }
        var boundary = new RegionPath(data);
        try { _ = boundary.Geometry; } catch (System.IO.InvalidDataException) { return null; }
        var fill = new MaterialFill(asset, StableRegionId(name), Loc.T(name), boundary, width, height, tile / factor, tile / factor * MaterialEditing.Aspect(asset), angle, 0, 0, ink, lineWeight, 0);
        MaterialEditing.ValidateFill(fill, new Raster(1, 1), false);
        Token.ThrowIfCancellationRequested();
        var layer = new Layer { Kind = LayerKind.Material, Material = fill, X = left, Y = top, Scale = factor, Pixels = MaterialRenderer.Render(fill) };
        if (mask != null) layer.Mask = mask(width, height, factor);
        return Add(layer, name, blend, opacity);
    }

    Guid StableRegionId(string name)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seedKey + "/region/" + name + "/" + Layers.Count));
        return new Guid(hash.AsSpan(0, 16));
    }

    // Region outline in layer pixels, one decimal place, even-odd. Null when nothing closed is left.
    static string? PathData(IReadOnlyList<Point[]> outlines, double left, double top, int factor, int width, int height, double detail)
    {
        var text = new StringBuilder(); int figures = 0;
        foreach (var outline in outlines)
        {
            var points = outline.Select(p => new Point(Math.Clamp((p.X - left) / factor, 0, width), Math.Clamp((p.Y - top) / factor, 0, height))).ToList();
            if (detail > .2) points = RegionDetection.Simplify(points, detail / factor);
            if (points.Count < 3 || Math.Abs(RegionDetection.SignedArea(points)) < .5) continue;
            text.Append(figures == 0 ? "F0 M" : " M");
            for (int i = 0; i < points.Count; i++)
            {
                if (i == 1) text.Append(" L");
                text.Append(' ').Append(points[i].X.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(points[i].Y.ToString("0.#", CultureInfo.InvariantCulture));
            }
            text.Append(" Z"); figures++;
        }
        return figures == 0 ? null : text.ToString();
    }

    /// <summary>A pattern over the whole canvas (a full rectangle region).</summary>
    public Layer? FillCanvas(string name, MaterialAsset asset, double tile, uint ink, double lineWeight, BlendMode blend, double opacity) =>
        FillRegions(name, [[new Point(0, 0), new Point(Width, 0), new Point(Width, Height), new Point(0, Height)]], asset, tile, 0, lineWeight, ink, null, blend, opacity);

    // ---- Masks ----------------------------------------------------------------------------------

    /// <summary>A coverage that fades along the region's longer side, dithered into dots: a stipple gradient.</summary>
    // upgrade: dot/stipple gradient fill for regions from codex/screentone-patterns.
    public MaskSource StippleGradient(double density, bool reverse)
    {
        uint seed = Seed("stipple");
        return (w, h, _) =>
        {
            var mask = new byte[w * h]; bool vertical = h >= w * .8;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double t = vertical ? (y + .5) / h : (x + .5) / w; if (reverse) t = 1 - t;
                    double d = Math.Clamp(Math.Pow(1 - t, 1.35) * density + .015, 0, 1);
                    // Clumps of two pixels mixed with single ones, like toner on a photocopy.
                    double n = StyleTextures.Hash(x >> 1, y >> 1, seed) * .6 + StyleTextures.Hash(x, y, seed ^ 0x9E37u) * .4;
                    mask[y * w + x] = n < d ? (byte)255 : (byte)0;
                }
            });
            return mask;
        };
    }

    /// <summary>Document-size coverage that is 1 at the edges and 0 inside, with an irregular, brushed inner border.</summary>
    // upgrade: rough / burned edges of the paper texture adjustment from codex/style-effects.
    public byte[] EdgeMask(double inset, double softness)
    {
        uint seed = Seed("edges"); var mask = new byte[Width * Height]; double scale = Math.Max(Width, Height);
        Parallel.For(0, Height, y =>
        {
            for (int x = 0; x < Width; x++)
            {
                double edge = Math.Min(Math.Min(x + .5, Width - x - .5), Math.Min(y + .5, Height - y - .5)) / scale;
                double wobble = (StyleTextures.Smooth(x / (scale * .05), y / (scale * .05), seed) - .5) * .7 * inset + (StyleTextures.Smooth(x / (scale * .008), y / (scale * .008), seed ^ 77) - .5) * .35 * inset
                    + (StyleTextures.Smooth(x / (scale * .0016), y / (scale * .0016), seed ^ 91) - .5) * .12 * inset;
                double t = Math.Clamp((inset + wobble - edge) / Math.Max(1e-6, softness), 0, 1);
                mask[y * Width + x] = Imaging.Byte(t * t * (3 - 2 * t) * 255);
            }
        });
        return mask;
    }

    /// <summary>A rectangle in document pixels as the coverage of a layer drawn from (0, 0) at the given scale.</summary>
    public static MaskSource RectangleMask(Rect area) => (w, h, scale) =>
    {
        var mask = new byte[w * h];
        var r = new Rect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double cx = Math.Clamp(Math.Min(x + 1, r.Right) - Math.Max(x, r.Left), 0, 1), cy = Math.Clamp(Math.Min(y + 1, r.Bottom) - Math.Max(y, r.Top), 0, 1);
            mask[y * w + x] = Imaging.Byte(cx * cy * 255);
        }
        return mask;
    };

    // ---- Text and shapes -----------------------------------------------------------------------

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> fonts = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lazy<HashSet<string>> installed = new(() => Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static bool HasFont(string family) => fonts.GetOrAdd(family, name => installed.Value.Contains(name));
    public static bool HasHangul(string text) => text.Any(c => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㅣ');

    public static (double Width, double Height) Measure(TextSpec spec)
    {
        var layout = DocumentFeatures.TextDrawing(spec); return (layout.Width - 8, layout.Height - 8);
    }

    /// <summary>An editable text layer; the layer scale takes over beyond the largest font size.</summary>
    // upgrade: text outline (stroke / outline-only) and paragraph box width from codex/text-poster.
    public Layer AddText(string name, TextSpec spec, double x, double y, double scale = 1)
    {
        spec.Validate();
        var layer = DocumentFeatures.CreateText(spec, x, y); layer.Scale = Math.Clamp(scale, .01, 20);
        return Add(layer, name);
    }

    /// <summary>Font size (with a layer scale beyond 1024 px) that makes the text <paramref name="width"/> pixels wide.</summary>
    public static (double Size, double Scale) FitWidth(TextSpec spec, double width, double maxHeight)
    {
        var probe = spec with { FontSize = 200, LineHeight = spec.LineHeight > 0 ? spec.LineHeight / Math.Max(1, spec.FontSize) * 200 : 0 };
        var (w, h) = Measure(probe);
        double size = 200 * Math.Min(width / Math.Max(1, w), maxHeight / Math.Max(1, h));
        return size <= 1024 ? (Math.Max(4, size), 1) : (1024, Math.Min(20, size / 1024));
    }

    public Layer AddShape(string name, ShapeSpec spec, double x, double y, double opacity = 1, BlendMode blend = BlendMode.Normal)
    {
        var layer = VectorShapes.Create(spec, x, y);
        return Add(layer, name, blend, opacity);
    }

    // ---- Edits to existing layers -----------------------------------------------------------

    /// <summary>Hides an existing layer while the style is applied; removing the style shows it again.</summary>
    public void Hide(Layer existing)
    {
        if (!existing.Visible) return;
        Edits.Add(new StyleEdit(existing.Id, true)); existing.Visible = false;
    }

    /// <summary>A deterministic seed per style, folder purpose and parameter set.</summary>
    public uint Seed(string purpose)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seedKey + "/" + purpose));
        return BitConverter.ToUInt32(hash, 0);
    }

    /// <summary>The document's own layers (not style folders) in the analysis, by id.</summary>
    public Layer? Original(Guid id) => document.Layers.FirstOrDefault(l => l.Id == id);
}
