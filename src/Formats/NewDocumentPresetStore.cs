using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

/// <summary>One saved new-document size. Millimeter sizes keep their DPI so they reopen at the same pixels.</summary>
public sealed record SavedDocumentSize(string Name, double Width, double Height, bool Millimeters, double Dpi, int Background = 0);

// Per-user recent sizes and custom presets for the new document dialog.
// Stored in %LOCALAPPDATA%\Morupixel, never in the repository or a release package.
public static class NewDocumentPresetStore
{
    public const int RecentLimit = 6, CustomLimit = 12;
    const int MaxStoreBytes = 64 * 1024;

    public sealed record Snapshot(IReadOnlyList<SavedDocumentSize> Recent, IReadOnlyList<SavedDocumentSize> Custom);
    sealed record Stored(SavedDocumentSize[]? Recent, SavedDocumentSize[]? Custom);

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "new-document.json");

    public static Snapshot Load(string? store = null)
    {
        try
        {
            var file = new FileInfo(store ?? DefaultStorePath);
            if (!file.Exists || file.Length > MaxStoreBytes) return new([], []);
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file.FullName));
            return new(Clean(stored?.Recent, RecentLimit), Clean(stored?.Custom, CustomLimit));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new([], []); }
    }

    /// <summary>Records a created size, newest first; the same size and unit replaces its older entry.</summary>
    public static Snapshot AddRecent(SavedDocumentSize size, string? store = null)
    {
        var current = Load(store);
        var next = current.Recent.Where(r => !SameSize(r, size)).Prepend(size).Take(RecentLimit).ToArray();
        return Save(new(next, current.Custom), store);
    }

    public static Snapshot AddCustom(SavedDocumentSize size, string? store = null)
    {
        var current = Load(store);
        var next = current.Custom.Where(c => !string.Equals(c.Name, size.Name, StringComparison.CurrentCultureIgnoreCase)).Prepend(size).Take(CustomLimit).ToArray();
        return Save(new(current.Recent, next), store);
    }

    public static Snapshot RemoveCustom(string name, string? store = null)
    {
        var current = Load(store);
        return Save(new(current.Recent, current.Custom.Where(c => !string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToArray()), store);
    }

    static bool SameSize(SavedDocumentSize a, SavedDocumentSize b) => a.Width == b.Width && a.Height == b.Height && a.Millimeters == b.Millimeters && a.Dpi == b.Dpi;

    static SavedDocumentSize[] Clean(SavedDocumentSize[]? entries, int limit) => (entries ?? [])
        .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name) && e.Name.Length <= 80 && double.IsFinite(e.Width) && double.IsFinite(e.Height) && double.IsFinite(e.Dpi)
            && e.Width > 0 && e.Height > 0 && e.Width <= 100_000 && e.Height <= 100_000 && e.Dpi >= 1 && e.Dpi <= 9600 && e.Background is >= 0 and <= 2)
        .Take(limit).ToArray();

    static Snapshot Save(Snapshot snapshot, string? store)
    {
        try
        {
            string path = store ?? DefaultStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(snapshot.Recent.ToArray(), snapshot.Custom.ToArray())));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return snapshot;
    }
}
