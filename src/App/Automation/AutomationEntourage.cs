using System.Text.Json.Nodes;

namespace Compositor.Windows;

// query_entourage / place_entourage: the 점경 library for AI clients and placing (or restyling)
// entourage with the same renderer as the palette. Heights are real metres; positions are the item's
// anchor in document pixels: the ground point under an elevation item, the centre of a plan symbol.
public static partial class AutomationCatalog
{
    public const string EntourageIdPattern = "^([a-z]+(\\.[a-z0-9]+(-[a-z0-9]+)*)+|custom:[0-9a-f]{32})$";
    internal static readonly string[] EntourageFills = ["none", "white", "gray", "solid"];
    static readonly string[] EntouragePlaceOnly = ["itemId", "x", "y", "count", "spread", "seed", "name"];

    static void AddEntourageCommands(Action<string, string, bool, Dictionary<string, Field>, string[]> add, Func<(string Key, Field Value)[], Dictionary<string, Field>> mutation, Func<string[], string[]> required)
    {
        add("query_entourage", "List the entourage library: built-in original line-art people, trees and plants, vehicles and street furniture (itemId, Korean name, category, view elevation/plan, default height in metres, default fill, shape variants), then the user's own items (My entourage on this PC and those carried by the document, itemId custom:<id>). With documentId (default: the active document) also the document's drawing scale for entourage (pixelsPerMeter and where it comes from) and the entourage already placed. No document is needed for the library.", true,
            Fields(("documentId", Id), ("category", Choice("people", "plants", "vehicles", "props", "custom")), ("view", Choice("elevation", "plan")),
                ("nameContains", new("string", "Case-insensitive literal substring of itemId, Korean name, display name or keywords.", MaxLength: 256))), []);
        add("place_entourage", "Place an entourage item (itemId from query_entourage) with its anchor at x, y in document pixels: the ground point under an elevation/section item (feet, trunk base, wheels), or the centre of a plan symbol. Built-in items are retained vectors (crisp at any zoom, exported as PDF/.ai vectors); user items are image layers. height is the real size in metres (elevation: height; plan: the longer side or canopy diameter) at pixelsPerMeter (default: the document's entourage scale, otherwise 1:100 at the document DPI). count 2-24 scatters copies naturally (slightly different sizes, flips and shape variants, plan items turned) along a ground line `spread` pixels wide centred on x (elevation) or within a disc of radius `spread` (plan), deterministic for `seed`, in one new group. With layerId instead of itemId/x/y, restyles a placed entourage layer in place (omitted fields keep their values; height or pixelsPerMeter drop a size set with the transform handles). One undo step; can be included in apply_batch. Returns layerId (the item, or the group when scattering) and layerIds.", false,
            mutation([("itemId", new("string", "Item from query_entourage: a built-in id (person.walking, tree.round, car.plan, …) or custom:<32 hex>.", MaxLength: 80, TextPattern: EntourageIdPattern)),
                ("layerId", Id with { Description = "A placed entourage layer to restyle instead of placing a new item." }),
                ("x", Coordinate with { Description = "Anchor x in document pixels (the ground point of elevation items, the centre of plan items)." }),
                ("y", Coordinate with { Description = "Anchor y in document pixels; for elevation items the ground line the item stands on." }),
                ("height", Number(EntourageSpec.MinMeters, EntourageSpec.MaxMeters, "Real size in metres: height (elevation) or longer side/canopy diameter (plan). Defaults to the item's typical size (a person 1.7 m).")),
                ("pixelsPerMeter", Number(EntourageSpec.MinPixelsPerMeter, EntourageSpec.MaxPixelsPerMeter, "Drawing scale: pixels for one metre. Defaults to the document's entourage scale.")),
                ("fill", new("string", "none (lines only), white or gray (silhouette filled under the lines), solid (silhouette in the line colour). User photo items read them as original, faded, greyscale, silhouette.", Choices: EntourageFills)),
                ("lineColor", new("string", "Line colour #RRGGBB or #AARRGGBB (alpha 01-FF).", MaxLength: 9, Ink: true)),
                ("lineWeight", Number(EntourageSpec.MinLineWeight, EntourageSpec.MaxLineWeight, "Pen multiplier; 1 is the weight the scale suggests, shared by all items at that scale.")),
                ("flip", Bool("Mirror left to right (new items), or set the mirroring of layerId.")),
                ("variant", Integer(0, EntourageSpec.MaxVariant, "Shape variant of the same item (query_entourage variants); wraps around.")),
                ("count", Integer(1, 24, "Number of copies; 2-24 scatters them naturally into one group. Defaults to 1.")),
                ("spread", Number(1, 100_000, "Scatter width of the ground line (elevation) or radius (plan) in document pixels. Defaults to a spacing from the item's size.")),
                ("seed", Integer(0, int.MaxValue, "Scatter arrangement number; the same seed gives the same arrangement. Defaults to 1.")),
                ("name", Name with { Description = "Layer (or scatter group) name; defaults to the item's Korean name." })]), required([]));
    }

