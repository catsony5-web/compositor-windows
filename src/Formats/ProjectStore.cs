using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class ProjectStore
{
    const long MaxManifestBytes = 32L * 1024 * 1024;
    sealed class ManifestBuffer : MemoryStream
    {
        void CheckSize(int count)
        {
            if (Length + count > MaxManifestBytes) throw new InvalidDataException("작업 정보가 32MiB를 초과합니다. 객체 이름과 텍스트 길이를 줄여 주세요.");
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckSize(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckSize(buffer.Length); base.Write(buffer); }
    }
    public sealed class Manifest
    {
        public int Version { get; set; } = 2;
        public int Width { get; set; }
        public int Height { get; set; }
        public double Dpi { get; set; } = 96;
        public string? Name { get; set; } = "";
        public Guid ActiveId { get; set; }
        public List<LayerInfo?>? Layers { get; set; } = [];
        public List<Artboard>? Artboards { get; set; } = [];
    }
    public sealed class LayerInfo
    {
        public Guid Id { get; set; }
        public string? Name { get; set; } = "";
        public bool Visible { get; set; }
        public bool Locked { get; set; }
        public double Opacity { get; set; }
        public BlendMode Blend { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Scale { get; set; }
        public double Rotation { get; set; }
        public bool FlipX { get; set; }
        public bool FlipY { get; set; }
        public bool HasMask { get; set; }
        public int? SharedGroupPixelsIndex { get; set; }
        public LayerKind Kind { get; set; }
        public Guid? ParentId { get; set; }
        public LayerCategory Category { get; set; }
        public string? SourceLayerName { get; set; }
        public bool Clipped { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public TextSpec? Text { get; set; }
        public ShapeSpec? Shape { get; set; }
        public VectorInfo? Vector { get; set; }
        public AdjustmentSpec? Adjustment { get; set; }
        public WarpQuad? Warp { get; set; }
        public Layer ToLayer(Raster pixels, byte[]? mask = null) => new() { Id = Id, Name = Name!, Pixels = pixels, Mask = mask, Visible = Visible, Locked = Locked, Opacity = Opacity, Blend = Blend, X = X, Y = Y, Scale = Scale, Rotation = Rotation, FlipX = FlipX, FlipY = FlipY, Kind = Kind, ParentId = ParentId, Category = Category, SourceLayerName = SourceLayerName, Clipped = Clipped, ScaleX = ScaleX, ScaleY = ScaleY, Shape = Shape, Text = Text, Adjustment = Adjustment, Warp = Warp };
    }
    public sealed record VectorInfo(VectorFormat Format, int Width, int Height, int Page);
    public static void AtomicWrite(string path, Action<Stream> write)
    {
        string full = Path.GetFullPath(path), temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(s); s.Flush(true); }
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Save(Document doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Validate();
        var manifest = new Manifest { Version = doc.Layers.Any(l => l.Vector != null) ? 3 : 2, Width = doc.Width, Height = doc.Height, Dpi = doc.Dpi, Name = doc.Name, ActiveId = doc.ActiveId, Artboards = doc.Artboards.ToList() };
        var groupSources = new Dictionary<(byte[] Data, int Width, int Height), int>();
        foreach (var l in doc.Layers)
        {
            int? sharedGroupIndex = null;
            if (l.Kind == LayerKind.Group)
            {
                var key = (l.Pixels.Data, l.Pixels.Width, l.Pixels.Height);
                if (groupSources.TryGetValue(key, out int sourceIndex)) { sharedGroupIndex = sourceIndex; manifest.Version = 4; }
                else groupSources.Add(key, manifest.Layers!.Count);
            }
            manifest.Layers!.Add(new LayerInfo { Id = l.Id, Name = l.Name, Visible = l.Visible, Locked = l.Locked, Opacity = l.Opacity, Blend = l.Blend, X = l.X, Y = l.Y, Scale = l.Scale, Rotation = l.Rotation, FlipX = l.FlipX, FlipY = l.FlipY, HasMask = l.Mask != null, Kind = l.Kind, ParentId = l.ParentId, Clipped = l.Clipped, ScaleX = l.ScaleX, ScaleY = l.ScaleY, Shape = l.Shape, Text = l.Text, Adjustment = l.Adjustment, Warp = l.Warp,
                Category = l.Category, SourceLayerName = l.SourceLayerName,
                Vector = l.Vector is { } vector ? new(vector.Format, vector.Width, vector.Height, vector.Page) : null, SharedGroupPixelsIndex = sharedGroupIndex });
        }
        if (doc.Artboards.Count > 0 || doc.Layers.Any(l => l.Category != LayerCategory.Automatic || l.SourceLayerName != null)) manifest.Version = 5;
        // Reject oversized metadata before encoding any payload or touching an
        // existing project. Serialization itself is bounded as node counts grow.
        using var metadata = new ManifestBuffer();
        JsonSerializer.Serialize(metadata, manifest, new JsonSerializerOptions { WriteIndented = true });
        metadata.Position = 0;
        AtomicWrite(path, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
            for (int index = 0; index < doc.Layers.Count; index++)
            {
                var l = doc.Layers[index];
                if (manifest.Layers![index]!.SharedGroupPixelsIndex == null)
                    using (var s = zip.CreateEntry($"layers/{index}.png", CompressionLevel.NoCompression).Open()) l.Pixels.WritePng(s);
                if (l.Vector is { } vector)
                {
                    using var source = zip.CreateEntry($"vectors/{index}.source", CompressionLevel.Optimal).Open(); vector.Write(source);
                }
                if (l.Mask != null) using (var s = zip.CreateEntry($"layers/{index}.mask", CompressionLevel.Optimal).Open()) s.Write(l.Mask);
            }
            using var meta = zip.CreateEntry("document.json").Open();
            metadata.CopyTo(meta);
        });
    }
    public static Document Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("document.json") ?? throw new InvalidDataException("작업 파일 형식이 올바르지 않습니다.");
        if (entry.Length > MaxManifestBytes) throw new InvalidDataException("작업 정보가 너무 큽니다.");
        using var meta = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(meta) ?? throw new InvalidDataException("작업 정보를 읽을 수 없습니다.");
        ValidateManifest(manifest);
        var layers = manifest.Layers!;
        var doc = new Document { Width = manifest.Width, Height = manifest.Height, Dpi = manifest.Dpi, Name = manifest.Name!, Artboards = manifest.Artboards! };
        var emptyGroups = new Dictionary<(int Width, int Height), Raster>();
        var groupPixels = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long vectorBytes = 0, pixelBytes = 0;
        for (int index = 0; index < layers.Count; index++)
        {
            var l = layers[index]!;
            Raster pixels;
            if (l.SharedGroupPixelsIndex is { } sourceIndex) pixels = doc.Layers[sourceIndex].Pixels;
            else
            {
                var imageEntry = zip.GetEntry($"layers/{index}.png") ?? throw new InvalidDataException("레이어 이미지가 없습니다.");
                if (imageEntry.Length > Raster.MaxEncodedBytes) throw new InvalidDataException("레이어 이미지가 너무 큽니다.");
                using (var s = imageEntry.Open())
                using (Stream staged = imageEntry.Length <= ImageStaging.MemoryThresholdBytes ? new MemoryStream() : ImageStaging.CreateTemporaryStream())
                {
                    s.CopyTo(staged); staged.Position = 0;
                    var decoder = BitmapDecoder.Create(staged, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count == 0) throw new InvalidDataException("레이어 이미지 프레임이 없습니다.");
                    var frame = decoder.Frames[0]; Raster.ValidateSize(frame.PixelWidth, frame.PixelHeight);
                    long required = (long)frame.PixelWidth * frame.PixelHeight * (l.HasMask ? 5 : 4);
                    bool mayShareLegacyGroup = l.Kind == LayerKind.Group && emptyGroups.ContainsKey((frame.PixelWidth, frame.PixelHeight));
                    if (!mayShareLegacyGroup && pixelBytes + required > Document.MaxLayerBytes)
                        throw new InvalidDataException("레이어 메모리 한도를 초과합니다.");
                    pixels = Raster.FromBitmap(frame);
                }
            }
            // Group rasters define their coordinate space, while the compositor
            // paints their children. CAD groups share an empty document-sized
            // surface; restore that sharing only after validating every PNG.
            if (l.Kind == LayerKind.Group && l.SharedGroupPixelsIndex == null && pixels.Data.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                var size = (pixels.Width, pixels.Height);
                if (emptyGroups.TryGetValue(size, out var shared)) pixels = shared;
                else emptyGroups.Add(size, pixels);
            }
            byte[]? mask = null;
            if (l.HasMask)
            {
                var maskEntry = zip.GetEntry($"layers/{index}.mask") ?? throw new InvalidDataException("마스크가 없습니다.");
                if (maskEntry.Length != pixels.Width * pixels.Height) throw new InvalidDataException("마스크 크기가 다릅니다.");
                if (pixelBytes + maskEntry.Length > Document.MaxLayerBytes) throw new InvalidDataException("레이어 메모리 한도를 초과합니다.");
                mask = new byte[maskEntry.Length]; using var s = maskEntry.Open(); s.ReadExactly(mask);
            }
            var layer = l.ToLayer(pixels, mask);
            pixelBytes += Document.StorageBytes(layer, groupPixels);
            if (pixelBytes > Document.MaxLayerBytes) throw new InvalidDataException("레이어 메모리 한도를 초과합니다.");
            if (l.Vector is { } info)
            {
                var source = zip.GetEntry($"vectors/{index}.source") ?? throw new InvalidDataException("벡터 원본이 없습니다.");
                vectorBytes += source.Length;
                if (vectorBytes > VectorContent.MaxDocumentBytes) throw new InvalidDataException("벡터 원본이 너무 큽니다.");
                using var input = source.Open(); layer.Vector = VectorContent.Read(info.Format, info.Width, info.Height, info.Page, input);
                if (info.Format == VectorFormat.Paths) _ = layer.Vector.Drawing;
            }
            // Validate decoded dimensions and the cumulative document budget before
            // rendering a new geometry cache. The retained shape is the source of truth;
            // a stale thumbnail payload must not later change rasterization or .comp export.
            Document.ValidateLayer(layer);
            doc.Layers.Add(layer);
            if (layer.Kind == LayerKind.Shape) layer.Pixels = VectorShapes.Render(layer.Shape!);
        }
        doc.ActiveId = manifest.ActiveId;
        doc.Validate();
        return doc;
    }
    static void ValidateManifest(Manifest manifest)
    {
        if (manifest.Version is not (1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("지원하지 않는 작업 파일 버전입니다.");
        if (manifest.Artboards == null || manifest.Version < 5 && (manifest.Artboards.Count != 0 || manifest.Layers?.Any(l => l?.Category != LayerCategory.Automatic || l.SourceLayerName != null) == true))
            throw new InvalidDataException("대지와 도면 레이어 정보가 작업 파일 버전과 맞지 않습니다.");
        Raster.ValidateSize(manifest.Width, manifest.Height);
        if (string.IsNullOrWhiteSpace(manifest.Name) || Encoding.UTF8.GetByteCount(manifest.Name) > 16_384)
            throw new InvalidDataException("작업 이름이 올바르지 않습니다.");
        if (manifest.Layers == null || manifest.Layers.Count > Document.MaxNodes)
            throw new InvalidDataException("레이어 수가 올바르지 않습니다.");
        var ids = new HashSet<Guid>();
        for (int index = 0; index < manifest.Layers.Count; index++)
        {
            var l = manifest.Layers[index];
            if (l?.SharedGroupPixelsIndex is { } sourceIndex && (manifest.Version < 4 || l.Kind != LayerKind.Group ||
                sourceIndex < 0 || sourceIndex >= index || manifest.Layers[sourceIndex]?.Kind != LayerKind.Group))
                throw new InvalidDataException("공유 그룹 이미지 참조가 올바르지 않습니다.");
            if (l != null && ((l.Kind == LayerKind.Vector) != (l.Vector != null) || l.Vector != null && (manifest.Version < 3 || !Enum.IsDefined(l.Vector.Format) || l.Vector.Page < 1 || l.Vector.Page > 100000)))
                throw new InvalidDataException("벡터 원본 정보가 올바르지 않습니다.");
            if (l?.Vector is { } vector) Raster.ValidateSize(vector.Width, vector.Height);
            if (l == null || l.Id == Guid.Empty || !ids.Add(l.Id) || string.IsNullOrWhiteSpace(l.Name) || Encoding.UTF8.GetByteCount(l.Name) > 16_384 ||
                !double.IsFinite(l.X) || !double.IsFinite(l.Y) || Math.Abs(l.X) > 100_000 || Math.Abs(l.Y) > 100_000 ||
                !double.IsFinite(l.Scale) || l.Scale < .01 || l.Scale > 20 ||
                !double.IsFinite(l.Rotation) || Math.Abs(l.Rotation) > 36_000 ||
                !double.IsFinite(l.Opacity) || l.Opacity < 0 || l.Opacity > 1 || !Enum.IsDefined(l.Blend))
                throw new InvalidDataException("레이어 속성이 올바르지 않습니다.");
        }
        if (manifest.ActiveId != Guid.Empty && !ids.Contains(manifest.ActiveId))
            throw new InvalidDataException("활성 레이어가 존재하지 않습니다.");
        // Validate all structural metadata before decoding image payloads, including cycles.
        var header = new Document { Width = manifest.Width, Height = manifest.Height, Dpi = manifest.Dpi, Name = manifest.Name!, ActiveId = manifest.ActiveId, Artboards = manifest.Artboards };
        var placeholder = new Raster(1, 1);
        foreach (var l in manifest.Layers) header.Layers.Add(l!.ToLayer(placeholder));
        header.ValidateMetadata();
    }
    public static void Export(Document doc, string path)
    {
        var rendered = DesignRenderer.RenderOutput(doc);
        bool jpeg = Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        if (jpeg)
        {
            for (int i = 0; i < rendered.Data.Length; i += 4)
            {
                double a = rendered.Data[i + 3] / 255.0;
                for (int c = 0; c < 3; c++) rendered.Data[i + c] = Imaging.Byte(rendered.Data[i + c] * a + 255 * (1 - a));
                rendered.Data[i + 3] = 255;
            }
        }
        AtomicWrite(path, s =>
        {
            BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered.Bitmap(doc.Dpi))); encoder.Save(s);
        });
    }
}
