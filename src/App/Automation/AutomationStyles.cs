using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Windows;

// query_styles / apply_style: the design style registry for AI clients, and strict checks of the
// style parameters (keys of the chosen style, slider ranges, toggles, choice keys or indices).
public static partial class AutomationCatalog
{
    static JsonObject StyleParametersSchema() => new()
    {
        ["type"] = "object", ["maxProperties"] = StyleTag.MaxValues,
        ["propertyNames"] = new JsonObject { ["pattern"] = "^[a-z][a-z0-9-]{0,63}$" },
        ["additionalProperties"] = new JsonObject
        {
            ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, new JsonObject { ["type"] = "boolean" }, new JsonObject { ["type"] = "string", ["maxLength"] = 64 })
        }
    };

    static JsonObject IdArraySchema() => new()
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4096, ["uniqueItems"] = true,
        ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["minLength"] = 36, ["maxLength"] = 36 }
    };

    static void ValidateIdArray(JsonArray array)
    {
        if (array.Count is < 1 or > 4096) throw new ArgumentException("targetLayerIds needs 1..4096 layer IDs.");
        var seen = new HashSet<Guid>();
        foreach (var item in array)
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || !seen.Add(id))
                throw new ArgumentException("targetLayerIds must be distinct, nonempty GUIDs returned by Morupixel.");
    }

    /// <summary>Parameter values for a style from an apply_style "parameters" object; throws on unknown keys, wrong types or out-of-range values.</summary>
    public static Dictionary<string, double> StyleValues(string styleId, JsonObject? parameters)
    {
        var style = DesignStyles.Find(styleId) ?? throw new ArgumentException($"Unknown styleId: {styleId}. Use query_styles.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (parameters == null) return result;
        if (parameters.Count > StyleTag.MaxValues) throw new ArgumentException("Too many style parameters.");
        foreach (var (key, node) in parameters)
        {
            var parameter = style.Parameter(key) ?? throw new ArgumentException($"Unknown parameter for {styleId}: {key}. Parameters: {string.Join(", ", style.Parameters.Select(p => p.Key))}.");
            var kind = node?.GetValueKind() ?? JsonValueKind.Null;
            double Number() => JsonSerializer.Deserialize<double>(node!.ToJsonString());
            switch (parameter.Kind)
            {
                case StyleParameterKind.Slider:
                    if (kind != JsonValueKind.Number || !double.IsFinite(Number()) || Number() < parameter.Min || Number() > parameter.Max)
                        throw new ArgumentException($"{key} must be a number between {parameter.Min} and {parameter.Max}.");
                    result[key] = Number(); break;
                case StyleParameterKind.Toggle:
                    if (kind is JsonValueKind.True or JsonValueKind.False) result[key] = kind == JsonValueKind.True ? 1 : 0;
                    else if (kind == JsonValueKind.Number && Number() is 0 or 1) result[key] = Number();
                    else throw new ArgumentException($"{key} must be true or false.");
                    break;
                default:
                    var keys = parameter.Choices.Select(c => c.Key).ToArray();
                    if (kind == JsonValueKind.String && Array.IndexOf(keys, node!.GetValue<string>()) is var index and >= 0) result[key] = index;
                    else if (kind == JsonValueKind.Number && Number() == Math.Truncate(Number()) && Number() >= 0 && Number() < keys.Length) result[key] = Number();
                    else throw new ArgumentException($"{key} must be one of {string.Join(", ", keys)} (or its index 0..{keys.Length - 1}).");
                    break;
            }
        }
        return result;
    }

    static void ValidateStyleArguments(JsonObject arguments)
    {
        string styleId = arguments["styleId"]!.GetValue<string>();
        _ = StyleValues(styleId, arguments["parameters"] as JsonObject);
        if (arguments.ContainsKey("groupId") && arguments.ContainsKey("targetLayerIds"))
            throw new ArgumentException("groupId re-applies a style folder with the targets it already reads; omit targetLayerIds.");
    }

    /// <summary>The registry as JSON: ids, Korean names, what each suits and the parameters.</summary>
    public static JsonArray StylesJson() => new(DesignStyles.All.Select(style => (JsonNode?)new JsonObject
    {
        ["styleId"] = style.Id, ["name"] = style.Name, ["displayName"] = Loc.T(style.Name), ["description"] = style.Description,
        ["target"] = style.Target switch { StyleTarget.Drawing => "drawing", StyleTarget.Photo => "photo", _ => "any" }, ["version"] = style.Version,
        ["folderName"] = DesignStyles.GroupName(style),
        ["parameters"] = new JsonArray(style.Parameters.Select(p => (JsonNode?)new JsonObject
        {
            ["key"] = p.Key, ["name"] = p.Name, ["description"] = p.Description,
            ["type"] = p.Kind switch { StyleParameterKind.Toggle => "boolean", StyleParameterKind.Choice => "choice", _ => "number" },
            ["minimum"] = p.Kind == StyleParameterKind.Slider ? p.Min : null, ["maximum"] = p.Kind == StyleParameterKind.Slider ? p.Max : null,
            ["default"] = p.Kind switch { StyleParameterKind.Toggle => JsonValue.Create(p.Default >= .5), StyleParameterKind.Choice => JsonValue.Create(p.Choices[(int)p.Default].Key), _ => JsonValue.Create(p.Default) },
            ["choices"] = p.Kind == StyleParameterKind.Choice ? new JsonArray(p.Choices.Select(c => (JsonNode?)new JsonObject { ["key"] = c.Key, ["name"] = c.Name }).ToArray()) : null
        }).ToArray())
    }).ToArray());

    /// <summary>A style folder's style and values as JSON (choices by key, toggles as booleans).</summary>
    public static JsonObject StyleFolder(Layer folder)
    {
        var tag = folder.Style!; var style = DesignStyles.Find(tag.StyleId); var values = new JsonObject();
        foreach (var value in tag.Values)
        {
            var parameter = style?.Parameter(value.Key);
            values[value.Key] = parameter?.Kind switch
            {
                StyleParameterKind.Toggle => JsonValue.Create(value.Value >= .5),
                StyleParameterKind.Choice when (int)value.Value >= 0 && (int)value.Value < parameter.Choices.Length => JsonValue.Create(parameter.Choices[(int)value.Value].Key),
                _ => JsonValue.Create(value.Value)
            };
        }
        return new JsonObject
        {
            ["groupId"] = folder.Id.ToString(), ["name"] = folder.Name, ["styleId"] = tag.StyleId, ["known"] = style != null, ["version"] = tag.Version,
            ["parameters"] = values, ["visible"] = folder.Visible, ["opacity"] = folder.Opacity, ["locked"] = folder.Locked,
            ["targetLayerIds"] = new JsonArray(tag.Targets.Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray()),
            ["hiddenLayerCount"] = tag.Edits.Length
        };
    }
}
