using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    JsonObject AutomationMaterialQuery(string command, JsonObject args)
    {
        var tab = AutomationTab(args); var document = tab.Document;
        if (args.ContainsKey("expectedRevision") && document.Revision != Guid.Parse(AString(args, "expectedRevision")))
            throw new AutomationFault("stale_revision", "문서가 변경되었습니다. 목록을 다시 조회하세요.");
        IEnumerable<JsonObject> items = command == "query_materials" ? MaterialEditing.Assets(document).Select(AutomationMaterials.Asset)
            : document.MaterialRegions.Select(AutomationMaterials.Region);
        if (args.ContainsKey("nameContains")) items = items.Where(item => AString(item, "name").Contains(AString(args, "nameContains"), StringComparison.OrdinalIgnoreCase));
        var all = items.ToArray(); int offset = (int)ANumber(args, "offset"), limit = (int)ANumber(args, "limit", 20);
        var page = all.Skip(offset).Take(limit).ToArray();
        return new JsonObject
        {
            ["documentId"] = tab.Id.ToString(), ["revision"] = document.Revision.ToString(), ["totalMatches"] = all.Length,
            ["offset"] = offset, ["limit"] = limit, ["nextOffset"] = offset + page.Length < all.Length ? offset + page.Length : null,
            [command == "query_materials" ? "materials" : "regions"] = new JsonArray(page.Cast<JsonNode?>().ToArray())
        };
    }
    async Task<JsonObject> AutomationRegisterMaterialAsync(string command, JsonObject args, CancellationToken token)
    {
        var tab = AutomationTab(args, true); var before = doc.Snapshot(); var candidate = doc.Snapshot();
        var initialSelection = selection;
        JsonObject added;
        if (command == "register_material")
        {
            if (MaterialEditing.Assets(candidate).Count >= MaterialEditing.MaxAssets) throw new AutomationFault("capacity_exceeded", "재료 라이브러리가 가득 찼습니다.");
            string path = AutomationPath(args);
            var pixels = await CompatibilityImport.OnSta(() => MaterialTextures.Load(path), token);
            var asset = new MaterialAsset(Guid.NewGuid(), AString(args, "name"), pixels, args.ContainsKey("source") ? AString(args, "source") : "", ABool(args, "tileable"));
            candidate.Materials.Add(asset); added = AutomationMaterials.Asset(asset);
        }
        else
        {
            if (candidate.MaterialRegions.Count >= MaterialEditing.MaxRegions) throw new AutomationFault("capacity_exceeded", "영역은 최대 128개를 보관합니다.");
            string source = AString(args, "source");
            var region = await CompatibilityImport.OnSta(() =>
            {
                token.ThrowIfCancellationRequested();
                Geometry geometry;
                Guid? layerId = null;
                if (source == "selection")
                {
                    if (initialSelection == null) throw new AutomationFault("selection_required", "먼저 적용할 영역을 선택하세요.");
                    geometry = SelectionContours.Create(initialSelection, token);
                }
                else if (source == "closed_layer")
                {
                    layerId = Guid.Parse(AString(args, "layerId")); geometry = MaterialEditing.ClosedLayer(candidate, layerId.Value);
                }
                else
                {
                    var contours = new List<Point[]> { AutomationCatalog.MaterialPoints(args["points"]!.AsArray()) };
                    if (args["holes"] is JsonArray holes) contours.AddRange(holes.Select(h => AutomationCatalog.MaterialPoints(h!.AsArray())));
                    geometry = MaterialEditing.Polygon(contours);
                }
                return MaterialEditing.Region(candidate, AString(args, "name"), geometry, source, layerId);
            }, token);
            candidate.MaterialRegions.Add(region); added = AutomationMaterials.Region(region);
        }
        candidate.Validate(); RequireAutomationIdle(token); _ = AutomationTab(args, true);
        if (command == "define_region" && AString(args, "source") == "selection" && !ReferenceEquals(initialSelection, selection))
            throw new AutomationFault("workspace_changed", "영역을 가져오는 동안 선택이 변경되었습니다.");
        CommitAutomationCandidate(command == "register_material" ? "AI · 재료 등록" : "AI · 적용 영역 등록",
            before, candidate, null, preserveSelection: true);
        added["documentId"] = tab.Id.ToString(); added["revision"] = doc.Revision.ToString(); return added;
    }
}
