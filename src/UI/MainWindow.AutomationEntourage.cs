using System.Text.Json.Nodes;
using System.Windows;

namespace Compositor.Windows;

// query_entourage and place_entourage over the local AI connection, on the same library, renderer
// and scatter as the palette. place_entourage edits the automation candidate, so it commits as one
// undo step (and can be a step of apply_batch).
public sealed partial class MainWindow
{
    JsonObject AutomationEntourageQuery(JsonObject args)
    {
        string? category = args["category"]?.GetValue<string>(), view = args["view"]?.GetValue<string>(), query = args["nameContains"]?.GetValue<string>();
        bool Fits(string itemCategory, string itemView) => (category == null || category == itemCategory) && (view == null || view == itemView);
        var items = category == "custom" ? [] : EntourageLibrary.All.Where(i => Fits(EntourageLibrary.CategoryKey(i.Category), EntourageLibrary.ViewKey(i.View)) && EntourageLibrary.Matches(i, query))
            .Select(i => (JsonNode?)AutomationCatalog.EntourageItemJson(i)).ToArray();
        WorkspaceTab? tab = args.ContainsKey("documentId") ? AutomationTab(args) : activeTab >= 0 ? tabs[activeTab] : null;
        var library = CustomEntourageLibrary();
        var custom = new List<JsonNode?>();
        var carried = tab?.Document.Layers.Where(l => l.Entourage is { IsCustom: true }).ToArray() ?? [];
        foreach (var item in library.Concat(CarriedCustomEntourage(tab?.Document).Where(c => library.All(i => i.Id != c.Id))))
        {
            if (category != null && category != "custom" || view != null && view != EntourageLibrary.ViewKey(item.View)) continue;
            if (!string.IsNullOrWhiteSpace(query) && !item.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) && !item.ItemId.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            custom.Add(new JsonObject
            {
                ["itemId"] = item.ItemId, ["name"] = item.Name, ["kind"] = "custom", ["category"] = EntourageLibrary.CategoryKey(item.Category), ["view"] = EntourageLibrary.ViewKey(item.View),
                ["defaultHeight"] = item.Meters, ["lineDrawing"] = item.LineDrawing, ["width"] = item.Pixels.Width, ["height"] = item.Pixels.Height,
                ["inLibrary"] = library.Any(i => i.Id == item.Id), ["inDocument"] = carried.Any(l => l.Entourage!.ItemId == item.ItemId)
            });
        }
        var result = new JsonObject
        {
            ["items"] = new JsonArray(items), ["count"] = items.Length, ["custom"] = new JsonArray(custom.ToArray()), ["customCount"] = custom.Count,
            ["usage"] = "place_entourage with itemId and x, y (ground point for elevation, centre for plan) and height in metres; layerId restyles a placed item."
        };
        if (tab != null)
        {
            var d = tab.Document;
            double ppm = AutomationEntourageScale(tab, d);
            result["documentId"] = tab.Id.ToString(); result["revision"] = d.Revision.ToString();
            result["scale"] = new JsonObject
            {
                ["pixelsPerMeter"] = ppm, ["personPixels"] = Math.Round(1.7 * ppm, 1),
                ["basis"] = entourageScaleByTab.ContainsKey(tab.Id) ? "user" : EntourageRenderer.DocumentPixelsPerMeter(d) != null ? "document_entourage" : "default_1_to_100_at_dpi"
            };
            var placed = d.Layers.Where(l => l.Entourage != null).ToArray();
            result["placed"] = new JsonArray(placed.Take(200).Select(l => (JsonNode?)AutomationCatalog.EntouragePlacedJson(l, d)).ToArray());
            result["placedCount"] = placed.Length; result["placedTruncated"] = placed.Length > 200;
        }
        return result;
    }

    double AutomationEntourageScale(WorkspaceTab tab, Document d) => entourageScaleByTab.TryGetValue(tab.Id, out var chosen) ? chosen
        : EntourageRenderer.DocumentPixelsPerMeter(d) ?? EntourageRenderer.DefaultPixelsPerMeter(d);

    // The user's items a document carries (they may come from another PC).
    static IEnumerable<EntourageCustomItem> CarriedCustomEntourage(Document? document)
    {
        if (document == null) yield break;
        var seen = new HashSet<string>();
        foreach (var layer in document.Layers)
            if (layer.Entourage is { IsCustom: true, Source: { } source } spec && seen.Add(spec.ItemId) && Guid.TryParseExact(spec.ItemId[EntourageSpec.CustomPrefix.Length..], "N", out var id))
                yield return new EntourageCustomItem(id, layer.Name, EntourageCategory.Props, spec.View, spec.Meters, spec.LineDrawing, source);
    }

