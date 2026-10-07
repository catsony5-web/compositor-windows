using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Coverage for a layer of width × height pixels, each covering <c>scale</c> document pixels.</summary>
public delegate byte[] MaskSource(int width, int height, double scale);

/// <summary>Replaceable services a style uses: the subject cut-out (local AI background removal), the closed areas of the line work and the year for placeholders.</summary>
public sealed class StyleServices
{
    public static readonly StyleServices Default = new();
    public Func<Raster, CancellationToken, byte[]> SubjectMask { get; init; } = (image, token) => BackgroundRemoval.CreateMask(image, cancellationToken: token);
    /// <summary>Closed areas of the line work (the screentone plan); the gallery finds them once per miniature.</summary>
    public Func<StyleContext, RegionMap?> LineRegions { get; init; } = StyleRecipes.LineRegions;
    public int Year { get; init; } = DateTime.Now.Year;
    /// <summary>How many times larger the real document is than the one being styled: above 1 for a gallery miniature, so sizes with a minimum keep their proportion.</summary>
    public double Reduction { get; init; } = 1;
    /// <summary>Whether the document counts as a drawing, when the styled document cannot tell (a gallery's flattened miniature); null: decided from its layers.</summary>
    public bool? Drawing { get; init; }

    bool memoized;
    /// <summary>
    /// The same services, with each subject cut-out remembered for the image it was found in (a photo layer or a
    /// composite, recognised by its size and sampled pixels): gallery previews render the same image again for
    /// every style and setting. The model runs to completion once started, so a closed gallery never caches half a mask.
    /// </summary>
    public StyleServices Memoized()
    {
        if (memoized) return this;
        var masks = new System.Collections.Concurrent.ConcurrentDictionary<(int, int, ulong), Lazy<byte[]>>(); var find = SubjectMask;
        return new StyleServices
        {
            SubjectMask = (image, _) => masks.GetOrAdd(Fingerprint(image), _ => new Lazy<byte[]>(() => find(image, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication)).Value,
            LineRegions = LineRegions, Year = Year, Reduction = Reduction, Drawing = Drawing, memoized = true
        };
    }

    // Size and an FNV-1a hash of about 4,000 pixels spread over the image.
    static (int, int, ulong) Fingerprint(Raster image)
    {
        ulong hash = 14695981039346656037UL; int pixels = image.Width * image.Height, step = Math.Max(1, pixels / 4096);
        for (int p = 0; p < pixels; p += step)
            for (int c = 0; c < 4; c++) { hash ^= image.Data[p * 4 + c]; hash *= 1099511628211UL; }
        return (image.Width, image.Height, hash);
    }
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
    /// <summary>How many times larger the real document is (1 unless this is a gallery miniature).</summary>
    public double Reduction => Math.Max(1, Services.Reduction);
    /// <summary>A size as a fraction of the long side, clamped in pixels of the real document (a miniature scales the result down).</summary>
    public double Measure(double fraction, double min, double max) => Math.Clamp(Math.Max(Width, Height) * Reduction * fraction, min, max) / Reduction;
    /// <summary>At least <paramref name="min"/> pixels of the real document.</summary>
    public double AtLeast(double value, double min) => Math.Max(value, min / Reduction);
    /// <summary>Slider value as 0–1.</summary>
    public double Unit(string key) => Math.Clamp(Value(key) / 100, 0, 1);
    Raster? composite;
    /// <summary>The targets rendered at document size, as the style folder sees them below it.</summary>
    public Raster Composite => composite ??= DesignRenderer.RenderOutput(Source, Token);
}

// Every primitive a design style recipe uses goes through this kit: adjustment layers (also the
// threshold, halftone, paper texture and glow effects), screentone fills of detected regions, text,
// shapes, blurred copies and the subject in front of a title. The recipes (StyleRecipes) only
// describe a look in these terms, and every layer they make stays editable in the usual dialogs.
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

    // Adjustments share the folder's empty surface: it is never drawn into, only its size is used as coverage
    // (and as the page whose edges the paper texture wears).
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

