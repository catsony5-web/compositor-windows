using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed record TextSpec
{
    public string Content { get; init; } = "텍스트";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 48;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    // Zero uses the font's natural line spacing; tracking is in 1/1000 em.
    public double LineHeight { get; init; }
    public double Tracking { get; init; }
    public uint ColorArgb { get; init; } = 0xFFFFFFFF;
    public TextAlignment Alignment { get; init; }
    public void Validate()
    {
        if (Content == null || Content.Length > 100_000 || string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 256 ||
            !double.IsFinite(FontSize) || FontSize < 1 || FontSize > 1024 || !Enum.IsDefined(Alignment) ||
            !double.IsFinite(LineHeight) || LineHeight < 0 || LineHeight > 8192 ||
            !double.IsFinite(Tracking) || Tracking < -200 || Tracking > 2000)
            throw new InvalidDataException("텍스트 속성이 올바르지 않습니다.");
    }
}

public enum AdjustmentKind { Levels, Curves, HueSaturation, Exposure, GradientMap, Grain, PhotoDevelop }
public sealed record CurvePoint(double X, double Y);
public sealed record LevelsRange
{
    public double Black { get; init; }
    public double White { get; init; } = 255;
    public double Gamma { get; init; } = 1;
    public double OutputBlack { get; init; }
    public double OutputWhite { get; init; } = 255;
    public double Apply(double value) => Imaging.Level(value, Black, White, Gamma, OutputBlack, OutputWhite);
    public void Validate()
    {
        if (!double.IsFinite(Black) || Black < 0 || Black > 254 || !double.IsFinite(White) || White < Black + 1 || White > 255 ||
            !double.IsFinite(Gamma) || Gamma < .1 || Gamma > 9.99 || !double.IsFinite(OutputBlack) || OutputBlack < 0 || OutputBlack > 255 ||
            !double.IsFinite(OutputWhite) || OutputWhite < 0 || OutputWhite > 255) throw new InvalidDataException("채널 레벨 속성이 올바르지 않습니다.");
    }
}
public sealed record AdjustmentSpec
{
    public AdjustmentKind Kind { get; init; }
    public double Black { get; init; }
    public double White { get; init; } = 255;
    public double Gamma { get; init; } = 1;
    public double OutputBlack { get; init; }
    public double OutputWhite { get; init; } = 255;
    public double Exposure { get; init; }
    public double Offset { get; init; }
    public double ExposureGamma { get; init; } = 1;
    public double Hue { get; init; }
    public double Saturation { get; init; }
    public double Lightness { get; init; }
    public double Amount { get; init; } = .08;
    public int Seed { get; init; } = 1;
    public double GrainSize { get; init; } = 1.5;
    public double GrainRoughness { get; init; } = .5;
    public PhotoDevelopSpec PhotoDevelop { get; init; } = new();
    public uint DarkColor { get; init; } = 0xFF000000;
    public uint LightColor { get; init; } = 0xFFFFFFFF;
    public CurvePoint[] Curve { get; init; } = [new(0, 0), new(1, 1)];
    public LevelsRange RedLevels { get; init; } = new();
    public LevelsRange GreenLevels { get; init; } = new();
    public LevelsRange BlueLevels { get; init; } = new();
    public CurvePoint[] RedCurve { get; init; } = [new(0, 0), new(1, 1)];
    public CurvePoint[] GreenCurve { get; init; } = [new(0, 0), new(1, 1)];
    public CurvePoint[] BlueCurve { get; init; } = [new(0, 0), new(1, 1)];
    public AdjustmentSpec Snapshot() => this with { Curve = Curve.ToArray(), RedCurve = RedCurve.ToArray(), GreenCurve = GreenCurve.ToArray(), BlueCurve = BlueCurve.ToArray() };
    public void Validate()
    {
        static bool In(double n, double low, double high) => double.IsFinite(n) && n >= low && n <= high;
        if (!Enum.IsDefined(Kind) || !In(Black, 0, 254) || !In(White, Black + 1, 255) || !In(Gamma, .1, 9.99) ||
            !In(OutputBlack, 0, 255) || !In(OutputWhite, 0, 255) || !In(Exposure, -20, 20) || !In(Offset, -.5, .5) || !In(ExposureGamma, .01, 9.99) || !In(Hue, -360, 360) ||
            !In(Saturation, -100, 100) || !In(Lightness, -100, 100) || !In(Amount, 0, 1) || Curve == null || Curve.Length < 2 || Curve.Length > 64)
            throw new InvalidDataException("조정 레이어 속성이 올바르지 않습니다.");
        if (!In(GrainSize, .5, 20) || !In(GrainRoughness, 0, 1)) throw new InvalidDataException("그레인 속성이 올바르지 않습니다.");
        (PhotoDevelop ?? throw new InvalidDataException("사진 현상 설정이 없습니다.")).Validate();
        foreach (var range in new[] { RedLevels, GreenLevels, BlueLevels }) (range ?? throw new InvalidDataException("채널 레벨 정보가 없습니다.")).Validate();
        foreach (var curve in new[] { Curve, RedCurve, GreenCurve, BlueCurve })
        {
            if (curve == null || curve.Length < 2 || curve.Length > 64) throw new InvalidDataException("곡선 점 개수가 올바르지 않습니다.");
            double previous = -1;
            foreach (var point in curve)
            {
                if (point == null || !In(point.X, 0, 1) || !In(point.Y, 0, 1) || point.X <= previous)
                    throw new InvalidDataException("곡선 점은 X 좌표 순서대로 지정하세요.");
                previous = point.X;
            }
        }
    }
}