    Guid AutomationPlaceEntourage(Document candidate, JsonObject args)
    {
        EntourageSpec Styled(EntourageSpec spec) => spec with
        {
            Meters = ANumber(args, "height", spec.Meters), PixelsPerMeter = ANumber(args, "pixelsPerMeter", spec.PixelsPerMeter),
            Fill = args.ContainsKey("fill") ? AutomationCatalog.ParseFill(AString(args, "fill")) : spec.Fill,
            LineArgb = args.ContainsKey("lineColor") ? AutomationMaterials.ParseInk(AString(args, "lineColor")) : spec.LineArgb,
            LineWeight = ANumber(args, "lineWeight", spec.LineWeight),
            Variant = args.ContainsKey("variant") ? (int)ANumber(args, "variant") : spec.Variant
        };
        EntourageSpec Checked(EntourageSpec spec)
        {
            if (!spec.IsCustom && EntourageLibrary.Find(spec.ItemId) is { } item) spec = spec with { Variant = item.Variants <= 1 ? 0 : spec.Variant % item.Variants };
            try { spec.Validate(); } catch (System.IO.InvalidDataException e) { throw new AutomationFault("invalid_arguments", e.Message); }
            return spec;
        }
        if (args.ContainsKey("layerId"))
        {
            var id = Guid.Parse(AString(args, "layerId"));
            var layer = candidate.Layers.FirstOrDefault(l => l.Id == id) ?? throw new AutomationFault("layer_not_found", "레이어를 찾을 수 없습니다.");
            if (layer.Locked || Parents(candidate, layer).Any(l => l.Locked)) throw new AutomationFault("layer_locked", "레이어 또는 부모 그룹의 잠금을 먼저 해제하세요.");
            if (layer.Entourage is not { } current) throw new AutomationFault("wrong_layer_kind", "점경 레이어가 아닙니다. query_entourage의 placed 목록에서 layerId를 고르세요.");
            if (!current.IsCustom && EntourageLibrary.Find(current.ItemId) == null) throw new AutomationFault("entourage_not_found", "이 버전에 없는 점경이라 다시 그릴 수 없습니다.");
            var next = Checked(Styled(current));
            EntourageRenderer.Update(layer, next, args.ContainsKey("height") || args.ContainsKey("pixelsPerMeter"));
            if (args.ContainsKey("flip") && ABool(args, "flip") != layer.FlipX)
            {
                var stand = EntourageRenderer.Anchor(layer); layer.FlipX = !layer.FlipX;
                var moved = EntourageRenderer.Anchor(layer); layer.X += stand.X - moved.X; layer.Y += stand.Y - moved.Y;
            }
            automationStepDetails = new JsonObject { ["layerIds"] = new JsonArray(JsonValue.Create(layer.Id.ToString())), ["entourage"] = AutomationCatalog.EntouragePlacedJson(layer, candidate) };
            return layer.Id;
        }
        string itemId = AString(args, "itemId");
        EntourageChoice choice;
        if (EntourageLibrary.Find(itemId) is { } builtIn) choice = new EntourageChoice(builtIn, null);
        else if (CustomEntourageLibrary().Concat(CarriedCustomEntourage(candidate)).FirstOrDefault(i => i.ItemId == itemId) is { } custom) choice = new EntourageChoice(null, custom);
        else throw new AutomationFault("entourage_not_found", "점경 항목을 찾을 수 없습니다. query_entourage의 itemId를 사용하세요.");
        var tab = activeTab >= 0 ? tabs[activeTab] : null;
        double ppm = tab != null ? AutomationEntourageScale(tab, candidate) : EntourageRenderer.DefaultPixelsPerMeter(candidate);
        var spec = Checked(Styled(EntourageSpecFor(choice, ppm) with { LineArgb = EntourageSpec.DefaultLine, LineWeight = 1 }));
        var anchor = new Point(ANumber(args, "x"), ANumber(args, "y"));
        string name = args.ContainsKey("name") ? AString(args, "name") : choice.Name;
        int count = (int)ANumber(args, "count", 1);
        bool flip = ABool(args, "flip");
        if (count <= 1)
        {
            var layer = EntourageRenderer.Create(spec, anchor, name, flip);
            candidate.Add(layer);
            automationStepDetails = new JsonObject { ["layerIds"] = new JsonArray(JsonValue.Create(layer.Id.ToString())), ["entourage"] = AutomationCatalog.EntouragePlacedJson(layer, candidate) };
            return layer.Id;
        }
        Rect? area = null; Func<Point, bool>? inside = null;
        if (args.ContainsKey("spread"))
        {
            double spread = ANumber(args, "spread");
            if (spec.View == EntourageView.Elevation) area = new Rect(anchor.X - spread / 2, anchor.Y, spread, 0);
            else { area = new Rect(anchor.X - spread, anchor.Y - spread, spread * 2, spread * 2); inside = p => (p - anchor).Length <= spread; }
        }
        var placements = EntourageRenderer.Scatter(choice.Category, spec.View, spec.Meters, spec.PixelsPerMeter, EntourageFootprint(spec), choice.Variants, count, anchor, area, inside, (int)ANumber(args, "seed", 1));
        var group = DocumentFeatures.CreateGroup(candidate, $"{name} ×{placements.Count}");
        candidate.Add(group);
        var ids = new JsonArray();
        foreach (var p in placements)
        {
            var layer = EntourageRenderer.Create(spec with { Meters = p.Meters, Variant = p.Variant }, p.Anchor, name, p.Flip != flip, p.Rotation);
            layer.ParentId = group.Id; candidate.Add(layer); ids.Add(JsonValue.Create(layer.Id.ToString()));
        }
        automationStepDetails = new JsonObject { ["layerIds"] = ids, ["groupId"] = group.Id.ToString(), ["placedCount"] = placements.Count };
        return group.Id;
    }
}
