using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;

namespace Compositor.Windows;

/// <summary>
/// Layered PSD (v1, RGB / 8-bit) that keeps the document structure live: layer names, stacking
/// order, visibility, opacity, blend modes, clipping, layer masks and groups as PSD folders
/// (section dividers). Text, shapes, vectors, materials and unknown layer kinds are stored as
/// pixel layers of their visible appearance. An adjustment layer is stored as the adjusted result
/// of the layers below it, keeping its opacity, blend mode and mask. Each layer is cropped to its
/// content, and channels use PackBits when that is smaller than raw data. Records are stored
/// bottom-first as the format specifies.
/// </summary>
internal static class PsdLayerExport
{
    internal const int MaxRecords = 4096;
    const string DividerName = "</Layer group>";

    internal enum NodeKind { Pixels, Folder, Adjustment, Merged }

    internal sealed class Node(Layer source, Layer[] ancestors, NodeKind kind)
    {
        public Layer Source { get; } = source;
        public Layer[] Ancestors { get; } = ancestors;
        public NodeKind Kind { get; } = kind;
        public List<Node> Children { get; } = [];
        public Layer[] Members { get; init; } = [];
        public Int32Rect Region { get; init; }
        public bool Hidden { get; set; }
        public bool HasMask { get; init; }
        public string BlendKey { get; set; } = "norm";
        // Placeholder positions inside the record buffer.
        internal long BoundsAt, LengthsAt, MaskAt;
    }

    /// <summary>What the export will contain; built without rendering so dialogs can explain it.</summary>
    internal sealed record Plan(IReadOnlyList<Node> Roots, int Layers, int Folders, int Converted, int Adjustments, int Perspective, int Collapsed)
    {
        public int Records => Layers + Folders * 2;
    }

    enum RecordKind { Layer, Folder, Divider }

