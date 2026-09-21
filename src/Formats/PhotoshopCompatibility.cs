using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Compositor.Windows;

public static class PhotoshopCompatibility
{
    internal static readonly Dictionary<string, BlendMode> Blends = new()
    {
        ["norm"] = BlendMode.Normal, ["mul "] = BlendMode.Multiply, ["scrn"] = BlendMode.Screen,
        ["over"] = BlendMode.Overlay, ["sLit"] = BlendMode.SoftLight, ["dark"] = BlendMode.Darken,
        ["lite"] = BlendMode.Lighten, ["diff"] = BlendMode.Difference, ["div "] = BlendMode.ColorDodge,
        ["idiv"] = BlendMode.ColorBurn, ["hue "] = BlendMode.Hue, ["sat "] = BlendMode.Saturation,
        ["colr"] = BlendMode.Color, ["lum "] = BlendMode.Luminosity
    };
    public static CompatibilityResult Read(string path, bool layers, CancellationToken token = default) => PsdReader.Read(path, layers, token);

    public static bool CanWriteLayers(Document doc) => doc.Layers.All(l => l.Kind != LayerKind.Group && l.Kind != LayerKind.Adjustment && l.ParentId == null && !l.Clipped);
    // PSD v1, RGB/8, raw planar channels. Independent reader tests verify the file layout.
    public static void Write(Document document, Stream output, bool layers, CancellationToken token = default)
    {
        document.Validate();
        if (layers && !CanWriteLayers(document)) throw new NotSupportedException("그룹·조정·클리핑이 있는 문서는 합성 PSD로 내보내 주세요.");
        if (document.Width > 30_000 || document.Height > 30_000)
            throw new InvalidDataException("PSD 내보내기는 한 변 30,000px까지 지원합니다. 더 큰 이미지는 PNG 또는 TIFF로 저장해 주세요.");
        long layerBytes = (long)document.Width * document.Height * 4 * document.Layers.Count;
        if (layers && layerBytes > Document.MaxLayerBytes) throw new InvalidDataException($"PSD 레이어 출력이 {Document.MaxLayerBytes / (1024L * 1024 * 1024):N0}GB를 초과합니다. 합성 이미지로 출력해 주세요.");
        // The v1 writer below stores signed 32-bit section lengths. Check the
        // outer layer section, including names and channel headers, before rendering.
        long sectionBytes = 10; // inner section length, layer count, global mask length
        foreach (string name in layers ? document.Layers.Select(l => l.Name) : [document.Name])
        {
            int asciiBytes = Encoding.ASCII.GetByteCount(name.Length > 255 ? name[..255] : name);
            int pascalBytes = (asciiBytes + 4) / 4 * 4;
            sectionBytes += (long)document.Width * document.Height * 4 + 90 + pascalBytes + (long)name.Length * 2;
        }
        if (sectionBytes > int.MaxValue)
            throw new InvalidDataException("PSD 레이어 섹션이 2GB를 초과합니다. 합성 PSD 또는 PNG·TIFF로 저장해 주세요.");
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        void U16(int n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)n)); writer.Write(b); }
        void I16(short n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, n); writer.Write(b); }
        void I32(int n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, n); writer.Write(b); }
        void Text(string s) => writer.Write(Encoding.ASCII.GetBytes(s));
        void Section(Action action) { long p = output.Position; I32(0); action(); long end = output.Position; output.Position = p; I32(checked((int)(end - p - 4))); output.Position = end; }
        void Plane(Raster raster, int ch) { for (int y = 0; y < raster.Height; y++) { token.ThrowIfCancellationRequested(); for (int x = 0; x < raster.Width; x++) writer.Write(raster.Data[(y * raster.Width + x) * 4 + ch]); } }
        Text("8BPS"); U16(1); writer.Write(new byte[6]); U16(4); I32(document.Height); I32(document.Width); U16(8); U16(3); I32(0);
        Section(() => { Text("8BIM"); U16(1005); U16(0); I32(16); I32((int)Math.Round(document.Dpi * 65536)); U16(1); U16(1); I32((int)Math.Round(document.Dpi * 65536)); U16(1); U16(1); });
        var merged = Imaging.Render(document, token);
        Section(() =>
        {
            Section(() =>
            {
                // Negative count marks the first extra merged channel as transparency.
                var selected = layers ? document.Layers.AsEnumerable().Reverse().ToArray() : [new Layer { Name = document.Name, Pixels = merged }];
                I16((short)-selected.Length);
                int planeBytes = checked(document.Width * document.Height);
                foreach (var layer in selected)
                {
                    I32(0); I32(0); I32(document.Height); I32(document.Width); U16(4);
                    foreach (short channel in new short[] { 0, 1, 2, -1 }) { I16(channel); I32(planeBytes + 2); }
                    Text("8BIM"); Text(Blends.First(p => p.Value == layer.Blend).Key); writer.Write(Imaging.Byte(layer.Opacity * 255)); writer.Write((byte)0); writer.Write((byte)(layer.Visible ? 0 : 2)); writer.Write((byte)0);
                    Section(() =>
                    {
                        I32(0); I32(0);
                        byte[] name = Encoding.ASCII.GetBytes(layer.Name.Length > 255 ? layer.Name[..255] : layer.Name); writer.Write((byte)name.Length); writer.Write(name); writer.Write(new byte[(4 - (name.Length + 1) % 4) % 4]);
                        Text("8BIM"); Text("luni"); Section(() => { I32(layer.Name.Length); writer.Write(Encoding.BigEndianUnicode.GetBytes(layer.Name)); });
                    });
                }
                foreach (var layer in selected)
                {
                    var single = new Document { Width = document.Width, Height = document.Height }; var copy = layer.Snapshot(); copy.Visible = true; copy.Opacity = 1; copy.Blend = BlendMode.Normal; single.Add(copy);
                    var raster = Imaging.Render(single, token);
                    foreach (int channel in new[] { 2, 1, 0, 3 }) { U16(0); Plane(raster, channel); }
                }
                if ((output.Position & 1) != 0) writer.Write((byte)0);
            });
            I32(0);
        });
        U16(0); foreach (int channel in new[] { 2, 1, 0, 3 }) Plane(merged, channel);
    }
}
