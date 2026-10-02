using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed record MaterialAsset(Guid Id, string Name, Raster Pixels, string Source = "", bool Tileable = false);
public sealed record RegionPath(string Data, double M11 = 1, double M12 = 0, double M21 = 0, double M22 = 1, double OffsetX = 0, double OffsetY = 0)
{
    static readonly ConditionalWeakTable<RegionPath, Geometry> cache = new();
    [System.Text.Json.Serialization.JsonIgnore] public Geometry Geometry => cache.GetValue(this, value =>
    {
        if (string.IsNullOrWhiteSpace(value.Data) || Encoding.UTF8.GetByteCount(value.Data) > MaterialEditing.MaxPathBytes ||
            !new[] { value.M11, value.M12, value.M21, value.M22, value.OffsetX, value.OffsetY }.All(double.IsFinite))
            throw new InvalidDataException("영역 경로 또는 변환이 올바르지 않습니다.");
        var geometry = System.Windows.Media.Geometry.Parse(value.Data).CloneCurrentValue();
        if (geometry.GetFlattenedPathGeometry().Figures.Any(f => !f.IsClosed)) throw new InvalidDataException("영역은 닫힌 경로여야 합니다.");
        geometry.Transform = new MatrixTransform(value.M11, value.M12, value.M21, value.M22, value.OffsetX, value.OffsetY);
        var bounds = geometry.Bounds; double area = MaterialEditing.Area(geometry);
        if (bounds.IsEmpty || !double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height) ||
            Math.Abs(bounds.X) > 100_000 || Math.Abs(bounds.Y) > 100_000 || bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.Width > 100_000 || bounds.Height > 100_000 || !double.IsFinite(area) || area <= .000001)
            throw new InvalidDataException("면적이 있는 닫힌 영역을 지정하세요.");
        geometry.Freeze(); return geometry;
    });
    public static RegionPath From(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        var m = path.Transform?.Value ?? Matrix.Identity;
        return new(path.ToString(CultureInfo.InvariantCulture), m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);
    }
    public RegionPath Translate(double x, double y) => this with { OffsetX = OffsetX + x, OffsetY = OffsetY + y };
}
public sealed record MaterialRegion(Guid Id, string Name, RegionPath Path, string Source, Guid? SourceLayerId = null);
public sealed record MaterialFill(MaterialAsset Asset, Guid SourceRegionId, string RegionName, RegionPath Boundary, int Width, int Height,
    double TileWidth, double TileHeight, double Angle = 0, double OffsetX = 0, double OffsetY = 0, uint Ink = 0, double LineWeight = 1, uint Background = 0);
// Background: for line patterns, an optional #AARRGGBB color painted inside the region under the lines (0 = none, transparent).