// Corners are expressed in layer pixel coordinates before the layer's affine transform.
public sealed record WarpQuad(Point TopLeft, Point TopRight, Point BottomRight, Point BottomLeft)
{
    public Point[] Corners => [TopLeft, TopRight, BottomRight, BottomLeft];
    public Point Forward(Point point, int width, int height) => Map().Transform(new Point(point.X / width, point.Y / height));
    public Point Inverse(Point point, int width, int height)
    {
        var p = Map().Inverse().Transform(point); return new Point(p.X * width, p.Y * height);
    }
    internal ProjectiveMap Map()
    {
        var p0 = TopLeft; var p1 = TopRight; var p2 = BottomRight; var p3 = BottomLeft;
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        double det = dx1 * dy2 - dx2 * dy1;
        double g = (dx3 * dy2 - dx2 * dy3) / det, h = (dx1 * dy3 - dx3 * dy1) / det;
        return new(p1.X - p0.X + g * p1.X, p3.X - p0.X + h * p3.X, p0.X,
            p1.Y - p0.Y + g * p1.Y, p3.Y - p0.Y + h * p3.Y, p0.Y, g, h, 1);
    }
    public void Validate()
    {
        var p = Corners; double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!double.IsFinite(p[i].X) || !double.IsFinite(p[i].Y) || Math.Abs(p[i].X) > 100_000 || Math.Abs(p[i].Y) > 100_000)
                throw new InvalidDataException("왜곡 좌표가 올바르지 않습니다.");
            var a = p[(i + 1) % 4] - p[i]; var b = p[(i + 2) % 4] - p[(i + 1) % 4];
            double cross = a.X * b.Y - a.Y * b.X;
            if (Math.Abs(cross) < .0001 || i > 0 && Math.Sign(cross) != Math.Sign(sign))
                throw new InvalidDataException("왜곡 사각형은 교차하지 않는 볼록한 모양이어야 합니다.");
            sign = cross;
        }
        _ = Map().Inverse();
    }
}

internal readonly record struct ProjectiveMap(double A, double B, double C, double D, double E, double F, double G, double H, double I)
{
    public Point Transform(Point p)
    {
        double w = G * p.X + H * p.Y + I;
        if (Math.Abs(w) < 1e-12) return new(double.NaN, double.NaN);
        return new((A * p.X + B * p.Y + C) / w, (D * p.X + E * p.Y + F) / w);
    }
    public ProjectiveMap Inverse()
    {
        double det = A * (E * I - F * H) - B * (D * I - F * G) + C * (D * H - E * G);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12) throw new InvalidDataException("왜곡 변환을 역산할 수 없습니다.");
        return new((E * I - F * H) / det, (C * H - B * I) / det, (B * F - C * E) / det,
            (F * G - D * I) / det, (A * I - C * G) / det, (C * D - A * F) / det,
            (D * H - E * G) / det, (B * G - A * H) / det, (A * E - B * D) / det);
    }
}