    static void ValidateEntourageArguments(JsonObject arguments)
    {
        bool update = arguments.ContainsKey("layerId");
        if (update && EntouragePlaceOnly.Any(arguments.ContainsKey))
            throw new ArgumentException("layerId restyles a placed item; itemId, x, y, count, spread, seed and name belong to placing a new one.");
        if (!update && !(arguments.ContainsKey("itemId") && arguments.ContainsKey("x") && arguments.ContainsKey("y")))
            throw new ArgumentException("Give itemId, x and y to place an item, or layerId to restyle one.");
        if (arguments["lineColor"] is { } color && color.GetValue<string>() == "default") throw new ArgumentException("lineColor must be #RRGGBB or #AARRGGBB.");
        if (!update && NumberValue(arguments, "count", 1) <= 1 && (arguments.ContainsKey("spread") || arguments.ContainsKey("seed")))
            throw new ArgumentException("spread and seed apply when count is 2 or more.");
    }

    public static string FillKey(EntourageFill fill) => EntourageFills[(int)fill];
    public static EntourageFill ParseFill(string key) => (EntourageFill)Array.IndexOf(EntourageFills, key);

    public static JsonObject EntourageItemJson(EntourageItem item) => new()
    {
        ["itemId"] = item.Id, ["name"] = item.Name, ["displayName"] = Loc.T(item.Name), ["kind"] = "builtin",
        ["category"] = EntourageLibrary.CategoryKey(item.Category), ["view"] = EntourageLibrary.ViewKey(item.View),
        ["defaultHeight"] = item.DefaultMeters, ["defaultFill"] = FillKey(item.DefaultFill), ["variants"] = Math.Max(1, item.Variants),
        ["keywords"] = item.Keywords
    };

    public static JsonObject EntouragePlacedJson(Layer layer, Document document)
    {
        var spec = layer.Entourage!; var anchor = EntourageRenderer.Anchor(layer);
        var lookup = document.Layers.ToDictionary(l => l.Id);
        for (var parent = layer.ParentId; parent is { } id && lookup.TryGetValue(id, out var group); parent = group.ParentId) anchor = group.Document(anchor);
        return new JsonObject
        {
            ["layerId"] = layer.Id.ToString(), ["name"] = layer.Name, ["itemId"] = spec.ItemId, ["kind"] = spec.IsCustom ? "custom" : "builtin",
            ["view"] = EntourageLibrary.ViewKey(spec.View), ["height"] = spec.Meters, ["pixelsPerMeter"] = spec.PixelsPerMeter,
            ["fill"] = FillKey(spec.Fill), ["lineColor"] = $"#{spec.LineArgb:X8}", ["lineWeight"] = spec.LineWeight, ["variant"] = spec.Variant,
            ["flip"] = layer.FlipX, ["anchor"] = new JsonObject { ["x"] = Math.Round(anchor.X, 2), ["y"] = Math.Round(anchor.Y, 2), ["space"] = "document" },
            ["sizedByHandles"] = Math.Abs(layer.ScaleX * layer.ScaleY - 1) > 1e-9 || !spec.IsCustom && Math.Abs(layer.Scale - 1) > 1e-9
        };
    }

    static JsonObject EntourageCapabilities() => new()
    {
        ["query"] = "query_entourage", ["place"] = "place_entourage", ["count"] = EntourageLibrary.All.Count,
        ["categories"] = Strings(["people", "plants", "vehicles", "props", "custom"]), ["views"] = Strings(["elevation", "plan"]), ["fills"] = Strings(EntourageFills),
        ["anchor"] = "Elevation items stand on their anchor (ground point); plan items are centred on it. Positions are document pixels.",
        ["scale"] = "height in metres at pixelsPerMeter; default is the document's entourage scale, otherwise 1:100 at the document DPI.",
        ["vector"] = "Built-in items are retained vector layers (PDF/.ai keep them as vectors); user items are image layers carrying their original.",
        ["scatter"] = "count 2-24 places copies with seeded size, flip and variant changes in one group.",
        ["restyle"] = "place_entourage with layerId", ["batch"] = true, ["undoSteps"] = 1
    };
}
