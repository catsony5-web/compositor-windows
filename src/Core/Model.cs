using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed class Raster
{
    public const int MaxDimension = 65_535;
    // Largest BGRA surface that fits in one .NET byte[] with signed-int indexing.
    public static readonly int MaxPixels = Array.MaxLength / 4;
    public const long MaxEncodedBytes = 4L * 1024 * 1024 * 1024;
    public int Width { get; }
    public int Height { get; }
    // Straight (not premultiplied) BGRA. Published rasters are immutable; edits clone first.
    public byte[] Data { get; }
    public Raster(int width, int height, byte[]? data = null)
    {
        ValidateSize(width, height);
        Width = width; Height = height;
        if (data != null && data.Length != (long)width * height * 4) throw new InvalidDataException("픽셀 데이터 크기가 잘못되었습니다.");
        Data = data ?? new byte[checked(width * height * 4)];
    }
    public static void ValidateSize(int w, int h)
    {
        if (w < 1 || h < 1 || w > MaxDimension || h > MaxDimension || (long)w * h > MaxPixels)
            throw new InvalidDataException($"이미지는 한 변 {MaxDimension:N0}px, 전체 {MaxPixels:N0}픽셀 이하로 사용하세요. 실제로 열 수 있는 크기는 사용 가능한 메모리에 따라 달라집니다.");
    }
    public Raster Clone() => new(Width, Height, (byte[])Data.Clone());
    public BitmapSource Bitmap(double dpi = 96)
    {
        var b = BitmapSource.Create(Width, Height, dpi, dpi, PixelFormats.Bgra32, null, Data, Width * 4);
        b.Freeze(); return b;
    }
    public BitmapSource Thumbnail(int maxSide = 68)
    {
        if (maxSide < 1 || maxSide > 1024) throw new ArgumentOutOfRangeException(nameof(maxSide));
        double scale = Math.Min(1, maxSide / (double)Math.Max(Width, Height));
        if (scale == 1) return Bitmap();
        int w = Math.Max(1, (int)Math.Round(Width * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
        var preview = new Raster(w, h);
        // Sample directly into a small preview. A layer-list icon must not retain a
        // second full-resolution WIC bitmap (up to 2 GiB for a large source).
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double b = 0, g = 0, r = 0, a = 0;
            for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
            {
                int px = Math.Min(Width - 1, (int)((x + (sx + .5) / 4) * Width / w));
                int py = Math.Min(Height - 1, (int)((y + (sy + .5) / 4) * Height / h));
                int i = (py * Width + px) * 4;
                double alpha = Data[i + 3];
                b += Data[i] * alpha; g += Data[i + 1] * alpha; r += Data[i + 2] * alpha; a += alpha;
            }
            int d = (y * w + x) * 4;
            if (a > 0) { preview.Data[d] = Imaging.Byte(b / a); preview.Data[d + 1] = Imaging.Byte(g / a); preview.Data[d + 2] = Imaging.Byte(r / a); }
            preview.Data[d + 3] = Imaging.Byte(a / 16);
        }
        return preview.Bitmap();
    }
    public static Raster FromBitmap(BitmapSource b)
    {
        ValidateSize(b.PixelWidth, b.PixelHeight);
        var converted = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var r = new Raster(b.PixelWidth, b.PixelHeight);
        converted.CopyPixels(r.Data, r.Width * 4, 0); return r;
    }
    public static Raster Load(Stream stream)
    {
        // Read dimensions before WIC commits to caching all decoded pixels. CopyPixels in
        // FromBitmap completes the decode while the caller's stream is still open.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("이미지 프레임이 없습니다.");
        var frame = decoder.Frames[0];
        ValidateSize(frame.PixelWidth, frame.PixelHeight);
        return FromBitmap(frame);
    }
    public static Raster Load(string path) => ImportExport.LoadImage(path);
    public void WritePng(Stream s)
    {
        // WIC encoders need a seekable stream; ZipArchive entry streams are not seekable.
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(Bitmap()));
        if (s.CanSeek) { enc.Save(s); return; }
        using Stream buffer = Data.LongLength <= ImageStaging.MemoryThresholdBytes
            ? new MemoryStream() : ImageStaging.CreateTemporaryStream();
        enc.Save(buffer); buffer.Position = 0; buffer.CopyTo(s);
    }
    public static Raster Solid(int w, int h, Color color)
    {
        var r = new Raster(w, h);
        for (int i = 0; i < r.Data.Length; i += 4) { r.Data[i] = color.B; r.Data[i + 1] = color.G; r.Data[i + 2] = color.R; r.Data[i + 3] = color.A; }
        return r;
    }
}