    /// <summary>A soft S-curve: 0 keeps the image, 1 is strong contrast.</summary>
    public static CurvePoint[] Contrast(double amount)
    {
        double a = Math.Clamp(amount, 0, 1) * .16;
        return [new(0, 0), new(.25, Math.Max(.01, .25 - a)), new(.5, .5), new(.75, Math.Min(.99, .75 + a)), new(1, 1)];
    }

    // ---- Style effects (한계값 · 망점 · 종이·인쇄 질감 · 빛 번짐) ---------------------------------------

    /// <summary>한계값: luminance from <paramref name="level"/> up becomes white, below it black, with a soft ramp of <paramref name="smoothness"/> levels.</summary>
    // A gallery miniature has averaged hairlines and screens into greys; its ramp widens with the reduction so they keep their tone.
    public Layer AddThreshold(string name, double level, double smoothness = 0, bool clipped = false) =>
        Adjustment(name, new AdjustmentSpec
        {
            Kind = AdjustmentKind.Threshold,
            Threshold = new ThresholdSpec { Level = Math.Clamp(level, 0, 255), Smoothness = Math.Clamp(smoothness * Context.Reduction, 0, 64) }
        }, 1, BlendMode.Normal, clipped, null);

    /// <summary>망점: the darkness below printed as dots of <paramref name="ink"/> on <paramref name="paper"/>, <paramref name="cell"/> document pixels apart.</summary>
    // Cells smaller than a couple of device pixels (a miniature, a zoomed-out view) show their mean tone.
    public Layer AddHalftone(string name, double cell, double angle, uint ink, uint paper, HalftoneShape shape = HalftoneShape.Round, bool clipped = false) =>
        Adjustment(name, new AdjustmentSpec
        {
            Kind = AdjustmentKind.Halftone,
            Halftone = new HalftoneSpec { CellSize = Math.Clamp(cell, 2, 256), Angle = angle, Shape = shape, InkArgb = ink, PaperArgb = paper }
        }, 1, BlendMode.Normal, clipped, null);

    /// <summary>종이·인쇄 질감 with its own seed; <see cref="PaperTextureSpec.Scale"/> should come from <see cref="Grain"/>.</summary>
    public Layer AddPaper(string name, PaperTextureSpec spec, double opacity = 1) =>
        Adjustment(name, new AdjustmentSpec { Kind = AdjustmentKind.PaperTexture, Paper = spec with { Seed = (int)(Seed(name) % 999_983) + 1 } }, opacity, BlendMode.Normal, false, null);

    /// <summary>The finest paper grain for this document: <paramref name="fraction"/> of the long side, in the texture's 0.5–32 px range.</summary>
    public double Grain(double fraction = 1 / 1100d) => Math.Clamp(Context.Measure(fraction, .5, 32), .5, 32);

    /// <summary>빛 번짐: highlights above <paramref name="threshold"/> (0–1) spread over <paramref name="radius"/> document pixels.</summary>
    public Layer AddGlow(string name, double threshold, double radius, double intensity, uint tint = 0x00FFFFFF) =>
        Adjustment(name, new AdjustmentSpec
        {
            Kind = AdjustmentKind.Glow,
            Glow = new GlowSpec { Threshold = Math.Clamp(threshold, 0, 1), Radius = Math.Clamp(radius, 1, 1000), Intensity = Math.Clamp(intensity, 0, 4), TintArgb = tint }
        }, 1, BlendMode.Normal, false, null);

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

    /// <summary>The subject layer in front of a title and its confident core in document pixels.</summary>
    public sealed record Subject(Layer Layer, Int32Rect Core);

