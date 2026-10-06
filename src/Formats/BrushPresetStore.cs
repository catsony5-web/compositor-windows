using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

// The user's saved brushes (내 프리셋), per user in %LOCALAPPDATA%\Morupixel\brush-presets.json, in
// list order. Never part of the repository or a document. Image shapes are referred to by their ID;
// the shapes themselves stay in BrushTipStore.
public static class BrushPresetStore
{
    public const int MaxPresets = 64;
    const long MaxStoreBytes = 256 * 1024;
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "brush-presets.json");

    sealed record Stored(int Version, BrushPreset[] Presets);

    // Presets in list order. A missing, oversized or damaged file reads as empty; a damaged entry is skipped.
    public static IReadOnlyList<BrushPreset> Load(string path)
    {
        var result = new List<BrushPreset>();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxStoreBytes) return result;
            using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number != 1 || !root.TryGetProperty("Presets", out var presets) || presets.ValueKind != JsonValueKind.Array) return result;
            var seen = new HashSet<Guid>();
            foreach (var entry in presets.EnumerateArray())
            {
                if (result.Count >= MaxPresets) break;
                try
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (BrushPreset.Normalize(entry.Deserialize<BrushPreset>(options)) is { } preset && seen.Add(preset.Id)) result.Add(preset);
                }
                catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { }
        return result;
    }

    // Writes the list atomically. Invalid or duplicate entries are dropped and at most MaxPresets are kept.
    public static void Save(IReadOnlyList<BrushPreset> presets, string path)
    {
        var seen = new HashSet<Guid>();
        var kept = presets.Select(BrushPreset.Normalize).OfType<BrushPreset>().Where(p => seen.Add(p.Id)).Take(MaxPresets).ToArray();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        ProjectStore.AtomicWrite(full, stream => JsonSerializer.Serialize(stream, new Stored(1, kept), options));
    }
}