public enum BlendMode { Normal, Multiply, Screen, Overlay, SoftLight, Darken, Lighten, Difference, ColorDodge, ColorBurn, Hue, Saturation, Color, Luminosity }
public enum LayerKind { Raster, Text, Adjustment, Group, Shape, Vector, Material }
public enum LayerCategory { Automatic, Drawing, Photo }

public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "레이어";
    public Raster Pixels { get; set; } = null!;
    public byte[]? Mask { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1;
    public BlendMode Blend { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public double Rotation { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public LayerKind Kind { get; set; }
    public Guid? ParentId { get; set; }
    public LayerCategory Category { get; set; }
    public string? SourceLayerName { get; set; }
    public bool Clipped { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public TextSpec? Text { get; set; }
    public ShapeSpec? Shape { get; set; }
    public VectorContent? Vector { get; set; }
    public MaterialFill? Material { get; set; }
    public AdjustmentSpec? Adjustment { get; set; }
    public WarpQuad? Warp { get; set; }
    public Layer Snapshot()
    {
        var copy = (Layer)MemberwiseClone();
        if (Adjustment != null) copy.Adjustment = Adjustment.Snapshot();
        return copy;
    }
    // Port of LayerTransform / BrushRaster.pixelToDocument: rotate/flip around center.
    public Matrix Matrix
    {
        get
        {
            var m = Matrix.Identity;
            m.Translate(-Pixels.Width / 2.0, -Pixels.Height / 2.0);
            m.Scale(Scale * ScaleX * (FlipX ? -1 : 1), Scale * ScaleY * (FlipY ? -1 : 1));
            m.Rotate(Rotation); m.Translate(X + Pixels.Width * Scale * ScaleX / 2, Y + Pixels.Height * Scale * ScaleY / 2);
            return m;
        }
    }
    public Point Local(Point p) { var m = Matrix; m.Invert(); var q = m.Transform(p); return Warp == null ? q : Warp.Inverse(q, Pixels.Width, Pixels.Height); }
    public Point Document(Point p) => Matrix.Transform(Warp == null ? p : Warp.Forward(p, Pixels.Width, Pixels.Height));
}

public sealed class Document
{
    // Keep the bitmap/adjustment ceiling used by legacy image importers. CAD
    // entities and their groups have a separate, bounded scene-node allowance.
    public const int MaxLayers = 128;
    public const int MaxNodes = 32768;
    public const long MaxLayerBytes = 8L * 1024 * 1024 * 1024;
    readonly long layerByteLimit;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public double Dpi { get; set; } = 96;
    public string Name { get; set; } = "제목 없음";
    public Guid Revision { get; set; } = Guid.NewGuid();
    public List<Layer> Layers { get; set; } = [];
    public List<Artboard> Artboards { get; set; } = [];
    public List<MaterialAsset> Materials { get; set; } = [];
    public List<MaterialRegion> MaterialRegions { get; set; } = [];
    public Guid ActiveId { get; set; }
    public Layer? Active => Layers.Find(l => l.Id == ActiveId);
    public Document(long layerByteLimit = MaxLayerBytes)
    {
        if (layerByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(layerByteLimit));
        this.layerByteLimit = layerByteLimit;
    }
    public Document Snapshot() => new(layerByteLimit) { Width = Width, Height = Height, Dpi = Dpi, Name = Name, Revision = Revision, ActiveId = ActiveId, Layers = Layers.Select(l => l.Snapshot()).ToList(), Artboards = Artboards.ToList(), Materials = Materials.ToList(), MaterialRegions = MaterialRegions.ToList() };
    public void Add(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ValidateLayer(layer);
        if (Layers.Count >= MaxNodes) throw new InvalidOperationException($"최대 {MaxNodes:N0}개 객체와 그룹을 지원합니다.");
        if (UsesBitmapSlot(layer) && Layers.Count(UsesBitmapSlot) >= MaxLayers)
            throw new InvalidOperationException($"이미지와 조정 레이어는 최대 {MaxLayers}개를 지원합니다.");
        if (Layers.Any(l => l.Id == layer.Id)) throw new InvalidOperationException("레이어 ID가 중복되었습니다.");
        var groupPixels = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        var bytes = Layers.Sum(l => StorageBytes(l, groupPixels)) + StorageBytes(layer, groupPixels) + MaterialEditing.ValidateLibrary(this, layer);
        if (bytes > layerByteLimit) throw new InvalidOperationException($"레이어 메모리 한도({layerByteLimit / (1024.0 * 1024):N0} MiB)를 초과합니다.");
        if (Layers.Sum(l => l.Vector?.ByteLength ?? 0) + (layer.Vector?.ByteLength ?? 0) > VectorContent.MaxDocumentBytes)
            throw new InvalidOperationException("문서의 벡터 원본이 512MiB를 초과합니다.");
        Layers.Add(layer); ActiveId = layer.Id;
    }
    public void Validate() => ValidateCore(true);
    // Project headers are checked before raster payloads are decoded. Their 1×1
    // placeholders cannot be compared with retained geometry dimensions yet.
    internal void ValidateMetadata() => ValidateCore(false);
    void ValidateCore(bool validateRasterDimensions)
    {
        Raster.ValidateSize(Width, Height);
        ArtboardEditing.Validate(this);
        if (!double.IsFinite(Dpi) || Dpi < 1 || Dpi > 9600) throw new InvalidDataException("해상도는 1~9600 DPI로 입력하세요.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("작업 이름이 비어 있습니다.");
        if (Encoding.UTF8.GetByteCount(Name) > 16_384) throw new InvalidDataException("작업 이름이 너무 깁니다.");
        if (Layers == null || Layers.Count > MaxNodes) throw new InvalidDataException("객체와 그룹 수가 지원 한도를 초과합니다.");
        if (Layers.Count(l => l != null && UsesBitmapSlot(l)) > MaxLayers)
            throw new InvalidDataException($"이미지와 조정 레이어는 최대 {MaxLayers}개를 지원합니다.");
        var ids = new HashSet<Guid>();
        if (Layers.Sum(l => l?.Vector?.ByteLength ?? 0) > VectorContent.MaxDocumentBytes) throw new InvalidDataException("문서의 벡터 원본이 512MiB를 초과합니다.");
        long bytes = MaterialEditing.ValidateLibrary(this);
        if (bytes > layerByteLimit) throw new InvalidDataException($"레이어 메모리 한도({layerByteLimit / (1024.0 * 1024):N0} MiB)를 초과합니다.");
        var groupPixels = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var layer in Layers)
        {
            if (layer == null) throw new InvalidDataException("레이어 정보가 없습니다.");
            ValidateLayer(layer, validateRasterDimensions);
            if (!ids.Add(layer.Id)) throw new InvalidDataException("레이어 ID가 중복되었습니다.");
            bytes += StorageBytes(layer, groupPixels);
            if (bytes > layerByteLimit) throw new InvalidDataException($"레이어 메모리 한도({layerByteLimit / (1024.0 * 1024):N0} MiB)를 초과합니다.");
        }
        if (ActiveId != Guid.Empty && !ids.Contains(ActiveId)) throw new InvalidDataException("활성 레이어가 존재하지 않습니다.");
        var lookup = Layers.ToDictionary(l => l.Id);
        foreach (var layer in Layers)
        {
            var chain = new HashSet<Guid> { layer.Id }; var parent = layer.ParentId;
            while (parent is { } id)
            {
                if (!lookup.TryGetValue(id, out var folder) || folder.Kind != LayerKind.Group || !chain.Add(id))
                    throw new InvalidDataException("그룹 계층에 순환 또는 잘못된 부모가 있습니다.");
                if (chain.Count > 17) throw new InvalidDataException("그룹은 16단계까지 중첩할 수 있습니다.");
                parent = folder.ParentId;
            }
        }
    }
    // Empty CAD folders share a coordinate-space surface. Count that immutable
    // buffer once while retaining the conservative per-layer bitmap/mask budget.
    internal static long StorageBytes(Layer layer, HashSet<byte[]> groupPixels) =>
        (layer.Kind != LayerKind.Group || groupPixels.Add(layer.Pixels.Data) ? layer.Pixels.Data.LongLength : 0) + (layer.Mask?.LongLength ?? 0);
    static bool UsesBitmapSlot(Layer layer) => layer.Kind is LayerKind.Raster or LayerKind.Adjustment or LayerKind.Material;
    internal static void ValidateLayer(Layer layer, bool validateRasterDimensions = true)
    {
        if (layer.Id == Guid.Empty) throw new InvalidDataException("레이어 ID가 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(layer.Name)) throw new InvalidDataException("레이어 이름이 비어 있습니다.");
        if (Encoding.UTF8.GetByteCount(layer.Name) > 16_384) throw new InvalidDataException("레이어 이름이 너무 깁니다.");
        if (layer.Pixels == null) throw new InvalidDataException("레이어 이미지가 없습니다.");
        if (layer.Mask != null && layer.Mask.LongLength != (long)layer.Pixels.Width * layer.Pixels.Height)
            throw new InvalidDataException("마스크 크기가 레이어 이미지와 다릅니다.");
        if (!double.IsFinite(layer.X) || !double.IsFinite(layer.Y) || Math.Abs(layer.X) > 100_000 || Math.Abs(layer.Y) > 100_000 ||
            !double.IsFinite(layer.Scale) || layer.Scale < .01 || layer.Scale > 20 ||
            !double.IsFinite(layer.ScaleX) || layer.ScaleX < .01 || layer.ScaleX > 20 ||
            !double.IsFinite(layer.ScaleY) || layer.ScaleY < .01 || layer.ScaleY > 20 ||
            !double.IsFinite(layer.Rotation) || Math.Abs(layer.Rotation) > 36_000 ||
            !double.IsFinite(layer.Opacity) || layer.Opacity < 0 || layer.Opacity > 1 || !Enum.IsDefined(layer.Blend))
            throw new InvalidDataException("레이어 속성이 올바르지 않습니다.");
        if (!Enum.IsDefined(layer.Kind)) throw new InvalidDataException("레이어 종류가 올바르지 않습니다.");
        if (layer.Kind == LayerKind.Material) MaterialEditing.ValidateFill(layer.Material!, layer.Pixels, validateRasterDimensions);
        else if (layer.Material != null) throw new InvalidDataException("재료 레이어 종류가 일치하지 않습니다.");
        if (!Enum.IsDefined(layer.Category) || layer.SourceLayerName is { } source && (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > 16_384))
            throw new InvalidDataException("도면 레이어 정보가 올바르지 않습니다.");
        if (layer.Kind == LayerKind.Vector)
        {
            if (validateRasterDimensions && (layer.Vector == null || layer.Vector.Width != layer.Pixels.Width || layer.Vector.Height != layer.Pixels.Height))
                throw new InvalidDataException("벡터 원본과 미리보기의 크기가 다릅니다.");
        }
        else if (layer.Vector != null) throw new InvalidDataException("벡터 레이어 종류가 일치하지 않습니다.");
        if (layer.Kind == LayerKind.Shape)
        {
            var shape = layer.Shape ?? throw new InvalidDataException("도형 정보가 없습니다.");
            shape.Validate();
            if (validateRasterDimensions && (shape.Width != layer.Pixels.Width || shape.Height != layer.Pixels.Height))
                throw new InvalidDataException("도형의 크기와 레이어 이미지 크기가 다릅니다.");
        }
        else if (layer.Shape != null) throw new InvalidDataException("도형 레이어 종류가 일치하지 않습니다.");
        if (layer.Kind == LayerKind.Text) (layer.Text ?? throw new InvalidDataException("텍스트 정보가 없습니다.")).Validate();
        else if (layer.Text != null) throw new InvalidDataException("텍스트 레이어 종류가 일치하지 않습니다.");
        if (layer.Kind == LayerKind.Adjustment) (layer.Adjustment ?? throw new InvalidDataException("조정 정보가 없습니다.")).Validate();
        else if (layer.Adjustment != null) throw new InvalidDataException("조정 레이어 종류가 일치하지 않습니다.");
        layer.Warp?.Validate();
    }
}

public sealed class History
{
    readonly List<(string Label, Document State)> past = [];
    readonly List<(string Label, Document State)> future = [];
    readonly int entryLimit;
    readonly long retainedByteLimit;
    public Guid SavedRevision { get; private set; }
    public bool CanUndo => past.Count > 0;
    public bool CanRedo => future.Count > 0;
    public string UndoLabel => CanUndo ? past[^1].Label : "";
    public History(int entryLimit = 50, long retainedByteLimit = 192L * 1024 * 1024)
    {
        this.entryLimit = Math.Max(0, entryLimit);
        this.retainedByteLimit = Math.Max(0, retainedByteLimit);
    }
    public void Reset(Document d) { past.Clear(); future.Clear(); MarkSaved(d); }
    public void MarkSaved(Document d) => SavedRevision = d.Revision;
    public bool Dirty(Document d) => SavedRevision != d.Revision;
    public void Commit(string label, Document before, Document after)
    {
        if (SameState(before, after)) return;
        after.Revision = Guid.NewGuid(); past.Add((label, before)); future.Clear(); Trim(after);
    }
    void Trim(Document current)
    {
        // Count only distinct backing arrays retained in history, matching the upstream design.
        while (past.Count + future.Count > entryLimit || RetainedBytes(current) > retainedByteLimit)
        {
            if (past.Count > 0) past.RemoveAt(0);
            else if (future.Count > 0) future.RemoveAt(0);
            else break;
        }
    }
    public long RetainedBytes(Document current)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var l in current.Layers) { seen.Add(l.Pixels.Data); if (l.Mask != null) seen.Add(l.Mask); if (l.Vector != null) seen.Add(l.Vector); }
        foreach (var a in MaterialEditing.Assets(current)) seen.Add(a.Pixels.Data);
        long bytes = 0;
        foreach (var entry in past.Concat(future)) foreach (var a in MaterialEditing.Assets(entry.State))
            if (seen.Add(a.Pixels.Data)) bytes += a.Pixels.Data.LongLength;
        foreach (var entry in past.Concat(future)) foreach (var l in entry.State.Layers)
        {
            if (seen.Add(l.Pixels.Data)) bytes += l.Pixels.Data.Length;
            if (l.Mask != null && seen.Add(l.Mask)) bytes += l.Mask.Length;
            if (l.Vector != null && seen.Add(l.Vector)) bytes += l.Vector.ByteLength;
        }
        return bytes;
    }
    static bool SameState(Document a, Document b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Dpi != b.Dpi || a.Name != b.Name || a.ActiveId != b.ActiveId || a.Layers.Count != b.Layers.Count || !a.Artboards.SequenceEqual(b.Artboards) || !a.Materials.SequenceEqual(b.Materials) || !a.MaterialRegions.SequenceEqual(b.MaterialRegions)) return false;
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var x = a.Layers[i]; var y = b.Layers[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Visible != y.Visible || x.Locked != y.Locked || x.Opacity != y.Opacity || x.Blend != y.Blend ||
                x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.FlipX != y.FlipX || x.FlipY != y.FlipY ||
                x.Kind != y.Kind || x.ParentId != y.ParentId || x.Category != y.Category || x.SourceLayerName != y.SourceLayerName || x.Clipped != y.Clipped || x.ScaleX != y.ScaleX || x.ScaleY != y.ScaleY ||
                x.Shape != y.Shape || x.Text != y.Text || x.Vector != y.Vector || x.Material != y.Material || x.Warp != y.Warp || !DocumentFeatures.SameAdjustment(x.Adjustment, y.Adjustment) ||
                !ReferenceEquals(x.Pixels.Data, y.Pixels.Data) || !ReferenceEquals(x.Mask, y.Mask)) return false;
        }
        return true;
    }
    public Document Undo(Document d)
    {
        if (!CanUndo) return d;
        var e = past[^1]; past.RemoveAt(past.Count - 1); future.Add((e.Label, d.Snapshot()));
        var result = e.State.Snapshot(); Trim(result); return result;
    }
    public Document Redo(Document d)
    {
        if (!CanRedo) return d;
        var e = future[^1]; future.RemoveAt(future.Count - 1); past.Add((e.Label, d.Snapshot()));
        var result = e.State.Snapshot(); Trim(result); return result;
    }
}