    public static bool CanWrite(Document document)
    {
        try { Build(document); return true; }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException) { return false; }
    }

    public static Plan Build(Document document)
    {
        document.Validate();
        if (document.Width > 30_000 || document.Height > 30_000)
            throw new InvalidDataException("PSD 내보내기는 한 변 30,000px까지 지원합니다. 더 큰 이미지는 PNG 또는 TIFF로 저장해 주세요.");
        var children = LayerExportRender.Children(document); var lookup = document.Layers.ToDictionary(l => l.Id);
        int Depth(Layer layer) { int depth = 0; for (var parent = layer.ParentId; parent is { } id; parent = lookup[id].ParentId) depth++; return depth; }
        int deepest = document.Layers.Where(l => l.Kind == LayerKind.Group).Select(Depth).DefaultIfEmpty(-1).Max();
        // Too many layers for one .psd: merge the deepest groups first, keeping the upper structure.
        var plan = Build(document, children, int.MaxValue);
        for (int limit = deepest; plan.Layers > Document.MaxLayers && limit >= 0; limit--) plan = Build(document, children, limit);
        if (plan.Layers > Document.MaxLayers)
            throw new InvalidDataException($"레이어 {plan.Layers:N0}개는 .psd 레이어 한도 {Document.MaxLayers}개를 넘습니다. 레이어를 그룹으로 묶거나 ‘.psd · 한 장으로 합치기’를 선택해 주세요.");
        if (plan.Records > MaxRecords)
            throw new InvalidDataException($"그룹 {plan.Folders:N0}개는 .psd에 담을 수 있는 수를 넘습니다. 빈 그룹을 정리하거나 ‘.psd · 한 장으로 합치기’를 선택해 주세요.");
        return plan;
    }

    static Plan Build(Document document, Dictionary<Guid, Layer[]> children, int limit)
    {
        int layers = 0, folders = 0, converted = 0, adjustments = 0, perspective = 0, collapsed = 0;
        static bool AllNormal(IEnumerable<Node> nodes) => nodes.All(n => n.Source.Blend == BlendMode.Normal && (n.Kind != NodeKind.Folder || AllNormal(n.Children)));
        bool Covers(Layer layer, Layer[] ancestors, Int32Rect region)
        {
            if (layer.Warp != null || layer.Rotation % 360 != 0 || ancestors.Any(a => a.Rotation % 360 != 0)) return false;
            var bounds = LayerExportRender.DocumentBounds(layer, ancestors);
            return !bounds.IsEmpty && bounds.Left <= region.X && bounds.Top <= region.Y && bounds.Right >= region.X + region.Width && bounds.Bottom >= region.Y + region.Height;
        }
        List<Node> Stack(Layer[] stack, Layer[] ancestors, int depth)
        {
            var nodes = new List<Node>(stack.Length);
            for (int i = 0; i < stack.Length; i++)
            {
                var layer = stack[i];
                int basis = i; while (basis >= 0 && stack[basis].Clipped) basis--;
                // Clipped layers without a drawable base are never shown in the editor either.
                bool orphan = layer.Clipped && (basis < 0 || stack[basis].Kind == LayerKind.Adjustment);
                Node node;
                if (layer.Kind == LayerKind.Group && layer.Warp == null && depth < limit)
                {
                    node = new Node(layer, ancestors, NodeKind.Folder) { Region = LayerExportRender.Footprint(document, layer, ancestors), HasMask = layer.Mask != null };
                    node.Children.AddRange(Stack(LayerExportRender.ChildrenOf(layer, children), [.. ancestors, layer], depth + 1));
                    node.BlendKey = layer.Blend == BlendMode.Normal && AllNormal(node.Children) ? "pass" : Key(layer.Blend);
                    folders++;
                }
                else if (layer.Kind == LayerKind.Group)
                {
                    // Perspective applies to the whole group image, so its children cannot stay separate.
                    node = new Node(layer, ancestors, NodeKind.Merged) { Members = [LayerExportRender.Plain(layer)], Region = LayerExportRender.Footprint(document, layer, ancestors), HasMask = layer.Mask != null };
                    if (layer.Warp != null) perspective++; else collapsed++;
                }
                else if (layer.Kind == LayerKind.Adjustment)
                {
                    Layer[] members = []; var region = new Int32Rect();
                    var adjustment = LayerExportRender.Plain(layer);
                    if (!layer.Clipped) { members = [.. stack.Take(i), adjustment]; region = LayerExportRender.StackArea(document, ancestors); }
                    else if (!orphan)
                    {
                        adjustment.Clipped = true;
                        members = [LayerExportRender.Plain(stack[basis], keepMask: true), .. stack.Skip(basis + 1).Take(i - basis - 1), adjustment];
                        region = LayerExportRender.Footprint(document, stack[basis], ancestors);
                    }
                    node = new Node(layer, ancestors, NodeKind.Adjustment) { Members = members, Region = region, HasMask = members.Length > 0 && (layer.Mask != null || !Covers(layer, ancestors, region)) };
                    adjustments++;
                }
                else
                {
                    node = new Node(layer, ancestors, NodeKind.Pixels) { Members = [LayerExportRender.Plain(layer)], Region = LayerExportRender.Footprint(document, layer, ancestors), HasMask = layer.Mask != null };
                    if (layer.Kind != LayerKind.Raster) converted++;
                }
                if (node.Kind != NodeKind.Folder) { layers++; node.BlendKey = Key(layer.Blend); }
                node.Hidden = !layer.Visible || orphan;
                nodes.Add(node);
            }
            return nodes;
        }
        var roots = Stack(LayerExportRender.Roots(document), [], 0);
        return new Plan(roots, layers, folders, converted, adjustments, perspective, collapsed);
    }

    static string Key(BlendMode blend) => PsdCompatibility.Blends.First(p => p.Value == blend).Key;

    static void Records(IEnumerable<Node> nodes, List<(Node Node, RecordKind Kind)> output)
    {
        foreach (var node in nodes)
        {
            if (node.Kind != NodeKind.Folder) { output.Add((node, RecordKind.Layer)); continue; }
            // Bottom-first: the divider closes the group below its children; the folder record follows them.
            output.Add((node, RecordKind.Divider)); Records(node.Children, output); output.Add((node, RecordKind.Folder));
        }
    }

    public static void Write(Document document, Stream output, Action<int, int>? progress = null, CancellationToken token = default)
    {
        var plan = Build(document);
        if (!output.CanSeek) throw new NotSupportedException("레이어를 유지하는 PSD는 파일로만 저장할 수 있습니다.");
        var children = LayerExportRender.Children(document);
        var records = new List<(Node Node, RecordKind Kind)>(plan.Records); Records(plan.Roots, records);

        // Record headers first, with placeholders for bounds and channel lengths patched after rendering.
        using var buffer = new MemoryStream();
        var head = new BigEndian(buffer);
        foreach (var (node, kind) in records) WriteRecord(head, node, kind);
        long worst = 2 + buffer.Length;
        foreach (var (node, kind) in records)
        {
            long area = (long)node.Region.Width * node.Region.Height;
            worst += kind switch { RecordKind.Layer => 4 * (2 + area), RecordKind.Folder => 8, _ => 8 };
            if (kind != RecordKind.Divider && node.HasMask) worst += 2 + area;
        }
        if (worst + 16 > int.MaxValue)
            throw new InvalidDataException("PSD 레이어 섹션이 2GB를 초과합니다. 레이어 수나 크기를 줄이거나 ‘.psd · 한 장으로 합치기’ 또는 PNG·TIFF로 저장해 주세요.");

        var file = new BigEndian(output);
        file.Text("8BPS"); file.U16(1); file.Bytes(new byte[6]); file.U16(4); file.I32(document.Height); file.I32(document.Width); file.U16(8); file.U16(3); file.I32(0);
        file.Section(() =>
        {
            file.Text("8BIM"); file.U16(1005); file.U16(0); file.I32(16);
            file.I32((int)Math.Round(document.Dpi * 65536)); file.U16(1); file.U16(1); file.I32((int)Math.Round(document.Dpi * 65536)); file.U16(1); file.U16(1);
            // Version information: the stored composite is real, so readers may show it directly.
            var version = new MemoryStream(); var v = new BigEndian(version);
            v.I32(1); v.Byte(1); v.Unicode("Morupixel"); v.Unicode("Morupixel"); v.I32(1);
            file.Text("8BIM"); file.U16(1057); file.U16(0); file.I32((int)version.Length); version.WriteTo(output); if ((version.Length & 1) != 0) file.Byte(0);
        });
        long sectionAt = output.Position; file.I32(0);
        long infoAt = output.Position; file.I32(0);
        file.I16(checked((short)-records.Count));
        long recordsAt = output.Position; buffer.WriteTo(output);

        int total = records.Count(r => r.Kind == RecordKind.Layer), done = 0;
        var rowScratch = new byte[document.Width]; var packScratch = new byte[PackBound(document.Width)];
        void Patch(long at, Action write) { long end = output.Position; output.Position = at; write(); output.Position = end; }
        foreach (var (node, kind) in records)
        {
            token.ThrowIfCancellationRequested();
            if (kind == RecordKind.Divider) { for (int c = 0; c < 4; c++) file.U16(0); continue; }
            var lengths = new List<long>();
            if (kind == RecordKind.Layer)
            {
                var (pixels, bounds) = RenderPixels(document, node, children, token);
                Patch(recordsAt + node.BoundsAt, () => { file.I32(bounds.Y); file.I32(bounds.X); file.I32(bounds.Y + bounds.Height); file.I32(bounds.X + bounds.Width); });
                // Alpha, red, green, blue in the order of the channel list.
                foreach (int channel in new[] { 3, 2, 1, 0 }) lengths.Add(WriteChannel(file, pixels, channel, rowScratch, packScratch, token));
            }
            else for (int c = 0; c < 4; c++) { file.U16(0); lengths.Add(2); }
            if (node.HasMask)
            {
                var (mask, bounds) = RenderMask(document, node, children, token);
                Patch(recordsAt + node.MaskAt, () => { file.I32(bounds.Y); file.I32(bounds.X); file.I32(bounds.Y + bounds.Height); file.I32(bounds.X + bounds.Width); });
                // The mask value is the grey level; outside the drawn area it is cleared to hidden.
                lengths.Add(WriteChannel(file, mask, 2, rowScratch, packScratch, token));
            }
            Patch(recordsAt + node.LengthsAt, () => { for (int i = 0; i < lengths.Count; i++) { output.Position += 2; file.I32(checked((int)lengths[i])); } });
            if (kind == RecordKind.Layer) progress?.Invoke(++done, total);
        }
        if (((output.Position - infoAt - 4) & 1) != 0) file.Byte(0);
        Patch(infoAt, () => file.I32(checked((int)(output.Length - infoAt - 4))));
        output.Position = output.Length; file.I32(0); // no global layer mask
        Patch(sectionAt, () => file.I32(checked((int)(output.Length - sectionAt - 4))));
        output.Position = output.Length;

        token.ThrowIfCancellationRequested();
        var merged = DesignRenderer.RenderOutput(document, token);
        WriteMerged(file, merged, rowScratch, packScratch, token);
    }

    static void WriteRecord(BigEndian w, Node node, RecordKind kind)
    {
        var layer = node.Source; bool mask = kind != RecordKind.Divider && node.HasMask;
        node.BoundsAt = kind == RecordKind.Layer ? w.Position : node.BoundsAt;
        w.I32(0); w.I32(0); w.I32(0); w.I32(0);
        short[] ids = mask ? [-1, 0, 1, 2, -2] : [-1, 0, 1, 2];
        w.U16(ids.Length); long lengthsAt = w.Position;
        if (kind != RecordKind.Divider) node.LengthsAt = lengthsAt;
        foreach (short id in ids) { w.I16(id); w.I32(2); }
        w.Text("8BIM"); w.Text(kind == RecordKind.Divider ? "norm" : node.BlendKey);
        w.Byte(kind == RecordKind.Divider ? (byte)255 : Imaging.Byte(layer.Opacity * 255));
        w.Byte((byte)(kind != RecordKind.Divider && layer.Clipped ? 1 : 0));
        // Bit 1 hides the layer; bit 3 marks bit 4 as meaningful ("pixels irrelevant" for group records).
        // Some readers take a group's visibility from its end marker, so both group records carry it.
        byte flags = (byte)(kind == RecordKind.Layer ? 0x08 : 0x18); if (node.Hidden) flags |= 0x02;
        w.Byte(flags); w.Byte(0);
        string name = kind == RecordKind.Divider ? DividerName : layer.Name;
        w.Section(() =>
        {
            if (mask)
            {
                w.I32(20); node.MaskAt = w.Position; w.I32(0); w.I32(0); w.I32(0); w.I32(0);
                w.Byte(0); w.Byte(0); w.Byte(0); w.Byte(0); // outside the mask is hidden; absolute position
            }
            else w.I32(0);
            w.I32(0); // blending ranges
            var ascii = Encoding.ASCII.GetBytes(name.Length > 255 ? name[..255] : name);
            w.Byte((byte)ascii.Length); w.Bytes(ascii); w.Bytes(new byte[(4 - (ascii.Length + 1) % 4) % 4]);
            w.Text("8BIM"); w.Text("luni"); w.Section(() => { w.I32(name.Length); w.Bytes(Encoding.BigEndianUnicode.GetBytes(name)); });
            if (kind == RecordKind.Folder) { w.Text("8BIM"); w.Text("lsct"); w.I32(12); w.I32(1); w.Text("8BIM"); w.Text(node.BlendKey); }
            else if (kind == RecordKind.Divider) { w.Text("8BIM"); w.Text("lsct"); w.I32(4); w.I32(3); }
        });
    }

    static (Raster? Pixels, Int32Rect Bounds) RenderPixels(Document document, Node node, Dictionary<Guid, Layer[]> children, CancellationToken token)
    {
        if (node.Region.Width <= 0 || node.Region.Height <= 0 || node.Members.Length == 0) return (null, new Int32Rect());
        var raster = LayerExportRender.Render(document, node.Ancestors, node.Members, children, node.Region, token);
        var local = LayerExportRender.Opaque(raster, token);
        if (local.Width == 0) return (null, new Int32Rect());
        return (LayerExportRender.Crop(raster, local), new Int32Rect(node.Region.X + local.X, node.Region.Y + local.Y, local.Width, local.Height));
    }

    static (Raster? Mask, Int32Rect Bounds) RenderMask(Document document, Node node, Dictionary<Guid, Layer[]> children, CancellationToken token)
    {
        if (node.Region.Width <= 0 || node.Region.Height <= 0) return (null, new Int32Rect());
        var coverage = LayerExportRender.Coverage(document, node.Source, node.Ancestors, children, node.Region, token);
        // Hidden mask areas equal the default outside the mask rectangle, so keep the rectangle tight.
        for (int i = 0; i < coverage.Data.Length; i += 4) if (coverage.Data[i + 2] == 0) coverage.Data[i + 3] = 0;
        var local = LayerExportRender.Opaque(coverage, token);
        if (local.Width == 0) return (null, new Int32Rect());
        return (LayerExportRender.Crop(coverage, local), new Int32Rect(node.Region.X + local.X, node.Region.Y + local.Y, local.Width, local.Height));
    }

    // One planar channel of a straight BGRA raster. Colour under fully transparent pixels is
    // cleared so empty areas compress; PackBits is used when it is smaller than raw bytes.
    static long WriteChannel(BigEndian w, Raster? raster, int channel, byte[] row, byte[] packed, CancellationToken token)
    {
        if (raster == null) { w.U16(0); return 2; }
        int width = raster.Width, height = raster.Height;
        void Row(int y)
        {
            int start = y * width * 4;
            for (int x = 0; x < width; x++) { int i = start + x * 4; row[x] = channel != 3 && raster.Data[i + 3] == 0 ? (byte)0 : raster.Data[i + channel]; }
        }
        long rle = 2 + 2L * height;
        for (int y = 0; y < height; y++) { if ((y & 255) == 0) token.ThrowIfCancellationRequested(); Row(y); rle += Pack(row.AsSpan(0, width), packed); }
        long raw = 2 + (long)width * height;
        if (rle >= raw)
        {
            w.U16(0);
            for (int y = 0; y < height; y++) { if ((y & 255) == 0) token.ThrowIfCancellationRequested(); Row(y); w.Bytes(row.AsSpan(0, width)); }
            return raw;
        }
        w.U16(1);
        for (int y = 0; y < height; y++) { Row(y); w.U16(Pack(row.AsSpan(0, width), packed)); }
        for (int y = 0; y < height; y++) { if ((y & 255) == 0) token.ThrowIfCancellationRequested(); Row(y); int n = Pack(row.AsSpan(0, width), packed); w.Bytes(packed.AsSpan(0, n)); }
        return rle;
    }

    // Composite image data: PackBits for all four channels, with a patched row-length table.
    static void WriteMerged(BigEndian w, Raster raster, byte[] row, byte[] packed, CancellationToken token)
    {
        int width = raster.Width, height = raster.Height;
        w.U16(1); long tableAt = w.Position; w.Bytes(new byte[checked(8L * height)]);
        var counts = new ushort[4 * height]; int index = 0;
        foreach (int channel in new[] { 2, 1, 0, 3 })
            for (int y = 0; y < height; y++)
            {
                if ((y & 255) == 0) token.ThrowIfCancellationRequested();
                int start = y * width * 4; for (int x = 0; x < width; x++) row[x] = raster.Data[start + x * 4 + channel];
                int n = Pack(row.AsSpan(0, width), packed); counts[index++] = checked((ushort)n); w.Bytes(packed.AsSpan(0, n));
            }
        long end = w.Position; w.Position = tableAt; foreach (var count in counts) w.U16(count); w.Position = end;
    }

    /// <summary>Largest PackBits output for <paramref name="length"/> input bytes (one header per 128 literal bytes).</summary>
    internal static int PackBound(int length) => length + (length + 127) / 128;

    /// <summary>
    /// PackBits: runs of three or more equal bytes are repeated; shorter repeats stay inside the
    /// literal around them. A run packet then always saves at least one byte, so a row never grows
    /// beyond <see cref="PackBound"/> even for noisy photo channels full of isolated equal pairs.
    /// </summary>
    internal static int Pack(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int s = 0, d = 0, n = source.Length;
        static bool RunAt(ReadOnlySpan<byte> row, int i) => i + 2 < row.Length && row[i] == row[i + 1] && row[i] == row[i + 2];
        while (s < n)
        {
            if (RunAt(source, s))
            {
                int run = 3; while (s + run < n && run < 128 && source[s + run] == source[s]) run++;
                destination[d++] = unchecked((byte)(1 - run)); destination[d++] = source[s]; s += run; continue;
            }
            int start = s, length = 0;
            while (s < n && length < 128 && !RunAt(source, s)) { s++; length++; }
            destination[d++] = (byte)(length - 1); source.Slice(start, length).CopyTo(destination[d..]); d += length;
        }
        return d;
    }

    sealed class BigEndian(Stream stream)
    {
        public long Position { get => stream.Position; set => stream.Position = value; }
        public void Byte(byte value) => stream.WriteByte(value);
        public void Bytes(ReadOnlySpan<byte> value) => stream.Write(value);
        public void Text(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        public void U16(int value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)value)); stream.Write(b); }
        public void I16(short value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, value); stream.Write(b); }
        public void I32(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); stream.Write(b); }
        public void Unicode(string value) { I32(value.Length); Bytes(Encoding.BigEndianUnicode.GetBytes(value)); }
        // A 32-bit length followed by its content.
        public void Section(Action body)
        {
            long at = stream.Position; I32(0); body(); long end = stream.Position;
            stream.Position = at; I32(checked((int)(end - at - 4))); stream.Position = end;
        }
    }
}