public static class MaterialEditing
{
    public const int MaxAssets = 64, MaxRegions = 128, MaxPathBytes = 65536;
    public const int MaxPixels = 16_777_216, MaxSide = 8192;
    public const long MaxAssetBytes = 256L * 1024 * 1024;
    public static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new InvalidDataException("재료 이미지와 적용 영역은 한 변 8,192px, 전체 16,777,216픽셀까지 지원합니다.");
    }
    public static void Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 4096) throw new InvalidDataException("재료와 영역 이름을 확인하세요.");
    }
    public static void ValidateAsset(MaterialAsset asset)
    {
        if (asset == null || asset.Id == Guid.Empty || asset.Pixels == null || asset.Source == null || Encoding.UTF8.GetByteCount(asset.Source) > 4096)
            throw new InvalidDataException("재료 원본 정보가 올바르지 않습니다.");
        Name(asset.Name); ValidateSize(asset.Pixels.Width, asset.Pixels.Height);
    }
    /// <summary>Filled area in transformed coordinates. WPF's GetArea can report 0 for a thin shape
    /// once its transform moves it near the origin, so a zero result is re-measured on the flattened,
    /// transform-applied outline before a region is rejected.</summary>
    public static double Area(Geometry geometry)
    {
        double area = geometry.GetArea();
        if (double.IsFinite(area) && area > .000001) return area;
        var flat = geometry.GetFlattenedPathGeometry();
        double measured = flat.GetArea();
        if (double.IsFinite(measured) && measured > .000001) return measured;
        // Last resort: shoelace over the flattened figures (nonzero fill of simple hatch outlines).
        double sum = 0;
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                else if (segment is LineSegment line) points.Add(line.Point);
            double twice = 0;
            for (int i = 0; i < points.Count; i++) { var a = points[i]; var b = points[(i + 1) % points.Count]; twice += a.X * b.Y - b.X * a.Y; }
            sum += Math.Abs(twice) / 2;
        }
        return double.IsFinite(sum) ? sum : 0;
    }

    public static void ValidateRegion(MaterialRegion region)
    {
        if (region == null || region.Id == Guid.Empty || region.Path == null || region.Source is not ("polygon" or "selection" or "closed_layer"))
            throw new InvalidDataException("영역 정보가 올바르지 않습니다.");
        Name(region.Name); _ = region.Path.Geometry;
    }
    public static void ValidateFill(MaterialFill fill, Raster pixels, bool dimensions = true)
    {
        if (fill == null || fill.SourceRegionId == Guid.Empty || fill.Boundary == null) throw new InvalidDataException("재료 맵핑 정보가 없습니다.");
        Name(fill.RegionName); ValidateAsset(fill.Asset); ValidateSize(fill.Width, fill.Height);
        if (!new[] { fill.TileWidth, fill.TileHeight, fill.Angle, fill.OffsetX, fill.OffsetY, fill.LineWeight }.All(double.IsFinite) ||
            fill.TileWidth < 1 || fill.TileHeight < 1 || fill.TileWidth > 100_000 || fill.TileHeight > 100_000 || fill.LineWeight < .1 || fill.LineWeight > 8 ||
            Math.Abs(fill.Angle) > 36000 || Math.Abs(fill.OffsetX) > 100_000 || Math.Abs(fill.OffsetY) > 100_000)
            throw new InvalidDataException("재료의 반복 크기·방향·위치를 확인하세요.");
        var bounds = fill.Boundary.Geometry.Bounds;
        if (bounds.X < -.00001 || bounds.Y < -.00001 || bounds.Right > fill.Width + .00001 || bounds.Bottom > fill.Height + .00001 ||
            dimensions && (fill.Width != pixels.Width || fill.Height != pixels.Height))
            throw new InvalidDataException("재료 경계와 미리보기 크기가 다릅니다.");
    }
    // A mapped layer carries its immutable original. Copying it to another document
    // remains self-contained, even if that document has no library entries yet.
    public static IReadOnlyList<MaterialAsset> Assets(Document doc, Layer? extra = null)
    {
        if (doc.Materials == null || doc.MaterialRegions == null) throw new InvalidDataException("재료 라이브러리가 없습니다.");
        var result = new Dictionary<Guid, MaterialAsset>();
        foreach (var asset in doc.Materials.Concat(doc.Layers.Select(l => l?.Material?.Asset).OfType<MaterialAsset>())
            .Concat(extra?.Material is { } fill ? [fill.Asset] : []))
        {
            ValidateAsset(asset);
            if (result.TryGetValue(asset.Id, out var previous))
            {
                // Built-in patterns are redrawn from geometry; copies made by other builds may carry
                // slightly different fallback pixels and still name the same pattern.
                if (HatchPatterns.TryGet(previous, out var kept) && HatchPatterns.TryGet(asset, out var next) && kept == next) continue;
                // A renamed line pattern (the user's library entry) keeps its ID and tile; the first name wins.
                if (LinePatterns.IsCustom(previous) && LinePatterns.IsCustom(asset) && previous.Pixels.Width == asset.Pixels.Width &&
                    previous.Pixels.Height == asset.Pixels.Height && previous.Pixels.Data.AsSpan().SequenceEqual(asset.Pixels.Data)) continue;
                if (!ReferenceEquals(previous, asset) && (previous.Name != asset.Name || previous.Source != asset.Source || previous.Tileable != asset.Tileable ||
                    previous.Pixels.Width != asset.Pixels.Width || previous.Pixels.Height != asset.Pixels.Height || !previous.Pixels.Data.AsSpan().SequenceEqual(asset.Pixels.Data)))
                    throw new InvalidDataException("같은 재료 ID에 다른 원본이 있습니다.");
            }
            else result.Add(asset.Id, asset);
        }
        if (result.Count > MaxAssets || result.Values.Sum(a => a.Pixels.Data.LongLength) > MaxAssetBytes)
            throw new InvalidDataException("문서의 재료 원본은 최대 64개, 합계 256MiB까지 지원합니다.");
        return result.Values.ToArray();
    }
    public static long ValidateLibrary(Document doc, Layer? extra = null)
    {
        var assets = Assets(doc, extra);
        foreach (var region in doc.MaterialRegions) ValidateRegion(region);
        if (doc.Materials.Count > MaxAssets || doc.Materials.Select(m => m.Id).Distinct().Count() != doc.Materials.Count ||
            doc.MaterialRegions.Count > MaxRegions || doc.MaterialRegions.Select(r => r.Id).Distinct().Count() != doc.MaterialRegions.Count)
            throw new InvalidDataException("재료 또는 영역의 수와 ID를 확인하세요.");
        return assets.Sum(a => a.Pixels.Data.LongLength) + doc.MaterialRegions.Sum(r => (long)r.Path.Data.Length * 2);
    }
    public static Geometry Polygon(IReadOnlyList<Point[]> contours)
    {
        if (contours.Count is < 1 or > 17 || contours.Sum(c => c.Length) > 2048 ||
            contours.Any(c => c.Length < 3 || c.Any(p => !double.IsFinite(p.X + p.Y) || Math.Abs(p.X) > 100_000 || Math.Abs(p.Y) > 100_000)))
            throw new InvalidDataException("각 윤곽은 3개 이상, 전체 2,048개 이하의 점으로 지정하세요.");
        var path = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = path.Open()) foreach (var points in contours)
        { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        path.Freeze(); return path;
    }
    public static MaterialRegion Region(Document doc, string name, Geometry geometry, string source, Guid? layerId = null)
    {
        var region = new MaterialRegion(Guid.NewGuid(), name, RegionPath.From(geometry), source, layerId);
        ValidateRegion(region);
        var bounds = region.Path.Geometry.Bounds;
        if (bounds.X < -.00001 || bounds.Y < -.00001 || bounds.Right > doc.Width + .00001 || bounds.Bottom > doc.Height + .00001)
            throw new InvalidDataException("영역이 캔버스를 벗어납니다. 문서 픽셀 좌표를 확인하세요.");
        return region;
    }
    public static Geometry ClosedLayer(Document doc, Guid id)
    {
        var layer = doc.Layers.SingleOrDefault(l => l.Id == id) ?? throw new InvalidDataException("영역으로 사용할 객체가 없습니다.");
        Geometry geometry;
        if (layer.Shape is { } shape)
            geometry = shape.Kind == ShapeKind.Ellipse ? new EllipseGeometry(new Rect(0, 0, shape.Width, shape.Height))
                : new RectangleGeometry(new Rect(0, 0, shape.Width, shape.Height), shape.CornerRadius, shape.CornerRadius);
        else if (layer.Vector is { Format: VectorFormat.Paths } vector)
        {
            Geometry Walk(Drawing drawing)
            {
                if (drawing is GeometryDrawing gd)
                {
                    var path = gd.Geometry.GetFlattenedPathGeometry();
                    if (path.Figures.Count == 0 || path.Figures.Any(f => !f.IsClosed))
                        throw new InvalidDataException("열린 선은 면으로 추측하지 않습니다. 닫힌 객체나 선택 영역을 사용하세요.");
                    return gd.Geometry.CloneCurrentValue();
                }
                if (drawing is not DrawingGroup group) throw new InvalidDataException("이 객체의 경계를 가져올 수 없습니다.");
                var shape = new GeometryGroup { FillRule = FillRule.EvenOdd };
                foreach (var child in group.Children) shape.Children.Add(Walk(child));
                Geometry combined = shape;
                if (group.ClipGeometry != null) combined = Geometry.Combine(combined, group.ClipGeometry, GeometryCombineMode.Intersect, null);
                var transformed = new GeometryGroup(); transformed.Children.Add(combined); transformed.Transform = group.Transform?.CloneCurrentValue() ?? Transform.Identity;
                return transformed;
            }
            geometry = Walk(vector.Drawing);
        }
        else throw new InvalidDataException("닫힌 도형·CAD 경로를 선택하거나 선택 영역/다각형으로 지정하세요.");
        var matrix = Matrix.Identity; var current = layer;
        while (true)
        {
            if (current.Warp != null || current.Mask != null || current.Clipped)
                throw new InvalidDataException("마스크·원근·클리핑이 있는 객체는 선택 영역으로 지정하세요.");
            matrix.Append(current.Matrix);
            if (current.ParentId is not { } parent) break;
            current = doc.Layers.Single(l => l.Id == parent);
            if (!DrawingLayers.IsContainer(current)) throw new InvalidDataException("잘릴 수 있는 그룹 안의 객체는 선택 영역으로 지정하세요.");
        }
        // GetOutlinedPathGeometry bakes nested drawing/group transforms before the layer transform.
        var result = geometry.GetOutlinedPathGeometry();
        var transform = result.Transform?.Value ?? Matrix.Identity; transform.Append(matrix); result.Transform = new MatrixTransform(transform); result.Freeze();
        return result;
    }
    public static Layer Apply(Document doc, Guid assetId, Guid regionId, double tileWidth, double tileHeight, double angle = 0, double offsetX = 0, double offsetY = 0)
    {
        var asset = Assets(doc).SingleOrDefault(a => a.Id == assetId) ?? throw new InvalidDataException("등록된 재료 ID가 없습니다.");
        var region = doc.MaterialRegions.SingleOrDefault(r => r.Id == regionId) ?? throw new InvalidDataException("등록된 영역 ID가 없습니다.");
        var bounds = region.Path.Geometry.Bounds;
        double left = Math.Floor(bounds.Left), top = Math.Floor(bounds.Top);
        if (bounds.X < -.00001 || bounds.Y < -.00001 || bounds.Right > doc.Width + .00001 || bounds.Bottom > doc.Height + .00001)
            throw new InvalidDataException("저장된 영역이 현재 캔버스를 벗어납니다. 영역을 다시 지정하세요.");
        int width = checked((int)Math.Ceiling(bounds.Right - left)), height = checked((int)Math.Ceiling(bounds.Bottom - top));
        ValidateSize(width, height);
        var fill = new MaterialFill(asset, region.Id, region.Name, region.Path.Translate(-left, -top), width, height, tileWidth, tileHeight, angle, offsetX, offsetY);
        // Material layers are photo layers (DrawingLayers.Categories), wherever they sit.
        var layer = new Layer { Name = "재료 · " + asset.Name + " · " + region.Name, Kind = LayerKind.Material, Category = LayerCategory.Photo,
            Material = fill, X = left, Y = top, Pixels = new Raster(1, 1), Blend = BlendMode.Multiply };
        ValidateFill(fill, layer.Pixels, false);
        layer.Pixels = MaterialRenderer.Render(fill); return layer;
    }
    // Default repeat width for a new material in a document of this size (document pixels).
    public static double DefaultTile(int width, int height) => Math.Clamp(Math.Max(width, height) / 14d, 40, 320);
    // Default repeat width for this asset: large-cell patterns repeat over a longer distance.
    public static double DefaultTile(int width, int height, MaterialAsset asset) => DefaultTile(width, height) * RepeatScale(asset);
    public static double RepeatScale(MaterialAsset asset) => HatchPatterns.TryGet(asset, out var pattern) ? HatchPatterns.RepeatScale(pattern)
        : LinePatterns.IsCustom(asset) ? LinePatternRenderer.RepeatScale(asset) : 1;
    // Height / width of the asset's pixels.
    public static double Aspect(MaterialAsset asset) => asset.Pixels.Height / (double)Math.Max(1, asset.Pixels.Width);
    // The user's vertical ratio: 1 keeps the asset's own proportions.
    public static double Stretch(MaterialFill fill) => fill.TileHeight / (fill.TileWidth * Aspect(fill.Asset));
    // Another asset at the same relative size (크기 %), with the same ratio, direction, offset, ink, line weight and background.
    public static MaterialFill Swap(MaterialFill fill, MaterialAsset asset)
    {
        double width = fill.TileWidth * RepeatScale(asset) / RepeatScale(fill.Asset);
        return fill with { Asset = asset, TileWidth = width, TileHeight = width * Aspect(asset) * Stretch(fill) };
    }
    public static MaterialFill Sized(MaterialFill fill, double tileWidth, double stretch) => fill with { TileWidth = tileWidth, TileHeight = tileWidth * Aspect(fill.Asset) * stretch };
    public static void TranslateRegions(Document doc, double x, double y)
        => doc.MaterialRegions = doc.MaterialRegions.Select(r => r with { Path = r.Path.Translate(x, y) }).ToList();
}
