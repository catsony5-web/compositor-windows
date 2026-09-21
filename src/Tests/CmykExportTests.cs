using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

public static class CmykExportTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
        static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
        static void Near(byte actual, byte expected, int tolerance, string channel) => Assert(Math.Abs(actual - expected) <= tolerance, $"{channel}: expected {expected}, got {actual}");
        static string CmykProfile()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm");
            if (!File.Exists(path)) throw new FileNotFoundException("Windows RSWOP CMYK profile is unavailable.", path);
            return path;
        }
        static string RgbProfile()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
            if (!File.Exists(path)) throw new FileNotFoundException("Windows sRGB profile is unavailable.", path);
            return path;
        }
        static BitmapFrame Decode(byte[] tiff)
        {
            using var stream = new MemoryStream(tiff, false);
            var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        static byte[] Export(Raster raster, string? profile = null, double dpi = 300)
        {
            using var stream = new MemoryStream(); CmykExport.Write(raster, stream, profile, dpi); return stream.ToArray();
        }
        static byte[] EmbeddedProfile(byte[] tiff)
        {
            Assert(tiff.Length >= 8, "TIFF header is truncated");
            bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
            Assert(little || (tiff[0] == (byte)'M' && tiff[1] == (byte)'M'), "TIFF byte order is invalid");
            ushort U16(int offset) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(offset, 2)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(offset, 2));
            uint U32(int offset) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(offset, 4));
            Assert(U16(2) == 42, "TIFF magic is invalid");
            uint ifd = U32(4); Assert(ifd <= int.MaxValue && ifd + 2 <= tiff.Length, "TIFF IFD is invalid");
            ushort count = U16((int)ifd); Assert((long)ifd + 2 + count * 12L <= tiff.Length, "TIFF IFD is truncated");
            for (int i = 0; i < count; i++)
            {
                int entry = (int)ifd + 2 + i * 12;
                if (U16(entry) != 34675) continue;
                uint length = U32(entry + 4), offset = U32(entry + 8);
                Assert(length >= 128 && length <= int.MaxValue && offset <= int.MaxValue && (long)offset + length <= tiff.Length, "Embedded ICC tag is invalid");
                return tiff.AsSpan((int)offset, (int)length).ToArray();
            }
            throw new InvalidOperationException("TIFF ICC tag is missing");
        }

        test("CMYK TIFF retains pixel format dimensions DPI and ICC context", () =>
        {
            string profile = CmykProfile();
            byte[] tiff = Export(Raster.Solid(7, 5, Color.FromRgb(90, 120, 150)), profile, 360);
            var frame = Decode(tiff);
            Assert(frame.Format == PixelFormats.Cmyk32, $"Unexpected format {frame.Format}");
            Assert(frame.PixelWidth == 7 && frame.PixelHeight == 5);
            Assert(Math.Abs(frame.DpiX - 360) < .01 && Math.Abs(frame.DpiY - 360) < .01, $"Unexpected DPI {frame.DpiX} x {frame.DpiY}");
            Assert(frame.ColorContexts is { Count: > 0 }, "TIFF does not contain an embedded color context");
            var embedded = frame.ColorContexts![0];
            Assert(embedded.ProfileUri != null || (embedded.ToString()?.Length ?? 0) > 0, "Embedded profile is not readable");
            byte[] profileBytes = EmbeddedProfile(tiff);
            Assert(profileBytes.AsSpan(16, 4).SequenceEqual("CMYK"u8) && profileBytes.AsSpan(36, 4).SequenceEqual("acsp"u8), "Embedded ICC is not a CMYK profile");
            Assert(profileBytes.SequenceEqual(File.ReadAllBytes(profile)), "WIC changed the selected ICC profile while embedding it");
        });

        test("CMYK preview round trip is close for gamut-safe colors", () =>
        {
            var source = new Raster(4, 1);
            var colors = new[] { Color.FromRgb(32, 32, 32), Color.FromRgb(128, 128, 128), Color.FromRgb(225, 225, 225), Color.FromRgb(90, 120, 150) };
            for (int x = 0; x < colors.Length; x++) { int i = x * 4; source.Data[i] = colors[x].B; source.Data[i + 1] = colors[x].G; source.Data[i + 2] = colors[x].R; source.Data[i + 3] = 255; }
            var preview = CmykExport.Preview(source, CmykProfile());
            for (int x = 0; x < colors.Length; x++) { int i = x * 4; Near(preview.Data[i], colors[x].B, 40, $"B{x}"); Near(preview.Data[i + 1], colors[x].G, 40, $"G{x}"); Near(preview.Data[i + 2], colors[x].R, 40, $"R{x}"); Assert(preview.Data[i + 3] == 255); }
        });

        test("transparent pixels flatten onto white and source remains immutable", () =>
        {
            var source = Raster.Solid(1, 1, Color.FromArgb(0, 240, 10, 20)); var before = source.Data.ToArray();
            var preview = CmykExport.Preview(source, CmykProfile());
            Assert(source.Data.SequenceEqual(before), "Source raster was modified");
            Near(preview.Data[0], 255, 4, "B"); Near(preview.Data[1], 255, 4, "G"); Near(preview.Data[2], 255, 4, "R"); Assert(preview.Data[3] == 255);
            var frame = Decode(Export(source, CmykProfile()));
            var decoded = new ColorConvertedBitmap(frame, frame.ColorContexts![0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            var roundTrip = Raster.FromBitmap(decoded);
            Near(roundTrip.Data[0], 255, 4, "encoded B"); Near(roundTrip.Data[1], 255, 4, "encoded G"); Near(roundTrip.Data[2], 255, 4, "encoded R");
        });

        test("preview pixels match exported TIFF color-managed round trip", () =>
        {
            string profile = CmykProfile();
            var source = new Raster(5, 2);
            var colors = new[]
            {
                Color.FromArgb(255, 32, 32, 32), Color.FromArgb(192, 220, 80, 40), Color.FromArgb(128, 40, 170, 95),
                Color.FromArgb(64, 70, 110, 210), Color.FromArgb(0, 255, 0, 180), Color.FromArgb(255, 235, 235, 235),
                Color.FromArgb(180, 120, 90, 150), Color.FromArgb(90, 20, 130, 180), Color.FromArgb(255, 90, 120, 150),
                Color.FromArgb(210, 180, 160, 70)
            };
            for (int pixel = 0; pixel < colors.Length; pixel++)
            {
                int i = pixel * 4; var color = colors[pixel];
                source.Data[i] = color.B; source.Data[i + 1] = color.G; source.Data[i + 2] = color.R; source.Data[i + 3] = color.A;
            }

            var preview = CmykExport.Preview(source, profile);
            var frame = Decode(Export(source, profile, 600));
            var converted = new ColorConvertedBitmap(frame, frame.ColorContexts![0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            var exported = Raster.FromBitmap(converted);
            Assert(preview.Width == exported.Width && preview.Height == exported.Height);
            for (int i = 0; i < preview.Data.Length; i++)
                Assert(Math.Abs(preview.Data[i] - exported.Data[i]) <= 1, $"Preview/export pixel byte {i} differs: {preview.Data[i]} vs {exported.Data[i]}");
        });

        test("explicit RGB and malformed profiles are rejected", () =>
        {
            var source = Raster.Solid(1, 1, Colors.White);
            Reject<InvalidDataException>(() => CmykExport.Write(source, new MemoryStream(), RgbProfile()));
            string malformed = Path.Combine(Path.GetTempPath(), "morupixel-bad-" + Guid.NewGuid().ToString("N") + ".icc");
            try { File.WriteAllBytes(malformed, new byte[128]); Reject<InvalidDataException>(() => CmykExport.Write(source, new MemoryStream(), malformed)); }
            finally { File.Delete(malformed); }
        });

        test("invalid profile and DPI preserve an existing export target", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "morupixel-cmyk-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                string target = Path.Combine(directory, "existing.tif"); File.WriteAllText(target, "original");
                var doc = new Document { Width = 1, Height = 1 }; doc.Add(new Layer { Pixels = Raster.Solid(1, 1, Colors.Red) });
                Reject<FileNotFoundException>(() => CmykExport.Export(doc, target, Path.Combine(directory, "missing.icc")));
                Assert(File.ReadAllText(target) == "original", "Invalid profile replaced target");
                Reject<ArgumentOutOfRangeException>(() => CmykExport.Export(doc, target, CmykProfile(), double.NaN));
                Assert(File.ReadAllText(target) == "original", "Invalid DPI replaced target");
                Reject<ArgumentOutOfRangeException>(() => CmykExport.Export(doc, target, CmykProfile(), 9_601));
                Assert(File.ReadAllText(target) == "original", "Out-of-range DPI replaced target");
            }
            finally { Directory.Delete(directory, true); }
        });

        test("profile name describes a validated CMYK profile", () =>
        {
            string name = CmykExport.ProfileName(CmykProfile());
            Assert(!string.IsNullOrWhiteSpace(name) && name.Length <= 256, "Profile description is missing");
            Assert(CmykExport.ProfileName().Contains("CMYK", StringComparison.OrdinalIgnoreCase));
        });
    }
}