    /// <summary>
    /// 피사체를 글자 앞으로: the image's main subject found with the local AI model, as a layer to stand in front
    /// of a title (<see cref="MoveBelow"/> the title under it). When the image is one photo layer, the cut-out is a
    /// copy of that layer sharing its pixels with the subject as its mask (<see cref="SubjectFront.CutOut"/>);
    /// otherwise it is a copy of what the folder sees below it, cropped to the subject. Null (with a note) when
    /// no subject is found.
    /// </summary>
    public Subject? CutOutSubject(string name)
    {
        Layer layer; Int32Rect core;
        if (SolePhoto() is { } photo)
        {
            var mask = Tighten(Context.Services.SubjectMask(photo.Pixels, Token), photo.Pixels, out var photoCore, out _);
            if (mask == null) return NoSubject();
            layer = SubjectFront.CutOut(photo, mask, Loc.T(name), groupId);
            var bounds = new Rect(photoCore.X, photoCore.Y, photoCore.Width, photoCore.Height); bounds.Transform(photo.Matrix);
            bounds.Intersect(new Rect(0, 0, Width, Height));
            core = bounds.IsEmpty ? default : new Int32Rect((int)bounds.X, (int)bounds.Y, Math.Max(1, (int)bounds.Width), Math.Max(1, (int)bounds.Height));
        }
        else
        {
            var image = Context.Composite;
            var mask = Tighten(Context.Services.SubjectMask(image, Token), image, out core, out var kept);
            if (mask == null) return NoSubject();
            var pixels = new Raster(kept.Width, kept.Height); var cropped = new byte[kept.Width * kept.Height];
            for (int y = 0; y < kept.Height; y++)
            {
                Buffer.BlockCopy(image.Data, ((kept.Y + y) * image.Width + kept.X) * 4, pixels.Data, y * kept.Width * 4, kept.Width * 4);
                Buffer.BlockCopy(mask, (kept.Y + y) * image.Width + kept.X, cropped, y * kept.Width, kept.Width);
            }
            layer = new Layer { Kind = LayerKind.Raster, Pixels = pixels, Mask = cropped, X = kept.X, Y = kept.Y };
        }
        Add(layer, name);
        return new Subject(layer, core);
    }

    /// <summary>Moves a layer made by this kit directly below another one.</summary>
    public void MoveBelow(Layer layer, Layer anchor)
    {
        if (!Layers.Remove(layer)) return;
        Layers.Insert(Math.Max(0, Layers.IndexOf(anchor)), layer);
    }

    Subject? NoSubject() { Context.Notes.Add("사진에서 피사체를 찾지 못해 제목 앞 피사체 레이어는 만들지 않았습니다."); return null; }

    // The single visible leaf of what is read, when it is a plain photo layer at the top level of the document
    // (its copy then stands exactly where it is). Several layers, adjustments or a perspective need the composite.
    Layer? SolePhoto()
    {
        var source = Context.Source; var index = source.Layers.ToDictionary(l => l.Id);
        bool Shown(Layer layer)
        {
            for (Layer? current = layer; current != null; current = current.ParentId is { } p && index.TryGetValue(p, out var next) ? next : null)
                if (!current.Visible || current.Opacity <= 0) return false;
            return true;
        }
        var leaves = source.Layers.Where(l => l.Kind != LayerKind.Group && Shown(l)).Take(2).ToArray();
        return leaves is [{ Kind: LayerKind.Raster, Warp: null, Clipped: false, ParentId: null, Blend: BlendMode.Normal } leaf] && leaf.Opacity >= 1 && Original(leaf.Id) is { Kind: LayerKind.Raster } photo ? photo : null;
    }

