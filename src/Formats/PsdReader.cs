using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Compositor.Windows;

/// <summary>Bounded RGB/gray 8-bit PSD/PSB reader. Raw, PackBits, ZIP and ZIP prediction.</summary>
internal sealed class PsdReader : IDisposable
{
    sealed class Channel { public short Id; public long Length, Offset; }
    sealed class Record
    {
        public int Left, Top, Width, Height; public string Name = "PSD 레이어", Blend = "norm"; public byte Opacity, Flags; public bool Clip, Complex;
        public int MaskLeft, MaskTop, MaskWidth, MaskHeight; public byte MaskDefault, MaskFlags; public bool HasMask;
        // Section divider type: 0 = layer, 1/2 = group folder (open/closed), 3 = end of a group's children.
        public int Section; public string? SectionBlend;
        public readonly List<Channel> Channels = [];
    }
    readonly FileStream stream; readonly CancellationToken token; readonly List<string> warnings = [];
    readonly List<Record> records = [];
    int width, height, channels, mode; bool large, mergedAlpha; double dpi = 96;
    PsdReader(string path, CancellationToken token) { stream = File.OpenRead(path); this.token = token; }
    public void Dispose() => stream.Dispose();
    public static CompatibilityResult Read(string path, bool layers, CancellationToken token)
    {
        CompatibilityImport.ValidateFile(path); using var reader = new PsdReader(path, token); return reader.Load(path, layers);
    }
    byte[] Bytes(int count)
    {
        if (count < 0 || count > stream.Length - stream.Position) throw new InvalidDataException("PSD 데이터 길이가 파일 범위를 벗어납니다.");
        var result = new byte[count]; stream.ReadExactly(result); return result;
    }
    byte Byte() { int value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException("PSD 데이터가 잘렸습니다."); return (byte)value; }
    ushort U16() { Span<byte> b = stackalloc byte[2]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    short I16() => unchecked((short)U16());
    uint U32() { Span<byte> b = stackalloc byte[4]; stream.ReadExactly(b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    int I32() => unchecked((int)U32());
    long Length(bool eight = false)
    {
        if (!eight) return U32(); Span<byte> b = stackalloc byte[8]; stream.ReadExactly(b); ulong value = BinaryPrimitives.ReadUInt64BigEndian(b);
        if (value > (ulong)stream.Length) throw new InvalidDataException("PSB 섹션 길이가 파일보다 큽니다."); return (long)value;
    }
    string Tag() => Encoding.ASCII.GetString(Bytes(4));
    long End(long size, long limit) { long end = checked(stream.Position + size); if (size < 0 || end > limit || end > stream.Length) throw new InvalidDataException("PSD 섹션 길이가 올바르지 않습니다."); return end; }
    void Seek(long position) { if (position < stream.Position || position > stream.Length) throw new InvalidDataException("PSD 섹션이 중첩되거나 잘렸습니다."); stream.Position = position; }
    string Pascal(int alignment)
    {
        int n = Byte(); string value = Encoding.Latin1.GetString(Bytes(n)); int padding = (alignment - (n + 1) % alignment) % alignment; Bytes(padding); return value;
    }
    CompatibilityResult Load(string path, bool layers)
    {
        if (Tag() != "8BPS") throw new InvalidDataException("PSD/PSB 파일 서명이 아닙니다.");
        int version = U16(); if (version is not (1 or 2)) throw new NotSupportedException("지원하지 않는 PSD 버전입니다."); large = version == 2;
        Bytes(6); channels = U16(); height = checked((int)U32()); width = checked((int)U32()); Raster.ValidateSize(width, height);
        int depth = U16(); mode = U16();
        if (depth != 8 || mode is not (1 or 3)) throw new NotSupportedException("현재 PSD/PSB 가져오기는 RGB 또는 회색조 / 8비트를 지원합니다. 파일을 만든 프로그램에서 RGB / 8비트 사본으로 저장하거나 PDF·TIFF로 내보내 주세요.");
        if (channels < (mode == 3 ? 3 : 1) || channels > 16) throw new NotSupportedException("PSD 채널 구성이 지원 범위를 벗어났습니다.");
        Seek(End(Length(), stream.Length)); ReadResources();
        long maskEnd = End(Length(large), stream.Length);
        if (stream.Position < maskEnd)
        {
            long infoEnd = End(Length(large), maskEnd);
            if (stream.Position < infoEnd)
            {
                int count = I16(); mergedAlpha = count < 0; count = Math.Abs(count);
                if (count > 4096) throw new InvalidDataException("PSD 레이어 수가 한도를 초과합니다. 레이어 수를 줄이거나 합성 이미지로 열어 주세요.");
                for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); records.Add(ReadRecord(infoEnd)); }
                // Group folders and their end markers do not use image layer slots.
                if (layers && records.Count(r => r.Section == 0) > Document.MaxLayers) throw new InvalidDataException("PSD 레이어 수가 한도를 초과합니다. 레이어 수를 줄이거나 합성 이미지로 열어 주세요.");
                foreach (var record in records) foreach (var channel in record.Channels) { channel.Offset = stream.Position; Seek(End(channel.Length, infoEnd)); }
                Seek(infoEnd);
            }
            Seek(maskEnd);
        }
        long mergedOffset = stream.Position;
        if (!layers || records.Count == 0)
        {
            var raster = DecodeMerged(mergedOffset); warnings.Add("파일에 저장된 합성 이미지를 가져왔습니다. 레이어·효과·문자는 하나의 이미지에 합쳐져 있습니다.");
            return new(CompatibilityImport.Single(path, raster, dpi, "PSD 합성 이미지"), warnings.Distinct().ToArray());
        }
        long memory = 0; int folders = 0;
        foreach (var record in records)
        {
            if (record.Section is 1 or 2)
            {
                string blend = record.SectionBlend ?? record.Blend;
                if (record.Complex || blend != "pass" && !PsdCompatibility.Blends.ContainsKey(blend)) throw new NotSupportedException("그룹·조정 레이어 또는 지원하지 않는 혼합 모드가 있습니다. ‘합성 이미지’로 가져와 주세요.");
                if (record.HasMask && (record.MaskFlags & 2) == 0) memory = checked(memory + (long)width * height);
                folders++; continue;
            }
            if (record.Section == 3) continue;
            if (record.Complex || !PsdCompatibility.Blends.ContainsKey(record.Blend)) throw new NotSupportedException("그룹·조정 레이어 또는 지원하지 않는 혼합 모드가 있습니다. ‘합성 이미지’로 가져와 주세요.");
            if (record.Width == 0 || record.Height == 0) continue;
            Raster.ValidateSize(record.Width, record.Height); memory = checked(memory + (long)record.Width * record.Height * 5);
            if (memory > Document.MaxLayerBytes) throw new InvalidDataException($"PSD 레이어 메모리가 {Document.MaxLayerBytes / (1024L * 1024 * 1024):N0}GB를 초과합니다. 합성 이미지로 가져와 주세요.");
        }
        // Every group shares one document-sized coordinate surface, like other folders.
        if (folders > 0) memory = checked(memory + (long)width * height * 4);
        if (memory > Document.MaxLayerBytes) throw new InvalidDataException($"PSD 레이어 메모리가 {Document.MaxLayerBytes / (1024L * 1024 * 1024):N0}GB를 초과합니다. 합성 이미지로 가져와 주세요.");
        var doc = new Document { Width = width, Height = height, Name = Path.GetFileNameWithoutExtension(path), Dpi = dpi };
        Raster? surface = null;
        byte[]? Mask(Record record, byte[]? plane, int left, int top, int w, int h)
        {
            if (!record.HasMask || (record.MaskFlags & 2) != 0) return null;
            var result = new byte[checked(w * h)]; if (record.MaskDefault != 0) Array.Fill(result, record.MaskDefault);
            if (plane == null) return result;
            int dx = checked(record.MaskLeft - ((record.MaskFlags & 1) != 0 ? 0 : left)), dy = checked(record.MaskTop - ((record.MaskFlags & 1) != 0 ? 0 : top));
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            { long mx = (long)x - dx, my = (long)y - dy; if (mx >= 0 && my >= 0 && mx < record.MaskWidth && my < record.MaskHeight) result[y * w + x] = plane[(int)(my * record.MaskWidth + mx)]; }
            return result;
        }
        byte[]? MaskPlane(Record record)
        {
            var channel = record.Channels.FirstOrDefault(c => c.Id == -2);
            return channel != null && record.HasMask && (record.MaskFlags & 2) == 0 && record.MaskWidth > 0 && record.MaskHeight > 0
                ? DecodePlane(channel.Offset, channel.Length, record.MaskWidth, record.MaskHeight) : null;
        }
        string Name(Record record, string fallback) => string.IsNullOrWhiteSpace(record.Name) ? fallback : record.Name;
        // Records are stored bottom-first. A divider opens a group's children; the folder record closes it.
        var open = new Stack<List<Layer>>(); var level = new List<Layer>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Section == 3)
            {
                if (open.Count >= 16) throw new InvalidDataException("PSD 그룹이 16단계보다 깊게 중첩되어 있습니다. 합성 이미지로 가져와 주세요.");
                open.Push(level); level = []; continue;
            }
            if (record.Section is 1 or 2)
            {
                if (open.Count == 0) throw new InvalidDataException("PSD 그룹 구조가 올바르지 않습니다. 합성 이미지로 가져와 주세요.");
                string blend = record.SectionBlend ?? record.Blend;
                surface ??= new Raster(width, height);
                var group = new Layer { Name = Name(record, "PSD 그룹"), Kind = LayerKind.Group, Pixels = surface, Visible = (record.Flags & 2) == 0,
                    Opacity = record.Opacity / 255d, Clipped = record.Clip, Blend = blend == "pass" ? BlendMode.Normal : PsdCompatibility.Blends[blend] };
                group.Mask = Mask(record, MaskPlane(record), 0, 0, width, height);
                foreach (var child in level) child.ParentId ??= group.Id;
                var parent = open.Pop(); parent.AddRange(level); parent.Add(group); level = parent;
                continue;
            }
            if (record.Width == 0 || record.Height == 0)
            {
                // Keep empty layers so names and stacking order survive a round trip.
                level.Add(new Layer { Name = Name(record, "PSD 레이어"), Pixels = new Raster(1, 1), X = record.Left, Y = record.Top, Visible = (record.Flags & 2) == 0,
                    Opacity = record.Opacity / 255d, Clipped = record.Clip, Blend = PsdCompatibility.Blends[record.Blend] });
                continue;
            }
            var raster = new Raster(record.Width, record.Height); for (int i = 3; i < raster.Data.Length; i += 4) raster.Data[i] = 255;
            var used = new HashSet<short>();
            foreach (var channel in record.Channels)
            {
                if (!used.Add(channel.Id)) throw new InvalidDataException("PSD에 중복 채널이 있습니다.");
                if (channel.Id is >= -1 and <= 2)
                {
                    var plane = DecodePlane(channel.Offset, channel.Length, record.Width, record.Height);
                    Put(raster, plane, channel.Id);
                }
            }
            if (!used.Contains(0) || mode == 3 && (!used.Contains(1) || !used.Contains(2))) throw new InvalidDataException("PSD 레이어에 필요한 색상 채널이 없습니다.");
            var layer = new Layer { Name = Name(record, "PSD 레이어"), Pixels = raster, X = record.Left, Y = record.Top,
                Visible = (record.Flags & 2) == 0, Locked = (record.Flags & 1) != 0, Opacity = record.Opacity / 255d, Clipped = record.Clip, Blend = PsdCompatibility.Blends[record.Blend] };
            layer.Mask = Mask(record, MaskPlane(record), record.Left, record.Top, record.Width, record.Height);
            level.Add(layer);
        }
        // Tolerate a missing folder record: its children stay at the level above.
        while (open.Count > 0) { var parent = open.Pop(); parent.AddRange(level); level = parent; }
        foreach (var layer in level) doc.Add(layer);
        if (!doc.Layers.Any(l => l.Kind != LayerKind.Group)) throw new NotSupportedException("가져올 픽셀 레이어가 없습니다. 합성 이미지로 열어 주세요.");
        warnings.Add("기본 픽셀 레이어·위치·표시·불투명도·마스크·지원 혼합 모드를 가져왔습니다. 문자·내장 개체는 저장된 픽셀로 읽으며 편집 속성은 유지되지 않습니다.");
        if (folders > 0) warnings.Add($"PSD 그룹 {folders:N0}개의 이름·순서·표시·불투명도·마스크를 유지했습니다.");
        doc.Validate(); return new(doc, warnings.Distinct().ToArray());
    }
    void ReadResources()
    {
        long end = End(Length(), stream.Length);
        while (stream.Position < end)
        {
            token.ThrowIfCancellationRequested(); if (end - stream.Position < 12) throw new InvalidDataException("잘린 PSD 이미지 리소스입니다.");
            string signature = Tag(); if (signature is not ("8BIM" or "MeSa")) throw new InvalidDataException("PSD 이미지 리소스 서명이 올바르지 않습니다.");
            int id = U16(); Pascal(2); long length = Length(), resourceEnd = End(length, end);
            if (id == 1005 && length >= 16)
            {
                double value = U32() / 65536d; int units = U16(); if (units == 2) value *= 2.54; if (value >= 1 && value <= 9600) dpi = value;
            }
            if (id == 1039) warnings.Add("PSD의 내장 ICC는 색상 변환에 적용하지 않습니다. 정확한 색상이 필요하면 파일을 만든 프로그램에서 sRGB로 변환해 저장한 파일을 사용하세요.");
            Seek(resourceEnd); if ((length & 1) != 0) Byte(); if (stream.Position > end) throw new InvalidDataException("PSD 리소스 패딩 오류입니다.");
        }
    }
    Record ReadRecord(long limit)
    {
        var r = new Record { Top = I32(), Left = I32() }; int bottom = I32(), right = I32(); r.Width = checked(right - r.Left); r.Height = checked(bottom - r.Top);
        if (r.Width < 0 || r.Height < 0) throw new InvalidDataException("PSD 레이어 경계가 올바르지 않습니다.");
        int count = U16(); if (count > 16) throw new NotSupportedException("레이어당 16개 이상의 PSD 채널은 지원하지 않습니다.");
        for (int i = 0; i < count; i++) r.Channels.Add(new Channel { Id = I16(), Length = Length(large) });
        if (Tag() != "8BIM") throw new InvalidDataException("PSD 레이어 서명이 올바르지 않습니다.");
        r.Blend = Tag(); r.Opacity = Byte(); r.Clip = Byte() != 0; r.Flags = Byte(); Byte(); long extraEnd = End(Length(), limit);
        long maskLength = Length(), maskEnd = End(maskLength, extraEnd);
        if (maskLength != 0)
        {
            if (maskLength < 18) throw new InvalidDataException("PSD 마스크 데이터가 잘렸습니다.");
            r.HasMask = true; r.MaskTop = I32(); r.MaskLeft = I32(); int mb = I32(), mr = I32(); r.MaskWidth = checked(mr - r.MaskLeft); r.MaskHeight = checked(mb - r.MaskTop); r.MaskDefault = Byte(); r.MaskFlags = Byte();
            if (r.MaskWidth < 0 || r.MaskHeight < 0) throw new InvalidDataException("PSD 마스크 경계 오류입니다.");
            if ((r.MaskFlags & 16) != 0) warnings.Add("PSD 마스크의 추가 농도·페더 속성은 보존하지 않습니다.");
        }
        Seek(maskEnd); Seek(End(Length(), extraEnd)); r.Name = Pascal(4);
        while (stream.Position + 12 <= extraEnd)
        {
            string signature = Tag(); if (signature is not ("8BIM" or "8B64")) throw new InvalidDataException("PSD 추가 레이어 정보가 올바르지 않습니다.");
            string key = Tag(); bool wide = large && (signature == "8B64" || key is "LMsk" or "Lr16" or "Lr32" or "Layr" or "Mt16" or "Mt32" or "Mtrn" or "Alph" or "FMsk" or "lnk2" or "FEid" or "FXid" or "PxSD");
            long size = Length(wide), end = End(size, extraEnd);
            if (key == "luni" && size >= 4) { uint n = U32(); if (n > 65536 || (long)n * 2 > end - stream.Position) throw new InvalidDataException("PSD 레이어 이름이 너무 깁니다."); r.Name = Encoding.BigEndianUnicode.GetString(Bytes((int)n * 2)); }
            if ((key is "lsct" or "lsdk") && size >= 4)
            {
                uint type = U32(); r.Section = type <= 3 ? (int)type : 0;
                if (size >= 12 && Tag() == "8BIM") r.SectionBlend = Tag();
            }
            if (key is "levl" or "curv" or "hue2" or "brit" or "blnc" or "SoCo" or "GdFl" or "PtFl" or "blwh" or "expA" or "vibA" or "clrL" or "selc" or "mixr" or "phfl" or "nvrt" or "post" or "thrs" or "grdm") r.Complex = true;
            if (key is "lrFX" or "lfx2" or "vmsk" or "vsms") warnings.Add("레이어 효과·벡터 마스크는 개별 레이어 모드에서 재현하지 않습니다. 외형이 중요하면 합성 이미지로 가져오세요.");
            Seek(end); if ((size & 1) != 0 && stream.Position < extraEnd) Byte();
        }
        Seek(extraEnd); return r;
    }
    void Put(Raster raster, byte[] plane, int id)
    {
        int channel = id == -1 ? 3 : 2 - id;
        if (plane.Length != raster.Width * raster.Height) throw new InvalidDataException("PSD 채널 크기가 맞지 않습니다.");
        for (int i = 0; i < plane.Length; i++) { if (mode == 1 && id == 0) raster.Data[i * 4] = raster.Data[i * 4 + 1] = raster.Data[i * 4 + 2] = plane[i]; else raster.Data[i * 4 + channel] = plane[i]; }
    }
    byte[] DecodePlane(long offset, long length, int w, int h)
    {
        Raster.ValidateSize(w, h); stream.Position = offset; long end = End(length, stream.Length); if (length < 2) throw new InvalidDataException("빈 PSD 채널입니다.");
        int compression = U16(); var data = new byte[checked(w * h)];
        if (compression == 0) { if (end - stream.Position < data.Length) throw new InvalidDataException("PSD 픽셀이 잘렸습니다."); stream.ReadExactly(data); }
        else if (compression == 1)
        {
            var rows = RowLengths(h, end); for (int y = 0; y < h; y++) Unpack(data.AsSpan(y * w, w), rows[y], end);
        }
        else if (compression is 2 or 3) Inflate(data, end, compression == 3, w);
        else throw new NotSupportedException("지원하지 않는 PSD 압축입니다.");
        if (stream.Position > end) throw new InvalidDataException("PSD 채널이 선언된 길이를 초과했습니다."); token.ThrowIfCancellationRequested(); return data;
    }
    uint[] RowLengths(int count, long end)
    {
        if ((long)count * (large ? 4 : 2) > end - stream.Position) throw new InvalidDataException("PSD RLE 행 목록이 잘렸습니다.");
        var rows = new uint[count]; long sum = 0; for (int i = 0; i < count; i++) { rows[i] = large ? U32() : U16(); sum += rows[i]; }
        if (sum > end - stream.Position) throw new InvalidDataException("PSD RLE 데이터가 잘렸습니다."); return rows;
    }
    void Unpack(Span<byte> row, uint length, long limit)
    {
        token.ThrowIfCancellationRequested(); long end = End(length, limit); int written = 0;
        while (stream.Position < end)
        {
            int n = unchecked((sbyte)Byte()); if (n == -128) continue; int count = n >= 0 ? n + 1 : 1 - n;
            if (count > row.Length - written) throw new InvalidDataException("PSD RLE가 행 길이를 초과합니다.");
            if (n >= 0) { if (count > end - stream.Position) throw new InvalidDataException("PSD RLE 리터럴이 잘렸습니다."); stream.ReadExactly(row.Slice(written, count)); }
            else { if (stream.Position >= end) throw new InvalidDataException("PSD RLE 반복 값이 없습니다."); row.Slice(written, count).Fill(Byte()); }
            written += count;
        }
        if (written != row.Length) throw new InvalidDataException("PSD RLE 행이 완성되지 않았습니다.");
    }
    void Inflate(byte[] destination, long end, bool predict, int w)
    {
        // Restrict the compressed input to this channel; limit expansion to exactly its dimensions.
        if (end - stream.Position > CompatibilityImport.MaxFileBytes) throw new InvalidDataException("PSD 압축 채널이 너무 큽니다.");
        using (var input = new BoundedReadStream(stream, end - stream.Position, token))
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        {
            ReadPlane(zlib, destination, predict, w);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("PSD ZIP 데이터가 픽셀 크기를 초과합니다.");
        }
        // Preserve the channel boundary even when zlib finishes before padding.
        stream.Position = end;
    }
    void ReadPlane(Stream input, byte[] destination, bool predict, int w)
    {
        for (int offset = 0; offset < destination.Length;)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(1024 * 1024, destination.Length - offset);
            input.ReadExactly(destination.AsSpan(offset, count)); offset += count;
        }
        if (predict) for (int start = 0; start < destination.Length; start += w) { token.ThrowIfCancellationRequested(); for (int x = 1; x < w; x++) destination[start + x] = unchecked((byte)(destination[start + x] + destination[start + x - 1])); }
    }
    Raster DecodeMerged(long offset)
    {
        stream.Position = offset; int compression = U16(); var raster = new Raster(width, height); for (int i = 3; i < raster.Data.Length; i += 4) raster.Data[i] = 255;
        int planeBytes = checked(width * height), colorChannels = mode == 3 ? 3 : 1; uint[]? rows = compression == 1 ? RowLengths(checked(height * channels), stream.Length) : null;
        if (compression is < 0 or > 3) throw new NotSupportedException("지원하지 않는 PSD 압축입니다.");
        // ZIP channels share one stream. Reuse one plane instead of allocating
        // channels × pixels bytes, which can exceed a managed array's length.
        using var input = compression is 2 or 3 ? new BoundedReadStream(stream, stream.Length - stream.Position, token) : null;
        using var zlib = input != null ? new ZLibStream(input, CompressionMode.Decompress) : null;
        var plane = new byte[planeBytes];
        for (int c = 0; c < channels; c++)
        {
            token.ThrowIfCancellationRequested();
            if (zlib != null) ReadPlane(zlib, plane, compression == 3, width);
            else if (compression == 0) ReadPlane(stream, plane, false, width);
            else for (int y = 0; y < height; y++) Unpack(plane.AsSpan(y * width, width), rows![c * height + y], stream.Length);
            if (c < colorChannels) Put(raster, plane, c); else if (c == colorChannels && mergedAlpha) Put(raster, plane, -1);
        }
        if (zlib != null && zlib.ReadByte() != -1) throw new InvalidDataException("PSD ZIP 데이터가 픽셀 크기를 초과합니다.");
        return raster;
    }

    // Leave the owning PSD stream open, and prevent decoder read-ahead from
    // consuming another channel or section. Lengths stay 64-bit throughout.
    sealed class BoundedReadStream : Stream
    {
        readonly Stream source;
        readonly long length;
        readonly CancellationToken cancellationToken;
        long remaining;
        public BoundedReadStream(Stream source, long length, CancellationToken cancellationToken)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            this.source = source; this.length = remaining = length; this.cancellationToken = cancellationToken;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer[..count]); remaining -= read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
