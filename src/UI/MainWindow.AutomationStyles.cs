using System.Text.Json.Nodes;

namespace Compositor.Windows;

// query_styles and apply_style over the local AI connection. apply_style runs the same engine as
// the gallery (DesignStyleEngine on an isolated STA) on the automation candidate, so it commits as
// one undo step through the shared automation path.
public sealed partial class MainWindow
{
    StyleOutcome? automationStyleOutcome;

    JsonObject AutomationStyleQuery(JsonObject args)
    {
        var result = new JsonObject { ["styles"] = AutomationCatalog.StylesJson(), ["count"] = DesignStyles.All.Count,
            ["usage"] = "apply_style with styleId and parameters (keys from this list); groupId re-applies a folder listed under folders; delete_layer on a groupId removes the style." };
        WorkspaceTab? tab = args.ContainsKey("documentId") ? AutomationTab(args) : activeTab >= 0 ? tabs[activeTab] : null;
        if (tab != null)
        {
            var d = tab.Document;
            result["documentId"] = tab.Id.ToString(); result["revision"] = d.Revision.ToString();
            result["documentKind"] = DesignStyleEngine.IsDrawing(d, null) ? "drawing" : "photo";
            result["folders"] = new JsonArray(d.Layers.Where(DesignStyles.IsStyleGroup).Select(l => (JsonNode?)AutomationCatalog.StyleFolder(l)).ToArray());
        }
        return result;
    }

    async Task<Guid> AutomationApplyStyleAsync(Document candidate, JsonObject args, CancellationToken token)
    {
        string styleId = AString(args, "styleId");
        var values = AutomationCatalog.StyleValues(styleId, args["parameters"] as JsonObject);
        Guid? groupId = null; Guid[]? targets = null;
        if (args.ContainsKey("groupId"))
        {
            var id = Guid.Parse(AString(args, "groupId"));
            var folder = candidate.Layers.FirstOrDefault(l => l.Id == id) ?? throw new AutomationFault("layer_not_found", "스타일 그룹을 찾을 수 없습니다.");
            if (!DesignStyles.IsStyleGroup(folder)) throw new AutomationFault("wrong_layer_kind", "디자인 스타일 그룹이 아닙니다. query_styles의 groupId를 사용하세요.");
            if (Parents(candidate, folder).Any(l => l.Locked) || folder.Locked) throw new AutomationFault("layer_locked", "스타일 그룹 또는 부모 그룹의 잠금을 먼저 해제하세요.");
            // Omitted parameters keep the folder's values, like the inspector.
            if (DesignStyles.Find(folder.Style!.StyleId)?.Id == styleId)
                foreach (var (key, value) in folder.Style.ValueMap) values.TryAdd(key, value);
            groupId = id;
        }
        if (args["targetLayerIds"] is JsonArray ids)
        {
            targets = ids.Select(n => Guid.Parse(n!.GetValue<string>())).ToArray();
            foreach (var id in targets)
            {
                var layer = candidate.Layers.FirstOrDefault(l => l.Id == id) ?? throw new AutomationFault("layer_not_found", "대상 레이어를 찾을 수 없습니다.");
                if (DesignStyles.GroupOf(candidate, layer) != null) throw new AutomationFault("invalid_arguments", "스타일 그룹과 그 안의 레이어는 대상으로 쓸 수 없습니다.");
            }
        }
        var services = designStyleServices;
        var outcome = await CompatibilityImport.OnSta(() => DesignStyleEngine.Apply(candidate, new StyleRequest(styleId, values, targets, groupId), services, token), token);
        automationStyleOutcome = outcome;
        return outcome.GroupId;
    }

    static void AutomationStyleResult(JsonObject result, StyleOutcome outcome, Document document)
    {
        result["groupId"] = outcome.GroupId.ToString(); result["styleId"] = outcome.StyleId; result["layerCount"] = outcome.LayerCount;
        if (document.Layers.FirstOrDefault(l => l.Id == outcome.GroupId) is { Style: not null } folder) result["parameters"] = AutomationCatalog.StyleFolder(folder)["parameters"]!.DeepClone();
        result["notes"] = new JsonArray(outcome.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
    }
}