public sealed record Selection(Rect Bounds, bool Ellipse = false)
{
    public byte[]? Coverage { get; init; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    // A precision selection can store a cropped, denser mask in document space.
    // Ordinary masks keep the original one-sample-per-document-pixel mapping.
    public Rect? CoverageBounds { get; init; }
    public Geometry? Contour { get; init; }
    public double Weight(double x, double y)
    {
        if (!Bounds.Contains(x, y)) return 0;
        if (Coverage != null)
        {
            var area = CoverageBounds ?? new Rect(0, 0, CanvasWidth, CanvasHeight);
            int ix = (int)Math.Floor((x - area.X) * CanvasWidth / area.Width), iy = (int)Math.Floor((y - area.Y) * CanvasHeight / area.Height);
            if (ix < 0 || iy < 0 || ix >= CanvasWidth || iy >= CanvasHeight) return 0;
            return Coverage[iy * CanvasWidth + ix] / 255.0;
        }
        if (!Ellipse) return 1;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return 0;
        double dx = (x - Bounds.X - Bounds.Width / 2) / (Bounds.Width / 2), dy = (y - Bounds.Y - Bounds.Height / 2) / (Bounds.Height / 2);
        return dx * dx + dy * dy <= 1 ? 1 : 0;
    }
    public bool Contains(double x, double y) => Weight(x, y) > 0;
}