public static class DocumentFeatures
{
    public static Color Color(uint argb) => System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    public static bool SameAdjustment(AdjustmentSpec? a, AdjustmentSpec? b) =>
        ReferenceEquals(a, b) || a != null && b != null && (a with { Curve = b.Curve, RedCurve = b.RedCurve, GreenCurve = b.GreenCurve, BlueCurve = b.BlueCurve }) == b && a.Curve.SequenceEqual(b.Curve) && a.RedCurve.SequenceEqual(b.RedCurve) && a.GreenCurve.SequenceEqual(b.GreenCurve) && a.BlueCurve.SequenceEqual(b.BlueCurve);
    public static Layer CreateText(TextSpec spec, double x = 0, double y = 0)
    {
        spec.Validate(); return new Layer { Kind = LayerKind.Text, Text = spec, Name = string.IsNullOrWhiteSpace(spec.Content) ? "텍스트" : spec.Content.Split('\n')[0][..Math.Min(40, spec.Content.Split('\n')[0].Length)], Pixels = RenderText(spec), X = x, Y = y };
    }
    public static Raster RenderText(TextSpec spec)
    {
        spec.Validate();
        if (spec.Tracking != 0) return Typography.RenderTracked(spec);
        var face = new Typeface(new FontFamily(spec.FontFamily), spec.Italic ? FontStyles.Italic : FontStyles.Normal, spec.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(string.IsNullOrEmpty(spec.Content) ? " " : spec.Content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, spec.FontSize, new SolidColorBrush(Color(spec.ColorArgb)), 1);
        if (spec.LineHeight > 0) text.LineHeight = spec.LineHeight;
        text.MaxTextWidth = Math.Max(1, text.WidthIncludingTrailingWhitespace); text.TextAlignment = spec.Alignment;
        var bounds = new Rect(0, 0, text.MaxTextWidth, text.Height);
        var ink = text.BuildGeometry(new Point()).Bounds; if (!ink.IsEmpty) bounds.Union(ink);
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width + 8)), height = Math.Max(1, (int)Math.Ceiling(bounds.Height + 8));
        Raster.ValidateSize(width, height);
        return Imaging.Draw(width, height, dc => dc.DrawText(text, new Point(4 - bounds.Left, 4 - bounds.Top)));
    }
    public static void UpdateText(Layer layer, TextSpec spec)
    {
        if (layer.Kind != LayerKind.Text) throw new InvalidOperationException("텍스트 레이어를 선택하세요.");
        var pixels = RenderText(spec);
        bool resized = pixels.Width != layer.Pixels.Width || pixels.Height != layer.Pixels.Height;
        Point? warpedOrigin = resized && layer.Warp != null ? layer.Document(new Point()) : null;
        if (resized && layer.Mask is { } oldMask)
        {
            int oldWidth = layer.Pixels.Width, oldHeight = layer.Pixels.Height;
            var mask = new byte[pixels.Width * pixels.Height];
            // Masks follow the text's normalized surface. Clamped bilinear sampling preserves
            // fully visible/hidden masks and soft coverage without modifying history buffers.
            for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
            {
                double xx = Math.Clamp((x + .5) * oldWidth / pixels.Width - .5, 0, oldWidth - 1);
                double yy = Math.Clamp((y + .5) * oldHeight / pixels.Height - .5, 0, oldHeight - 1);
                int x0 = (int)xx, y0 = (int)yy, x1 = Math.Min(oldWidth - 1, x0 + 1), y1 = Math.Min(oldHeight - 1, y0 + 1);
                double fx = xx - x0, fy = yy - y0;
                mask[y * pixels.Width + x] = Imaging.Byte(oldMask[y0 * oldWidth + x0] * (1 - fx) * (1 - fy) + oldMask[y0 * oldWidth + x1] * fx * (1 - fy) + oldMask[y1 * oldWidth + x0] * (1 - fx) * fy + oldMask[y1 * oldWidth + x1] * fx * fy);
            }
            layer.Mask = mask;
        }
        layer.Pixels = pixels; layer.Text = spec;
        if (warpedOrigin is { } origin)
        {
            // Keep the existing destination quadrilateral. Compensate for the affine
            // transform's changed raster center so all four document corners stay fixed.
            var movedOrigin = layer.Document(new Point()); layer.X += origin.X - movedOrigin.X; layer.Y += origin.Y - movedOrigin.Y;
        }
    }
    public static void Rasterize(Layer layer)
    {
        if (layer.Kind == LayerKind.Group || layer.Kind == LayerKind.Adjustment) throw new InvalidOperationException("그룹 또는 조정은 병합하여 래스터화하세요.");
        layer.Kind = LayerKind.Raster; layer.Text = null; layer.Shape = null; layer.Adjustment = null;
    }
    public static Layer CreateGroup(Document doc, string name = "그룹") => new() { Name = name, Kind = LayerKind.Group, Pixels = new Raster(doc.Width, doc.Height) };
    public static Layer CreateAdjustment(Document doc, AdjustmentSpec spec, string? name = null)
    {
        spec.Validate(); return new() { Name = name ?? (spec.Kind == AdjustmentKind.PhotoDevelop ? "사진 현상" : spec.Kind.ToString()), Kind = LayerKind.Adjustment, Adjustment = spec.Snapshot(), Pixels = new Raster(doc.Width, doc.Height) };
    }
    public static Layer Group(Document doc, IEnumerable<Guid> layerIds, string name = "그룹")
    {
        var ids = layerIds.ToHashSet(); var selected = doc.Layers.Where(l => ids.Contains(l.Id)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("그룹에 넣을 레이어를 선택하세요.");
        if (selected.Any(l => l.ParentId != selected[0].ParentId)) throw new InvalidOperationException("같은 그룹 안의 레이어를 선택하세요.");
        var siblings = doc.Layers.Where(l => l.ParentId == selected[0].ParentId).ToArray();
        int first = Array.FindIndex(siblings, l => ids.Contains(l.Id)), last = Array.FindLastIndex(siblings, l => ids.Contains(l.Id));
        if (last - first + 1 != selected.Length) throw new InvalidOperationException("연속된 레이어를 선택하세요. 그룹 생성은 쌓임 순서를 보존합니다.");
        if (selected[0].Clipped) throw new InvalidOperationException("클리핑의 기준 레이어도 함께 선택하세요.");
        if (last + 1 < siblings.Length && siblings[last + 1].Clipped) throw new InvalidOperationException("기준 레이어에 연결된 클리핑 레이어를 모두 함께 선택하세요.");
        int insertion = doc.Layers.IndexOf(selected[^1]) + 1;
        var group = CreateGroup(doc, name); group.ParentId = selected[0].ParentId;
        if (group.ParentId is { } parentId)
        {
            var parent = doc.Layers.Find(l => l.Id == parentId) ?? throw new InvalidOperationException("부모 그룹이 없습니다.");
            group.Pixels = new Raster(parent.Pixels.Width, parent.Pixels.Height);
        }
        doc.Add(group);
        doc.Layers.Remove(group); doc.Layers.Insert(insertion, group);
        foreach (var l in selected) l.ParentId = group.Id;
        doc.Validate(); return group;
    }
    public static void Ungroup(Document doc, Guid groupId)
    {
        var group = doc.Layers.Find(l => l.Id == groupId) ?? throw new InvalidOperationException("그룹이 없습니다.");
        if (group.Kind != LayerKind.Group) throw new InvalidOperationException("그룹을 선택하세요.");
        // Arbitrary opacity, masks, or nonlinear transforms cannot be discarded losslessly.
        if (group.Mask != null || group.Opacity != 1 || group.Blend != BlendMode.Normal || group.Warp != null || group.ScaleX != 1 || group.ScaleY != 1 || group.Scale != 1 || group.Rotation != 0 || group.FlipX || group.FlipY || group.X != 0 || group.Y != 0 || group.Clipped)
            throw new InvalidOperationException("효과 또는 변형이 있는 그룹은 먼저 병합하세요.");
        var children = doc.Layers.Where(l => l.ParentId == groupId).ToArray();
        int index = doc.Layers.IndexOf(group);
        foreach (var child in children) { int old = doc.Layers.IndexOf(child); if (old < index) index--; doc.Layers.Remove(child); }
        doc.Layers.Remove(group);
        foreach (var child in children) { child.ParentId = group.ParentId; child.Visible &= group.Visible; doc.Layers.Insert(index++, child); }
        doc.ActiveId = children.LastOrDefault()?.Id ?? doc.Layers.LastOrDefault()?.Id ?? Guid.Empty;
    }
    public static void Remove(Document doc, Guid layerId)
    {
        var ids = new HashSet<Guid> { layerId }; bool changed;
        do { changed = false; foreach (var l in doc.Layers) if (l.ParentId is { } p && ids.Contains(p)) changed |= ids.Add(l.Id); } while (changed);
        doc.Layers.RemoveAll(l => ids.Contains(l.Id)); if (ids.Contains(doc.ActiveId)) doc.ActiveId = doc.Layers.LastOrDefault()?.Id ?? Guid.Empty;
    }
    public static Point ToDocumentSpace(Document doc, Layer layer, Point local)
    {
        var point = layer.Document(local); var parent = layer.ParentId; int depth = 0;
        while (parent is { } id && depth++ < 16)
        {
            var group = doc.Layers.Find(l => l.Id == id) ?? throw new InvalidOperationException("그룹이 없습니다.");
            point = group.Document(point); parent = group.ParentId;
        } return point;
    }
    public static Point ToParentSpace(Document doc, Layer layer, Point documentPoint)
    {
        var ancestors = new List<Layer>(); var parent = layer.ParentId;
        while (parent is { } id && ancestors.Count < 16)
        {
            var group = doc.Layers.Find(l => l.Id == id) ?? throw new InvalidOperationException("그룹이 없습니다.");
            ancestors.Add(group); parent = group.ParentId;
        }
        for (int i = ancestors.Count - 1; i >= 0; i--) documentPoint = ancestors[i].Local(documentPoint);
        return documentPoint;
    }
    public static Raster ApplyAdjustment(Raster source, AdjustmentSpec spec, CancellationToken cancellationToken = default)
    {
        spec.Validate();
        if (spec.Kind == AdjustmentKind.PhotoDevelop) return PhotoDevelop.Apply(source, spec.PhotoDevelop, cancellationToken);
        var result = source.Clone();
        var redCurve = BuildCurve(spec.RedCurve, spec.Curve); var greenCurve = BuildCurve(spec.GreenCurve, spec.Curve); var blueCurve = BuildCurve(spec.BlueCurve, spec.Curve);
        var exposure = Enumerable.Range(0, 256).Select(index =>
        {
            double encoded = index / 255.0, linear = encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * Math.Pow(2, spec.Exposure) + spec.Offset), 1 / spec.ExposureGamma);
            return Math.Clamp(linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055, 0, 1);
        }).ToArray();
        Parallel.For(0, source.Height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < source.Width; x++)
            {
                int i = (y * source.Width + x) * 4;
                double b = source.Data[i] / 255.0, g = source.Data[i + 1] / 255.0, r = source.Data[i + 2] / 255.0;
                switch (spec.Kind)
                {
                    case AdjustmentKind.Levels:
                        b = Imaging.Level(spec.BlueLevels.Apply(b), spec.Black, spec.White, spec.Gamma, spec.OutputBlack, spec.OutputWhite); g = Imaging.Level(spec.GreenLevels.Apply(g), spec.Black, spec.White, spec.Gamma, spec.OutputBlack, spec.OutputWhite); r = Imaging.Level(spec.RedLevels.Apply(r), spec.Black, spec.White, spec.Gamma, spec.OutputBlack, spec.OutputWhite); break;
                    case AdjustmentKind.Curves:
                        b = blueCurve[source.Data[i]]; g = greenCurve[source.Data[i + 1]]; r = redCurve[source.Data[i + 2]]; break;
                    case AdjustmentKind.Exposure:
                        b = exposure[source.Data[i]]; g = exposure[source.Data[i + 1]]; r = exposure[source.Data[i + 2]]; break;
                    case AdjustmentKind.HueSaturation:
                        ToHsl(r, g, b, out double h, out double s, out double l);
                        h = (h + spec.Hue / 360 + 2) % 1; s = Math.Clamp(s * (1 + spec.Saturation / 100), 0, 1);
                        l = spec.Lightness >= 0 ? l + (1 - l) * spec.Lightness / 100 : l * (1 + spec.Lightness / 100);
                        (r, g, b) = FromHsl(h, s, l); break;
                    case AdjustmentKind.GradientMap:
                        double lum = Math.Round((.2126 * r + .7152 * g + .0722 * b) * 255) / 255; var dark = Color(spec.DarkColor); var light = Color(spec.LightColor);
                        b = Math.Round(dark.B + (light.B - dark.B) * lum) / 255; g = Math.Round(dark.G + (light.G - dark.G) * lum) / 255; r = Math.Round(dark.R + (light.R - dark.R) * lum) / 255; break;
                    case AdjustmentKind.Grain:
                        double noise = Grain(x, y, spec) * spec.Amount * .35 * (.4 + 2.4 * (.2126 * r + .7152 * g + .0722 * b) * (1 - (.2126 * r + .7152 * g + .0722 * b)));
                        b += noise; g += noise; r += noise; break;
                }
                result.Data[i] = Imaging.Byte(b * 255); result.Data[i + 1] = Imaging.Byte(g * 255); result.Data[i + 2] = Imaging.Byte(r * 255);
            }
        }); return result;
    }
    // Shape-preserving cubic Hermite interpolation from upstream Curves.swift (MIT).
    static double[] BuildCurve(CurvePoint[] points, CurvePoint[] master)
    {
        static Func<double, double> Sampler(CurvePoint[] p)
        {
            var slopes = p.Zip(p.Skip(1), (a, b) => (b.Y - a.Y) / (b.X - a.X)).ToArray();
            double Slope(int j) => j == 0 ? slopes[0] : j == p.Length - 1 ? slopes[^1] : slopes[j - 1] * slopes[j] <= 0 ? 0 : 2 / (1 / slopes[j - 1] + 1 / slopes[j]);
            return x =>
            {
                if (x <= p[0].X) return p[0].Y; if (x >= p[^1].X) return p[^1].Y;
                int k = 0; while (k + 1 < p.Length - 1 && p[k + 1].X < x) k++;
                var a = p[k]; var b = p[k + 1]; double h = b.X - a.X, t = (x - a.X) / h;
                return Math.Clamp((2 * t * t * t - 3 * t * t + 1) * a.Y + (t * t * t - 2 * t * t + t) * h * Slope(k) + (-2 * t * t * t + 3 * t * t) * b.Y + (t * t * t - t * t) * h * Slope(k + 1), 0, 1);
            };
        }
        var channelValue = Sampler(points); var masterValue = Sampler(master);
        return Enumerable.Range(0, 256).Select(n => masterValue(channelValue(n / 255.0))).ToArray();
    }
    // Coordinate-stable triangular grain from upstream AdjustPixels.c (MIT).
    static double Grain(int x, int y, AdjustmentSpec spec)
    {
        static uint Mix(uint value) { unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); } }
        static double Lattice(int x, int y, uint seed) { uint hash = Mix(unchecked((uint)x * 0x9e3779b1) ^ Mix(unchecked((uint)y * 0x85ebca77) ^ seed)); return (hash & 65535) / 65535.0 + (hash >> 16) / 65535.0 - 1; }
        uint seed = unchecked((uint)spec.Seed); double xx = (x + .5) / spec.GrainSize, yy = (y + .5) / spec.GrainSize;
        int ix = (int)Math.Floor(xx), iy = (int)Math.Floor(yy); double tx = xx - ix, ty = yy - iy;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        double top = Lattice(ix, iy, seed) * (1 - tx) + Lattice(ix + 1, iy, seed) * tx, bottom = Lattice(ix, iy + 1, seed) * (1 - tx) + Lattice(ix + 1, iy + 1, seed) * tx;
        double smooth = (top * (1 - ty) + bottom * ty) * 1.6, fine = Lattice(x, y, Mix(seed ^ 0xa511e9b3));
        return smooth + (fine - smooth) * spec.GrainRoughness;
    }
    internal static void ToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        l = (max + min) / 2; h = s = 0; if (d < 1e-12) return;
        s = d / (1 - Math.Abs(2 * l - 1)); h = (max == r ? ((g - b) / d + (g < b ? 6 : 0)) : max == g ? (b - r) / d + 2 : (r - g) / d + 4) / 6;
    }
    internal static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = l - c / 2;
        var t = h < 1.0 / 6 ? (c, x, 0.0) : h < 2.0 / 6 ? (x, c, 0.0) : h < 3.0 / 6 ? (0.0, c, x) : h < 4.0 / 6 ? (0.0, x, c) : h < 5.0 / 6 ? (x, 0.0, c) : (c, 0.0, x);
        return (t.Item1 + m, t.Item2 + m, t.Item3 + m);
    }
}
