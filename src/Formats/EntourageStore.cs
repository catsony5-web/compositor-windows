using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace Compositor.Windows;

/// <summary>A 내 점경 entry: the user's own cut-out or line drawing, normalized to at most 1024 px.</summary>
public sealed record EntourageCustomItem(Guid Id, string Name, EntourageCategory Category, EntourageView View, double Meters, bool LineDrawing, Raster Pixels)
{
    public string ItemId => EntourageSpec.CustomId(Id);
}

// The user's entourage library (내 점경), per user in %LOCALAPPDATA%\Morupixel\Entourage: one PNG per
// item named by its ID and entourage.json with names, kinds and default sizes in library order. Never
// part of the repository; documents carry their own copy of every item they place (EntourageSpec.Source).
public static class EntourageStore
{
    const long MaxIndexBytes = 256 * 1024, MaxPngBytes = 16L * 1024 * 1024;
    const string IndexFile = "entourage.json";
    static readonly JsonSerializerOptions options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "Entourage");

    public sealed record Entry(Guid Id, string Name, EntourageCategory Category, EntourageView View, double Meters, bool LineDrawing);
    public sealed record Index(int Version, Entry[]? Items);

    static string PngPath(string directory, Guid id) => Path.Combine(directory, id.ToString("N") + ".png");

    public static string CleanName(string? name)
    {
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > EntourageStoreLimits.MaxNameLength) clean = clean[..EntourageStoreLimits.MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "내 점경" : clean;
    }

    public static void ValidatePixels(Raster pixels)
    {
        if (Math.Max(pixels.Width, pixels.Height) > EntourageStoreLimits.MaxSide || (long)pixels.Width * pixels.Height > EntourageStoreLimits.MaxPixels)
            throw new InvalidDataException($"내 점경 이미지는 한 변 {EntourageStoreLimits.MaxSide}px 이하로 저장합니다.");
    }

    // Items in library order. A missing, oversized or damaged entry is skipped, never fatal.
    public static IReadOnlyList<EntourageCustomItem> Load(string directory)
    {
        var result = new List<EntourageCustomItem>();
        try
        {
            var index = new FileInfo(Path.Combine(directory, IndexFile));
            if (!index.Exists || index.Length > MaxIndexBytes) return result;
            var stored = JsonSerializer.Deserialize<Index>(File.ReadAllText(index.FullName), options);
            if (stored is not { Version: 1, Items: { } entries }) return result;
            var seen = new HashSet<Guid>();
            foreach (var entry in entries)
            {
                if (result.Count >= EntourageStoreLimits.MaxItems) break;
                if (entry == null || entry.Id == Guid.Empty || !seen.Add(entry.Id) || !Enum.IsDefined(entry.Category) || !Enum.IsDefined(entry.View) ||
                    !double.IsFinite(entry.Meters) || entry.Meters < EntourageSpec.MinMeters || entry.Meters > EntourageSpec.MaxMeters) continue;
                try
                {
                    var file = new FileInfo(PngPath(directory, entry.Id));
                    if (!file.Exists || file.Length > MaxPngBytes) continue;
                    using var stream = file.OpenRead();
                    var pixels = MaterialTextures.Read(stream);
                    ValidatePixels(pixels);
                    result.Add(new EntourageCustomItem(entry.Id, CleanName(entry.Name), entry.Category, entry.View, entry.Meters, entry.LineDrawing, pixels));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or FormatException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return result;
    }

    // Writes the library: new images as PNG, the index atomically, and removes images no longer listed.
    public static void Save(IReadOnlyList<EntourageCustomItem> items, string directory)
    {
        var kept = items.GroupBy(i => i.Id).Select(g => g.First()).Take(EntourageStoreLimits.MaxItems).ToArray();
        Directory.CreateDirectory(directory);
        foreach (var item in kept)
        {
            string path = PngPath(directory, item.Id);
            if (!File.Exists(path)) ProjectStore.AtomicWrite(path, stream => item.Pixels.WritePng(stream));
        }
        var index = new Index(1, kept.Select(i => new Entry(i.Id, CleanName(i.Name), i.Category, i.View, i.Meters, i.LineDrawing)).ToArray());
        ProjectStore.AtomicWrite(Path.Combine(directory, IndexFile), stream => JsonSerializer.Serialize(stream, index, options));
        var ids = kept.Select(i => i.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 32 && Guid.TryParseExact(name, "N", out _) && !ids.Contains(name))
                try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

public static partial class ProjectStore
{
    // A 내 점경 shown in its own colours is its own original; a restyled one carries the original beside it.
    static Raster ReadEntourageSource(ZipArchive zip, int index, Raster pixels)
    {
        var entry = zip.GetEntry($"entourage/{index}.png");
        if (entry == null) return pixels;
        if (entry.Length > MaterialTextures.MaxEncodedBytes) throw new InvalidDataException("내 점경 원본 이미지가 너무 큽니다.");
        using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); buffer.Position = 0;
        var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("내 점경 원본 이미지가 없습니다.");
        var frame = decoder.Frames[0];
        if (Math.Max(frame.PixelWidth, frame.PixelHeight) > EntourageStoreLimits.MaxSide || (long)frame.PixelWidth * frame.PixelHeight > EntourageStoreLimits.MaxPixels)
            throw new InvalidDataException("내 점경 원본 이미지가 너무 큽니다.");
        return Raster.FromBitmap(frame);
    }
}
