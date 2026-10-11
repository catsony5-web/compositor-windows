using System.Collections.Concurrent;

namespace Compositor.Windows;

/// <summary>
/// A built-in entourage item. Ids are stable (saved in documents and used by the AI connection);
/// names are Korean source text shown through Loc. DefaultMeters is the real size it is drawn at
/// by default (height in elevation, longer side or canopy diameter in plan); Variants are seeded
/// shape variations of the same item. ShadowHeight is the real height a plan symbol stands for
/// (in metres per metre of its size) when it casts a plan shadow.
/// </summary>
public sealed record EntourageItem(string Id, string Name, EntourageCategory Category, EntourageView View, double DefaultMeters,
    EntourageFill DefaultFill, int Variants, string Keywords, double ShadowHeight = 0)
{
    internal Func<int, EntourageArt> Draw { get; init; } = _ => throw new InvalidOperationException();
}

/// <summary>
/// The built-in entourage set: original line art drawn in code (people, trees and plants,
/// vehicles, street furniture), each in elevation/section or plan view, with a few seeded
/// variations. Drawings are deterministic and cached per item and variant.
/// </summary>
public static partial class EntourageLibrary
{
    static readonly ConcurrentDictionary<(string, int), EntourageArt> cache = new();
    static readonly Lazy<EntourageItem[]> items = new(CreateItems);

    public static IReadOnlyList<EntourageItem> All => items.Value;
    public static EntourageItem? Find(string? id) => id == null ? null : items.Value.FirstOrDefault(i => i.Id == id);

    public static string CategoryName(EntourageCategory category) => category switch
    {
        EntourageCategory.People => "사람", EntourageCategory.Plants => "나무 · 식물", EntourageCategory.Vehicles => "탈것", _ => "소품"
    };
    public static string ViewName(EntourageView view) => view == EntourageView.Plan ? "평면" : "입면·단면";
    public static string CategoryKey(EntourageCategory category) => category switch
    {
        EntourageCategory.People => "people", EntourageCategory.Plants => "plants", EntourageCategory.Vehicles => "vehicles", _ => "props"
    };
    public static string ViewKey(EntourageView view) => view == EntourageView.Plan ? "plan" : "elevation";

    // Drawn once per item and variant; the geometry is frozen and shared.
    internal static EntourageArt Art(EntourageItem item, int variant)
    {
        int v = item.Variants <= 1 ? 0 : ((variant % item.Variants) + item.Variants) % item.Variants;
        return cache.GetOrAdd((item.Id, v), _ => item.Draw(v));
    }

    /// <summary>Items whose name, keywords, category or view contain the query (Korean or the display language).</summary>
    public static bool Matches(EntourageItem item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        bool Has(string text) => text.Contains(q, StringComparison.OrdinalIgnoreCase);
        return Has(item.Name) || Has(Loc.T(item.Name)) || Has(item.Keywords) || Has(item.Id) || Has(CategoryName(item.Category)) || Has(Loc.T(CategoryName(item.Category)))
            || Has(ViewName(item.View)) || Has(Loc.T(ViewName(item.View)));
    }

    static EntourageItem[] CreateItems()
    {
        var list = new List<EntourageItem>();
        AddPeople(list); AddPlants(list); AddVehicles(list); AddProps(list);
        return [.. list];
    }

    // Search words: Korean synonyms one by one, then English ones.
    static string K(params string[] words) => string.Join(" ", words);

    static EntourageItem Item(string id, string name, EntourageCategory category, EntourageView view, double meters, EntourageFill fill, int variants, string keywords,
        Action<EntourageSketch, EntourageRandom, int> draw, double shadowHeight = 0) => new(id, name, category, view, meters, fill, variants, keywords, shadowHeight)
    {
        Draw = variant =>
        {
            var sketch = new EntourageSketch { Ground = view == EntourageView.Elevation }; draw(sketch, new EntourageRandom(id, variant), variant);
            var art = sketch.Finish();
            return view == EntourageView.Plan ? art.Centered() : art;
        }
    };
}
