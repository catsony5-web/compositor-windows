using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

/// <summary>ICC-managed CMYK TIFF output and an sRGB round-trip preview.</summary>
public static class CmykExport
{
    const long MaximumProfileBytes = 64L * 1024 * 1024;

    public static void Write(Raster raster, Stream stream, string? profilePath = null, double dpi = 300)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("출력 스트림에 쓸 수 없습니다.", nameof(stream));
        ValidateDpi(dpi);

        var destination = LoadDestination(profilePath, out _);
        var cmyk = ConvertToCmyk(raster, destination, dpi);
        var contexts = new ReadOnlyCollection<ColorContext>([destination]);
        var frame = BitmapFrame.Create(cmyk, null, null!, contexts);
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        encoder.Frames.Add(frame);

        // WIC's TIFF encoder requires a seekable stream. Buffering also prevents a partially
        // encoded image from being written to a caller-provided non-seekable stream.
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    public static void Export(Document document, string path, string? profilePath = null, double dpi = 300)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateDpi(dpi);

        // Validate and initialize the requested profile before creating an output file.
        _ = LoadDestination(profilePath, out _);
        var rendered = DesignRenderer.RenderOutput(document);
        ProjectStore.AtomicWrite(path, stream => Write(rendered, stream, profilePath, dpi));
    }

    public static Raster Preview(Raster raster, string? profilePath = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        var destination = LoadDestination(profilePath, out _);
        var cmyk = ConvertToCmyk(raster, destination, 96);
        var srgb = new ColorContext(PixelFormats.Bgra32);
        var converted = new ColorConvertedBitmap(cmyk, destination, srgb, PixelFormats.Bgra32);
        converted.Freeze();
        return Raster.FromBitmap(converted);
    }

    public static string ProfileName(string? path = null)
    {
        _ = LoadDestination(path, out var profile);
        string name = ReadProfileDescription(profile!) ?? Path.GetFileNameWithoutExtension(Path.GetFullPath(path ?? DefaultProfilePath()));
        return path == null ? "Windows CMYK: " + name : name;
    }

    static BitmapSource ConvertToCmyk(Raster raster, ColorContext destination, double dpi)
    {
        var flattened = new byte[raster.Data.Length];
        for (int i = 0; i < flattened.Length; i += 4)
        {
            double alpha = raster.Data[i + 3] / 255.0;
            flattened[i] = Imaging.Byte(raster.Data[i] * alpha + 255 * (1 - alpha));
            flattened[i + 1] = Imaging.Byte(raster.Data[i + 1] * alpha + 255 * (1 - alpha));
            flattened[i + 2] = Imaging.Byte(raster.Data[i + 2] * alpha + 255 * (1 - alpha));
            flattened[i + 3] = 255;
        }

        var source = BitmapSource.Create(raster.Width, raster.Height, dpi, dpi, PixelFormats.Bgra32, null, flattened, raster.Width * 4);
        source.Freeze();
        var srgb = new ColorContext(PixelFormats.Bgra32);
        var converted = new ColorConvertedBitmap(source, srgb, destination, PixelFormats.Cmyk32);
        converted.Freeze();
        return converted;
    }

    static ColorContext LoadDestination(string? path, out byte[]? profile)
    {
        profile = null;
        string fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultProfilePath() : path);
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 128 || stream.Length > MaximumProfileBytes)
                throw new InvalidDataException("ICC 프로필 크기가 올바르지 않습니다.");
            profile = new byte[checked((int)stream.Length)];
            stream.ReadExactly(profile);
        }
        ValidateCmykProfile(profile);

        try { return new ColorContext(new Uri(fullPath, UriKind.Absolute)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or FileFormatException or COMException)
        {
            throw new InvalidDataException("ICC 프로필을 불러올 수 없습니다.", error);
        }
    }

    static void ValidateCmykProfile(byte[] bytes)
    {
        uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        if (declaredSize < 128 || declaredSize > bytes.Length ||
            !bytes.AsSpan(36, 4).SequenceEqual("acsp"u8) ||
            !bytes.AsSpan(16, 4).SequenceEqual("CMYK"u8))
            throw new InvalidDataException("CMYK ICC 프로필이 아닙니다.");
    }

    static string? ReadProfileDescription(byte[] profile)
    {
        if (profile.Length < 132) return null;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128, 4));
        if (count > 4096 || 132L + count * 12L > profile.Length) return null;
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + i * 12;
            if (!profile.AsSpan(entry, 4).SequenceEqual("desc"u8)) continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4, 4));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 8, 4));
            if (offset > int.MaxValue || length > int.MaxValue || (long)offset + length > profile.Length || length < 12) return null;
            var tag = profile.AsSpan((int)offset, (int)length);
            if (tag[..4].SequenceEqual("desc"u8))
            {
                uint textLength = BinaryPrimitives.ReadUInt32BigEndian(tag.Slice(8, 4));
                if (textLength is 0 || textLength > int.MaxValue || 12L + textLength > tag.Length) return null;
                return CleanName(Encoding.ASCII.GetString(tag.Slice(12, (int)textLength)).TrimEnd('\0'));
            }
            if (tag[..4].SequenceEqual("mluc"u8) && tag.Length >= 28)
            {
                uint recordCount = BinaryPrimitives.ReadUInt32BigEndian(tag.Slice(8, 4));
                uint recordSize = BinaryPrimitives.ReadUInt32BigEndian(tag.Slice(12, 4));
                if (recordCount == 0 || recordSize < 12 || 16L + recordSize * recordCount > tag.Length) return null;
                uint textLength = BinaryPrimitives.ReadUInt32BigEndian(tag.Slice(20, 4));
                uint textOffset = BinaryPrimitives.ReadUInt32BigEndian(tag.Slice(24, 4));
                if ((textLength & 1) != 0 || textLength > int.MaxValue || textOffset > int.MaxValue || (long)textOffset + textLength > tag.Length) return null;
                return CleanName(Encoding.BigEndianUnicode.GetString(tag.Slice((int)textOffset, (int)textLength)).TrimEnd('\0'));
            }
        }
        return null;
    }

    static string? CleanName(string value)
    {
        value = new(value.Where(c => !char.IsControl(c)).Take(256).ToArray());
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static void ValidateDpi(double dpi)
    {
        if (!double.IsFinite(dpi) || dpi < 1 || dpi > 9_600)
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI는 1에서 9,600 사이여야 합니다.");
    }

    static string DefaultProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm");
}
