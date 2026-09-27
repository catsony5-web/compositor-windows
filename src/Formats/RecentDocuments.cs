using System.IO;
using System.Text.Json;

namespace Compositor.Windows;

// Per-user list of recently opened or saved documents for the start screen.
// It lives in %LOCALAPPDATA%\Morupixel, never in the repository or a release package.
public static class RecentDocuments
{
    public const int Limit = 8;
    const int MaxStoreBytes = 64 * 1024;

    public static string DefaultStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morupixel", "recent.json");

    public static IReadOnlyList<string> Load(string? store = null)
    {
        try
        {
            var file = new FileInfo(store ?? DefaultStorePath);
            if (!file.Exists || file.Length > MaxStoreBytes) return [];
            var entries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file.FullName)) ?? [];
            return Normalize(entries);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    public static IReadOnlyList<string> Add(string document, string? store = null)
    {
        var current = Load(store);
        string full = Path.GetFullPath(document);
        var next = Normalize(current.Where(path => !SamePath(path, full)).Prepend(full));
        Save(next, store); return next;
    }

    public static IReadOnlyList<string> Remove(string document, string? store = null)
    {
        var next = Load(store).Where(path => !SamePath(path, document)).ToArray();
        Save(next, store); return next;
    }

    static IReadOnlyList<string> Normalize(IEnumerable<string?> entries) => entries
        .Where(path => !string.IsNullOrWhiteSpace(path) && path!.Length < 1024 && Path.IsPathFullyQualified(path))
        .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).Take(Limit).ToArray();

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void Save(IReadOnlyList<string> entries, string? store)
    {
        try
        {
            string path = store ?? DefaultStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
