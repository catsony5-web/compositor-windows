using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class ProjectStore
{
    public sealed class Manifest
    {
        public int Version { get; set; } = 2;
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Name { get; set; } = "";
        public Guid ActiveId { get; set; }
        public List<LayerInfo?>? Layers { get; set; } = [];
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
        public LayerKind Kind { get; set; }
        public Guid? ParentId { get; set; }
        public bool Clipped { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public TextSpec? Text { get; set; }
        public AdjustmentSpec? Adjustment { get; set; }
        public WarpQuad? Warp { get; set; }
        public Layer ToLayer(Raster pixels, byte[]? mask = null) => new() { Id = Id, Name = Name!, Pixels = pixels, Mask = mask, Visible = Visible, Locked = Locked, Opacity = Opacity, Blend = Blend, X = X, Y = Y, Scale = Scale, Rotation = Rotation, FlipX = FlipX, FlipY = FlipY, Kind = Kind, ParentId = ParentId, Clipped = Clipped, ScaleX = ScaleX, ScaleY = ScaleY, Text = Text, Adjustment = Adjustment, Warp = Warp };
    }
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
        AtomicWrite(path, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
            var manifest = new Manifest { Width = doc.Width, Height = doc.Height, Name = doc.Name, ActiveId = doc.ActiveId };
            for (int index = 0; index < doc.Layers.Count; index++)
            {
                var l = doc.Layers[index];
                manifest.Layers!.Add(new LayerInfo { Id = l.Id, Name = l.Name, Visible = l.Visible, Locked = l.Locked, Opacity = l.Opacity, Blend = l.Blend, X = l.X, Y = l.Y, Scale = l.Scale, Rotation = l.Rotation, FlipX = l.FlipX, FlipY = l.FlipY, HasMask = l.Mask != null, Kind = l.Kind, ParentId = l.ParentId, Clipped = l.Clipped, ScaleX = l.ScaleX, ScaleY = l.ScaleY, Text = l.Text, Adjustment = l.Adjustment, Warp = l.Warp });
                using (var s = zip.CreateEntry($"layers/{index}.png", CompressionLevel.NoCompression).Open()) l.Pixels.WritePng(s);
                if (l.Mask != null) using (var s = zip.CreateEntry($"layers/{index}.mask", CompressionLevel.Optimal).Open()) s.Write(l.Mask);
            }
            using var meta = zip.CreateEntry("document.json").Open();
            JsonSerializer.Serialize(meta, manifest, new JsonSerializerOptions { WriteIndented = true });
        });
    }
    public static Document Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("document.json") ?? throw new InvalidDataException("작업 파일 형식이 올바르지 않습니다.");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("작업 정보가 너무 큽니다.");
        using var meta = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(meta) ?? throw new InvalidDataException("작업 정보를 읽을 수 없습니다.");
        ValidateManifest(manifest);
        var layers = manifest.Layers!;
        var doc = new Document { Width = manifest.Width, Height = manifest.Height, Name = manifest.Name! };
        for (int index = 0; index < layers.Count; index++)
        {
            var l = layers[index]!;
            var imageEntry = zip.GetEntry($"layers/{index}.png") ?? throw new InvalidDataException("레이어 이미지가 없습니다.");
            if (imageEntry.Length > 100L * 1024 * 1024) throw new InvalidDataException("레이어 이미지가 너무 큽니다.");
            Raster pixels;
            using (var s = imageEntry.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); ms.Position = 0; pixels = Raster.Load(ms); }
            byte[]? mask = null;
            if (l.HasMask)
            {
                var maskEntry = zip.GetEntry($"layers/{index}.mask") ?? throw new InvalidDataException("마스크가 없습니다.");
                if (maskEntry.Length != pixels.Width * pixels.Height) throw new InvalidDataException("마스크 크기가 다릅니다.");
                mask = new byte[maskEntry.Length]; using var s = maskEntry.Open(); s.ReadExactly(mask);
            }
            doc.Add(l.ToLayer(pixels, mask));
        }
        doc.ActiveId = manifest.ActiveId;
        doc.Validate();
        return doc;
    }
    static void ValidateManifest(Manifest manifest)
    {
        if (manifest.Version is not (1 or 2)) throw new InvalidDataException("지원하지 않는 작업 파일 버전입니다.");
        Raster.ValidateSize(manifest.Width, manifest.Height);
        if (string.IsNullOrWhiteSpace(manifest.Name) || Encoding.UTF8.GetByteCount(manifest.Name) > 16_384)
            throw new InvalidDataException("작업 이름이 올바르지 않습니다.");
        if (manifest.Layers == null || manifest.Layers.Count > Document.MaxLayers)
            throw new InvalidDataException("레이어 수가 올바르지 않습니다.");
        var ids = new HashSet<Guid>();
        foreach (var l in manifest.Layers)
        {
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
        var header = new Document { Width = manifest.Width, Height = manifest.Height, Name = manifest.Name!, ActiveId = manifest.ActiveId };
        var placeholder = new Raster(1, 1);
        foreach (var l in manifest.Layers) header.Layers.Add(l!.ToLayer(placeholder));
        header.Validate();
    }
    public static void Export(Document doc, string path)
    {
        var rendered = Imaging.Render(doc);
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
            encoder.Frames.Add(BitmapFrame.Create(rendered.Bitmap())); encoder.Save(s);
        });
    }
}
