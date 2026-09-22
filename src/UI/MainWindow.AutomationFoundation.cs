using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    sealed record BatchReceipt(string Fingerprint, Guid DocumentId, JsonObject Result);
    readonly Dictionary<Guid, BatchReceipt> automationBatchReceipts = [];
    readonly Queue<Guid> automationBatchOrder = [];

    IReadOnlySet<Guid> AutomationSelectedIds(WorkspaceTab tab) => activeTab >= 0 && tabs[activeTab] == tab
        ? selectedLayers : new HashSet<Guid> { tab.Document.ActiveId };

    JsonObject AutomationInspect(string command, JsonObject args)
    {
        var tab = AutomationTab(args);
        if (args.ContainsKey("expectedRevision") && tab.Document.Revision != Guid.Parse(AString(args, "expectedRevision")))
            throw new AutomationFault("stale_revision", "조회 중 문서가 변경되었습니다. 첫 페이지부터 다시 확인하세요.");
        var scene = new AutomationScene(tab.Document);
        if (args.ContainsKey("parentId") && !scene.Contains(Guid.Parse(AString(args, "parentId"))))
            throw new AutomationFault("layer_not_found", "조회할 부모 레이어가 없습니다.");
        JsonObject result;
        if (command == "query_layers") result = scene.Query(args, AutomationSelectedIds(tab));
        else
        {
            var id = Guid.Parse(AString(args, "layerId"));
            if (!scene.Contains(id)) throw new AutomationFault("layer_not_found", "레이어를 찾을 수 없습니다.");
            result = new JsonObject { ["revision"] = tab.Document.Revision.ToString(), ["layer"] = scene.Describe(id) };
        }
        result["documentId"] = tab.Id.ToString();
        return result;
    }

    async Task<JsonObject> AutomationBatchAsync(JsonObject args, CancellationToken token)
    {
        var operationId = Guid.Parse(AString(args, "operationId"));
        bool dryRun = ABool(args, "dryRun");
        string fingerprint = BatchFingerprint(args);
        if (!dryRun && automationBatchReceipts.TryGetValue(operationId, out var receipt))
        {
            if (receipt.Fingerprint != fingerprint)
                throw new AutomationFault("operation_id_conflict", "같은 operationId로 다른 작업을 실행할 수 없습니다.");
            var replay = (JsonObject)receipt.Result.DeepClone();
            replay["replayed"] = true;
            replay["currentRevision"] = tabs.FirstOrDefault(t => t.Id == receipt.DocumentId)?.Document.Revision.ToString();
            return replay;
        }

        var tab = AutomationTab(args, true);
        var before = doc.Snapshot(); var candidate = doc.Snapshot();
        var steps = args["steps"]!.AsArray(); var results = new JsonArray();
        Guid? affected = null;
        for (int i = 0; i < steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var step = steps[i]!.AsObject(); string command = AString(step, "command");
            var arguments = AutomationCatalog.BatchArguments(args, step["arguments"]!.AsObject());
            try
            {
                affected = await ApplyAutomationEditAsync(candidate, command, arguments, token);
                candidate.Validate();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new AutomationFault(error is AutomationFault fault ? fault.Code : "invalid_arguments", error.Message,
                    new JsonObject { ["stepIndex"] = i, ["command"] = command, ["committed"] = false });
            }
            // IDs allocated during a dry run are not live document IDs and must not escape as editable targets.
            results.Add(new JsonObject { ["stepIndex"] = i, ["command"] = command, ["layerId"] = dryRun ? null : affected?.ToString() });
        }
        RequireAutomationIdle(token); _ = AutomationTab(args, true);
        bool changed = !SameDocument(before, candidate);
        var previousIds = before.Layers.Select(l => l.Id).ToHashSet();
        var nextIds = candidate.Layers.Select(l => l.Id).ToHashSet();
        var response = new JsonObject
        {
            ["documentId"] = tab.Id.ToString(), ["operationId"] = operationId.ToString(),
            ["dryRun"] = dryRun, ["validated"] = true, ["committed"] = !dryRun, ["replayed"] = false,
            ["wouldChange"] = changed, ["changed"] = !dryRun && changed,
            ["beforeRevision"] = before.Revision.ToString(), ["revision"] = before.Revision.ToString(),
            ["currentRevision"] = before.Revision.ToString(), ["undoSteps"] = !dryRun && changed ? 1 : 0,
            ["createdLayerCount"] = nextIds.Except(previousIds).Count(), ["removedLayerCount"] = previousIds.Except(nextIds).Count(),
            ["steps"] = results
        };
        if (dryRun) return response;

        string label = args.ContainsKey("label") ? AString(args, "label") : "묶음 편집";
        CommitAutomationCandidate("AI · " + label, before, candidate, affected, () =>
        {
            response["revision"] = doc.Revision.ToString(); response["currentRevision"] = doc.Revision.ToString();
            // Remember the commit before refreshing UI or delivering the reply, so a lost response cannot duplicate it.
            automationBatchReceipts.Add(operationId, new BatchReceipt(fingerprint, tab.Id, (JsonObject)response.DeepClone()));
            automationBatchOrder.Enqueue(operationId);
            while (automationBatchOrder.Count > AutomationCatalog.MaximumBatchReceipts)
                automationBatchReceipts.Remove(automationBatchOrder.Dequeue());
        });
        return response;
    }

    static string BatchFingerprint(JsonObject arguments)
    {
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonical(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        var value = (JsonObject)arguments.DeepClone();
        value.Remove("dryRun"); // A successful dry run may be committed with the same plan and operationId.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)!.ToJsonString())));
    }
}
