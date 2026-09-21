using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public sealed class Raster
{
    public const int MaxPixels = 16_777_216;
    public int Width { get; }
    public int Height { get; }
    // Straight (not premultiplied) BGRA. Published rasters are immutable; edits clone first.
    public byte[] Data { get; }
    public Raster(int width, int height, byte[]? data = null)
    {
        ValidateSize(width, height);
        Width = width; Height = height;
        if (data != null && data.Length != (long)width * height * 4) throw new InvalidDataException("픽셀 데이터 크기가 잘못되었습니다.");
        Data = data ?? new byte[width * height * 4];
    }
    public static void ValidateSize(int w, int h)
    {
        if (w < 1 || h < 1 || w > 8192 || h > 8192 || (long)w * h > MaxPixels)
            throw new InvalidDataException("이미지는 한 변 8,192px, 전체 1,677만 픽셀 이하로 사용하세요.");
    }
    public Raster Clone() => new(Width, Height, (byte[])Data.Clone());
    public BitmapSource Bitmap()
    {
        var b = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Width * 4);
        b.Freeze(); return b;
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
        using var buffer = new MemoryStream(); enc.Save(buffer); buffer.Position = 0; buffer.CopyTo(s);
    }
    public static Raster Solid(int w, int h, Color color)
    {
        var r = new Raster(w, h);
        for (int i = 0; i < r.Data.Length; i += 4) { r.Data[i] = color.B; r.Data[i + 1] = color.G; r.Data[i + 2] = color.R; r.Data[i + 3] = color.A; }
        return r;
    }
}

public enum BlendMode { Normal, Multiply, Screen, Overlay, SoftLight, Darken, Lighten, Difference, ColorDodge, ColorBurn, Hue, Saturation, Color, Luminosity }
public enum LayerKind { Raster, Text, Adjustment, Group }

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
    public bool Clipped { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public TextSpec? Text { get; set; }
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
    public const int MaxLayers = 128;
    public const long MaxLayerBytes = 384L * 1024 * 1024;
    readonly long layerByteLimit;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public string Name { get; set; } = "제목 없음";
    public Guid Revision { get; set; } = Guid.NewGuid();
    public List<Layer> Layers { get; set; } = [];
    public Guid ActiveId { get; set; }
    public Layer? Active => Layers.Find(l => l.Id == ActiveId);
    public Document(long layerByteLimit = MaxLayerBytes)
    {
        if (layerByteLimit < 0) throw new ArgumentOutOfRangeException(nameof(layerByteLimit));
        this.layerByteLimit = layerByteLimit;
    }
    public Document Snapshot() => new(layerByteLimit) { Width = Width, Height = Height, Name = Name, Revision = Revision, ActiveId = ActiveId, Layers = Layers.Select(l => l.Snapshot()).ToList() };
    public void Add(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ValidateLayer(layer);
        if (Layers.Count >= MaxLayers) throw new InvalidOperationException($"최대 {MaxLayers}개 레이어를 지원합니다.");
        if (Layers.Any(l => l.Id == layer.Id)) throw new InvalidOperationException("레이어 ID가 중복되었습니다.");
        var bytes = Layers.Sum(l => (long)l.Pixels.Data.Length + (l.Mask?.Length ?? 0));
        var incoming = (long)layer.Pixels.Data.Length + (layer.Mask?.Length ?? 0);
        if (bytes + incoming > layerByteLimit) throw new InvalidOperationException("레이어 메모리 한도(384MB)를 초과합니다.");
        Layers.Add(layer); ActiveId = layer.Id;
    }
    public void Validate()
    {
        Raster.ValidateSize(Width, Height);
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("작업 이름이 비어 있습니다.");
        if (Encoding.UTF8.GetByteCount(Name) > 16_384) throw new InvalidDataException("작업 이름이 너무 깁니다.");
        if (Layers == null || Layers.Count > MaxLayers) throw new InvalidDataException("레이어 수가 올바르지 않습니다.");
        var ids = new HashSet<Guid>();
        long bytes = 0;
        foreach (var layer in Layers)
        {
            if (layer == null) throw new InvalidDataException("레이어 정보가 없습니다.");
            ValidateLayer(layer);
            if (!ids.Add(layer.Id)) throw new InvalidDataException("레이어 ID가 중복되었습니다.");
            bytes += (long)layer.Pixels.Data.Length + (layer.Mask?.Length ?? 0);
            if (bytes > layerByteLimit) throw new InvalidDataException("레이어 메모리 한도(384MB)를 초과합니다.");
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
    static void ValidateLayer(Layer layer)
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
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var l in current.Layers) { seen.Add(l.Pixels.Data); if (l.Mask != null) seen.Add(l.Mask); }
        long bytes = 0;
        foreach (var entry in past.Concat(future)) foreach (var l in entry.State.Layers)
        {
            if (seen.Add(l.Pixels.Data)) bytes += l.Pixels.Data.Length;
            if (l.Mask != null && seen.Add(l.Mask)) bytes += l.Mask.Length;
        }
        return bytes;
    }
    static bool SameState(Document a, Document b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Name != b.Name || a.ActiveId != b.ActiveId || a.Layers.Count != b.Layers.Count) return false;
        for (int i = 0; i < a.Layers.Count; i++)
        {
            var x = a.Layers[i]; var y = b.Layers[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Visible != y.Visible || x.Locked != y.Locked || x.Opacity != y.Opacity || x.Blend != y.Blend ||
                x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.FlipX != y.FlipX || x.FlipY != y.FlipY ||
                x.Kind != y.Kind || x.ParentId != y.ParentId || x.Clipped != y.Clipped || x.ScaleX != y.ScaleX || x.ScaleY != y.ScaleY ||
                x.Text != y.Text || x.Warp != y.Warp || !DocumentFeatures.SameAdjustment(x.Adjustment, y.Adjustment) ||
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
    public double Weight(double x, double y)
    {
        if (!Bounds.Contains(x, y)) return 0;
        if (Coverage != null)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
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
