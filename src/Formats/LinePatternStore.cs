using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

// The user's line pattern library (내 패턴), per user in %LOCALAPPDATA%\Morupixel\Patterns: one
// coverage PNG per pattern named by its ID and patterns.json with the names in library order.
// Never part of the repository; documents carry their own copy of every pattern they use.
public static class LinePatternStore
{
    public const int MaxPatterns = 64, MaxNameLength = 80;
    const long MaxIndexBytes = 256 * 1024, MaxPngBytes = 16L * 1024 * 1024;
    const string IndexFile = "patterns.json";
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "Patterns");

    public sealed record Entry(Guid Id, string Name);
    public sealed record Index(int Version, Entry[]? Patterns);

    static string PngPath(string directory, Guid id) => Path.Combine(directory, id.ToString("N") + ".png");

    // Names are trimmed and limited; control characters are removed.
    public static string CleanName(string? name)
    {
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "내 패턴" : clean;
    }

    // Patterns in library order. A missing, oversized or damaged entry is skipped, never fatal.
    public static IReadOnlyList<MaterialAsset> Load(string directory)
    {
        var result = new List<MaterialAsset>();
        try
        {
            var index = new FileInfo(Path.Combine(directory, IndexFile));
            if (!index.Exists || index.Length > MaxIndexBytes) return result;
            var stored = JsonSerializer.Deserialize<Index>(File.ReadAllText(index.FullName), options);
            if (stored is not { Version: 1, Patterns: { } entries }) return result;
            var seen = new HashSet<Guid>();
            foreach (var entry in entries)
            {
                if (result.Count >= MaxPatterns) break;
                if (entry == null || entry.Id == Guid.Empty || HatchPatterns.TryGet(entry.Id, out _) || !seen.Add(entry.Id)) continue;
                try
                {
                    var file = new FileInfo(PngPath(directory, entry.Id));
                    if (!file.Exists || file.Length > MaxPngBytes) continue;
                    using var stream = file.OpenRead();
                    var pixels = MaterialTextures.Read(stream);
                    if (pixels.Width > LinePatterns.MaxSide || pixels.Height > LinePatterns.MaxSide) continue;
                    result.Add(LinePatterns.Create(CleanName(entry.Name), pixels, entry.Id));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or FormatException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return result;
    }

    // Writes the library: new tiles as PNG, the index atomically, and removes tiles no longer listed.
    public static void Save(IReadOnlyList<MaterialAsset> patterns, string directory)
    {
        var kept = patterns.Where(LinePatterns.IsCustom).GroupBy(p => p.Id).Select(g => g.First()).Take(MaxPatterns).ToArray();
        Directory.CreateDirectory(directory);
        foreach (var pattern in kept)
        {
            string path = PngPath(directory, pattern.Id);
            if (!File.Exists(path)) ProjectStore.AtomicWrite(path, stream => pattern.Pixels.WritePng(stream));
        }
        var index = new Index(1, kept.Select(p => new Entry(p.Id, CleanName(p.Name))).ToArray());
        ProjectStore.AtomicWrite(Path.Combine(directory, IndexFile), stream => JsonSerializer.Serialize(stream, index, options));
        var ids = kept.Select(p => p.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 32 && Guid.TryParseExact(name, "N", out _) && !ids.Contains(name))
                try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
