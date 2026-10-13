using System.Collections.Concurrent;

namespace Compositor.Windows;

/// <summary>
/// A built-in entourage item. Ids are stable (saved in documents and used by the AI connection);
/// names are Korean source text shown through Loc. DefaultMeters is the real size it is drawn at
/// by default (height in elevation and axonometric, longer side or canopy diameter in plan); Variants
/// are seeded shape variations of the same item. ShadowHeight is the real height a plan symbol stands
/// for (in metres per metre of its size) when it casts a plan shadow. Axonometric items are drawn in
/// every projection (Projected); Draw gives their isometric drawing.
/// </summary>
public sealed record EntourageItem(string Id, string Name, EntourageCategory Category, EntourageView View, double DefaultMeters,
    EntourageFill DefaultFill, int Variants, string Keywords, double ShadowHeight = 0)
{
    internal Func<int, EntourageArt> Draw { get; init; } = _ => throw new InvalidOperationException();
    internal Func<int, EntourageProjection, EntourageArt>? Projected { get; init; }
    // Axonometric items: the width of their ground footprint per metre of their size (spacing a scatter).
    public double Footprint { get; init; }
}

/// <summary>
/// The built-in entourage set: original line art drawn in code (people, trees and plants,
/// vehicles, street furniture), each in elevation/section or plan view, with a few seeded
/// variations. Drawings are deterministic and cached per item and variant.
/// </summary>
public static partial class EntourageLibrary
{
    static readonly ConcurrentDictionary<(string, int, EntourageProjection), EntourageArt> cache = new();
    static readonly Lazy<EntourageItem[]> items = new(CreateItems);

    public static IReadOnlyList<EntourageItem> All => items.Value;
    public static EntourageItem? Find(string? id) => id == null ? null : items.Value.FirstOrDefault(i => i.Id == id);

    public static string CategoryName(EntourageCategory category) => category switch
    {
        EntourageCategory.People => "사람", EntourageCategory.Plants => "나무 · 식물", EntourageCategory.Vehicles => "탈것", _ => "소품"
    };
    public static string ViewName(EntourageView view) => view switch { EntourageView.Plan => "평면", EntourageView.Axonometric => "액소노메트릭", _ => "입면·단면" };
    public static string ProjectionName(EntourageProjection projection) => projection switch
    {
        EntourageProjection.Dimetric => "이등각 2:1", EntourageProjection.Military => "평면 사투상 45°", _ => "등각 30°"
    };
    public static string ProjectionKey(EntourageProjection projection) => projection switch
    {
        EntourageProjection.Dimetric => "dimetric", EntourageProjection.Military => "military", _ => "isometric"
    };
    public static EntourageProjection? ParseProjection(string? key) => key switch
    {
        "isometric" => EntourageProjection.Isometric, "dimetric" => EntourageProjection.Dimetric, "military" => EntourageProjection.Military, _ => null
    };
    public static string CategoryKey(EntourageCategory category) => category switch
    {
        EntourageCategory.People => "people", EntourageCategory.Plants => "plants", EntourageCategory.Vehicles => "vehicles", _ => "props"
    };
    public static string ViewKey(EntourageView view) => view switch { EntourageView.Plan => "plan", EntourageView.Axonometric => "axonometric", _ => "elevation" };

    // Drawn once per item, variant and (axonometric items) projection; the geometry is frozen and shared.
    internal static EntourageArt Art(EntourageItem item, int variant, EntourageProjection projection = EntourageProjection.Isometric)
    {
        int v = item.Variants <= 1 ? 0 : ((variant % item.Variants) + item.Variants) % item.Variants;
        var p = item.Projected == null ? EntourageProjection.Isometric : projection;
        return cache.GetOrAdd((item.Id, v, p), _ => item.Projected is { } draw ? draw(v, p) : item.Draw(v));
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
        AddPeople(list); AddPlanPeople(list); AddPlants(list); AddPlanPlants(list); AddVehicles(list); AddPlanVehicles(list); AddProps(list);
        AddAxonometric(list);
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

    // An axonometric item: modelled in centimetres around its ground point and drawn in each projection.
    static EntourageItem Axo(string id, string name, EntourageCategory category, double meters, int variants, string keywords, double footprint,
        Action<AxoSketch, EntourageRandom, int> draw)
    {
        EntourageArt Projected(int variant, EntourageProjection projection)
        {
            var sketch = new EntourageSketch();
            draw(new AxoSketch(sketch, AxoProjection.Of(projection)), new EntourageRandom(id, variant), variant);
            if (sketch.ModelHeight <= 0) throw new InvalidOperationException($"{id} has no height.");
            return sketch.Finish();
        }
        return new(id, name, category, EntourageView.Axonometric, meters, EntourageFill.White, variants, keywords)
        {
            Projected = Projected, Draw = v => Projected(v, EntourageProjection.Isometric), Footprint = footprint
        };
    }
}