    // The model's soft matte cut into a printed edge (a one-pixel ramp around the half-way level), keeping the main
    // subject: the largest confident area and those at least a sixth of its size. Core: the confident kept area;
    // kept: everything kept (image pixels).
    static byte[]? Tighten(byte[] raw, Raster image, out Int32Rect core, out Int32Rect kept)
    {
        int width = image.Width, height = image.Height; core = kept = default;
        if (raw.Length != width * height) throw new InvalidOperationException("피사체 마스크 크기가 이미지와 다릅니다.");
        var mask = new byte[raw.Length];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                double t = Math.Clamp((raw[i] / 255d - .46) / .08, 0, 1); t = t * t * (3 - 2 * t);
                mask[i] = Imaging.Byte(t * image.Data[i * 4 + 3]);
            }
        });
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
        if (largest < (long)width * height / 300) return null;
        var main = areas.Select(a => a >= largest / 6).ToArray();
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        int coreLeft = int.MaxValue, coreTop = int.MaxValue, coreRight = -1, coreBottom = -1;
        bool NearKept(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                int l = label[yy * width + xx]; if (l != 0 && main[l]) return true;
            }
            return false;
        }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            // The edge ramp stays with the kept areas; everything else is dropped.
            if (mask[i] == 0) continue;
            bool inside = label[i] != 0 && main[label[i]];
            if (!inside && !NearKept(x, y)) { mask[i] = 0; continue; }
            if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
            if (inside) { if (x < coreLeft) coreLeft = x; if (x > coreRight) coreRight = x; if (y < coreTop) coreTop = y; if (y > coreBottom) coreBottom = y; }
        }
        core = new Int32Rect(coreLeft, coreTop, coreRight - coreLeft + 1, coreBottom - coreTop + 1);
        kept = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        return mask;
    }

    // ---- Screentone fills of regions ------------------------------------------------------------

    /// <summary>
    /// A material layer filling the outlines (document pixels, even-odd) with a pattern of the library — a dot,
    /// line or grid screen, black poché or a dot/stipple gradient running as <paramref name="gradient"/> says.
    /// Returns null when nothing is left to fill. Outline detail is reduced until the region fits the material
    /// region limits; very large canvases are filled at a reduced scale.
    /// </summary>
    public Layer? FillRegions(string name, IReadOnlyList<Point[]> outlines, MaterialAsset asset, double tile, double angle = 0, uint ink = 0xFF000000,
        ToneGradient? gradient = null, double lineWeight = 1, BlendMode blend = BlendMode.Multiply, double opacity = 1)
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
        var fill = new MaterialFill(asset, StableRegionId(name), Loc.T(name), boundary, width, height, tile / factor, tile / factor * MaterialEditing.Aspect(asset), angle, 0, 0, ink,
            Math.Clamp(lineWeight, .1, 8), 0, gradient);
        MaterialEditing.ValidateFill(fill, new Raster(1, 1), false);
        Token.ThrowIfCancellationRequested();
        var layer = new Layer { Kind = LayerKind.Material, Material = fill, X = left, Y = top, Scale = factor, Pixels = MaterialRenderer.Render(fill) };
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
        FillRegions(name, [[new Point(0, 0), new Point(Width, 0), new Point(Width, Height), new Point(0, Height)]], asset, tile, 0, ink, null, lineWeight, blend, opacity);

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

    // Sizes derived from tiny or very narrow canvases stay within what a text layer accepts.
    static TextSpec Safe(TextSpec spec) => spec with
    {
        FontSize = Math.Clamp(spec.FontSize, 1, 1024), LineHeight = Math.Clamp(spec.LineHeight, 0, 8192),
        OutlineWidth = Math.Clamp(spec.OutlineWidth, .5, TextSpec.MaxOutlineWidth), BoxWidth = spec.BoxWidth <= 0 ? 0 : Math.Clamp(spec.BoxWidth, 1, TextSpec.MaxBoxWidth)
    };

    /// <summary>Size of the text's surface without its 4 px margins (a paragraph box wraps the lines, an outline adds its reach).</summary>
    public static (double Width, double Height) Measure(TextSpec spec)
    {
        var layout = DocumentFeatures.TextDrawing(Safe(spec)); return (layout.Width - 8, layout.Height - 8);
    }

    /// <summary>An editable text layer (with its outline and paragraph box); the layer scale takes over beyond the largest font size.</summary>
    public Layer AddText(string name, TextSpec spec, double x, double y, double scale = 1)
    {
        spec = Safe(spec); spec.Validate();
        var layer = DocumentFeatures.CreateText(spec, x, y); layer.Scale = Math.Clamp(scale, .01, 20);
        return Add(layer, name);
    }

    /// <summary>Font size (with a layer scale beyond 1024 px) that makes the text <paramref name="width"/> pixels wide.</summary>
    public static (double Size, double Scale) FitWidth(TextSpec spec, double width, double maxHeight)
    {
        var probe = spec with { FontSize = 200, LineHeight = spec.LineHeight > 0 ? spec.LineHeight / Math.Max(1, spec.FontSize) * 200 : 0, OutlineWidth = spec.OutlineWidth / Math.Max(1, spec.FontSize) * 200 };
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
