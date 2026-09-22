using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public enum VectorFormat { Paths, Pdf }
public sealed record VectorPrimitive(Geometry Geometry, Color Color, bool Fill, double StrokeWidth, Geometry? Clip = null);

// Self-contained, immutable source. Pixel caches are only previews/raster-tool inputs.
// No XAML, executable objects, external filenames or network links are deserialized.
public sealed class VectorContent
{
    public const long MaxDocumentBytes = 512L * 1024 * 1024;
    public sealed record PathData(string Data, double[] Matrix);
    public sealed record Item(PathData Path, uint Color, bool Fill, double StrokeWidth, int Clip);
    public sealed record Scene(PathData[] Clips, Item[] Items);
    readonly byte[] payload;
    readonly Lazy<DrawingGroup>? drawing;
    public VectorFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Page { get; }
    public long ByteLength => payload.LongLength;
    internal Stream Open() => new MemoryStream(payload, false);
    internal void Write(Stream output) => output.Write(payload);
    public DrawingGroup Drawing => drawing?.Value ?? throw new InvalidOperationException("PDF 소스에는 경로 도형이 없습니다.");

    VectorContent(VectorFormat format, int width, int height, int page, byte[] bytes)
    {
        Raster.ValidateSize(width, height);
        if (!Enum.IsDefined(format) || page < 1 || page > 100000 || bytes.Length == 0 || bytes.LongLength > MaxDocumentBytes)
            throw new InvalidDataException("벡터 원본 정보 또는 크기가 올바르지 않습니다.");
        Format = format; Width = width; Height = height; Page = page; payload = bytes;
        if (format == VectorFormat.Pdf)
        {
            if (!System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(1024, bytes.Length)).Contains("%PDF-", StringComparison.Ordinal))
                throw new InvalidDataException("벡터 PDF 원본이 올바르지 않습니다.");
        }
        else drawing = new Lazy<DrawingGroup>(() => Decode(payload), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    internal static VectorContent Read(VectorFormat format, int width, int height, int page, Stream input)
    {
        using var copy = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = input.Read(buffer)) > 0) { if (copy.Length + read > MaxDocumentBytes) throw new InvalidDataException("벡터 원본이 너무 큽니다."); copy.Write(buffer, 0, read); }
        return new(format, width, height, page, copy.ToArray());
    }
    public static VectorContent FromPdf(int width, int height, int page, Stream input) => Read(VectorFormat.Pdf, width, height, page, input);
    public static VectorContent FromPaths(int width, int height, IEnumerable<VectorPrimitive> primitives)
    {
        PathData Encode(Geometry geometry)
        {
            var path = PathGeometry.CreateFromGeometry(geometry); var m = path.Transform.Value;
            return new((path.FillRule == FillRule.Nonzero ? "F1 " : "F0 ") + path.Figures.ToString(CultureInfo.InvariantCulture), [m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY]);
        }
        var clips = new List<PathData>(); var clipIds = new Dictionary<Geometry, int>(); var items = new List<Item>();
        foreach (var primitive in primitives)
        {
            int clip = -1;
            if (primitive.Clip is { } boundary && !clipIds.TryGetValue(boundary, out clip)) { clip = clips.Count; clipIds[boundary] = clip; clips.Add(Encode(boundary)); }
            items.Add(new(Encode(primitive.Geometry), VectorShapes.Argb(primitive.Color), primitive.Fill, primitive.StrokeWidth, clip));
        }
        return new(VectorFormat.Paths, width, height, 1, JsonSerializer.SerializeToUtf8Bytes(new Scene(clips.ToArray(), items.ToArray())));
    }
    static DrawingGroup Decode(byte[] bytes)
    {
        var scene = JsonSerializer.Deserialize<Scene>(bytes) ?? throw new InvalidDataException("벡터 경로 정보가 없습니다.");
        if (scene.Items == null || scene.Clips == null || scene.Items.Length > 1_000_000 || scene.Clips.Length > 10000)
            throw new InvalidDataException("벡터 경로 수가 지원 범위를 초과합니다.");
        Geometry Geometry(PathData data)
        {
            if (data == null || data.Data == null || data.Matrix == null || data.Matrix.Length != 6 || data.Matrix.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e15))
                throw new InvalidDataException("벡터 경로 좌표가 올바르지 않습니다.");
            var g = System.Windows.Media.Geometry.Parse(data.Data).CloneCurrentValue(); var m = data.Matrix;
            g.Transform = new MatrixTransform(m[0], m[1], m[2], m[3], m[4], m[5]);
            var bounds = g.Bounds;
            if (!bounds.IsEmpty && (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)))
                throw new InvalidDataException("벡터 경로 범위가 올바르지 않습니다.");
            g.Freeze(); return g;
        }
        var clips = scene.Clips.Select(Geometry).ToArray(); var result = new DrawingGroup();
        using (var dc = result.Open())
        {
            int current = -1; var brushes = new Dictionary<uint, SolidColorBrush>(); var pens = new Dictionary<(uint, double), Pen>();
            foreach (var item in scene.Items)
            {
                if (item == null || item.Clip < -1 || item.Clip >= clips.Length || !double.IsFinite(item.StrokeWidth) || item.StrokeWidth < 0 || item.StrokeWidth > 100000)
                    throw new InvalidDataException("벡터 경로 스타일이 올바르지 않습니다.");
                if (item.Clip != current) { if (current >= 0) dc.Pop(); current = item.Clip; if (current >= 0) dc.PushClip(clips[current]); }
                if (!brushes.TryGetValue(item.Color, out var brush)) { brush = new SolidColorBrush(VectorShapes.Color(item.Color)); brush.Freeze(); brushes[item.Color] = brush; }
                if (!pens.TryGetValue((item.Color, item.StrokeWidth), out var pen)) { pen = new Pen(brush, item.StrokeWidth); pen.Freeze(); pens[(item.Color, item.StrokeWidth)] = pen; }
                dc.DrawGeometry(item.Fill ? brush : null, item.Fill ? null : pen, Geometry(item.Path));
            }
            if (current >= 0) dc.Pop();
        }
        result.Freeze(); return result;
    }
}
