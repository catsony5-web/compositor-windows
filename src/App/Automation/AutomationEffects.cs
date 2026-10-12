using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Compositor.Windows;

// set_layer_effects: live layer effects (레이어 효과) over the AI connection — the whole ordered list
// is replaced in one step, with the same model, limits and defaults as the properties panel.
public static partial class AutomationCatalog
{
    static readonly string[] EffectKinds = ["stroke", "drop_shadow", "outer_glow", "inner_glow", "color_overlay", "gradient_overlay"];
    static readonly string[] EffectPositions = ["outside", "center", "inside"];
    // Fields each kind accepts besides kind and enabled.
    static readonly Dictionary<string, string[]> EffectFields = new(StringComparer.Ordinal)
    {
        ["stroke"] = ["color", "opacity", "size", "position", "rounded"],
        ["drop_shadow"] = ["color", "opacity", "size", "spread", "angle", "distance", "blend"],
        ["outer_glow"] = ["color", "opacity", "size", "spread", "blend"],
        ["inner_glow"] = ["color", "opacity", "size", "spread", "blend"],
        ["color_overlay"] = ["color", "opacity", "blend"],
        ["gradient_overlay"] = ["color", "endColor", "opacity", "angle", "blend"]
    };
    const string EffectColorPattern = "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$";

    static void AddLayerEffectCommands(Action<string, string, bool, Dictionary<string, Field>, string[]> add, Func<(string Key, Field Value)[], Dictionary<string, Field>> mutation, Func<string[], string[]> required)
    {
        add("set_layer_effects", "Replace a layer's live effects (layer effects: stroke, drop_shadow, outer_glow, inner_glow, color_overlay, gradient_overlay) with the given list; an empty list removes them. " +
            "Effects stay editable and follow the layer's content, moves and transforms. They paint in list order from the bottom (first) to the top (last): drop shadows and outer glows go behind the layer onto what is below it, " +
            "the other kinds onto the layer itself (an outside stroke under its pixels, centre and inside strokes, inner glows and overlays over them; inside strokes, inner glows and overlays stay within the layer's own coverage, so an overlay recolors a cut-out or icon and keeps its transparency). " +
            "Lengths (size, distance) are parent pixels (document pixels for root layers) at any layer scale; angles are degrees, 0 = right, 90 = down. Omitted fields take the kind's defaults (get_layer reports them). " +
            "Image, text, shape, drawing and material layers only (not groups or adjustment layers). At most " + LayerEffect.MaxCount + " effects. One undo step; can be included in apply_batch. Returns effects.", false,
            mutation([("layerId", Id), ("effects", new("array", "Ordered effect list, bottom first; [] removes all effects.", ArrayShape: "effects"))]), required(["layerId", "effects"]));
    }

    static JsonObject EffectsSchema()
    {
        static JsonObject Color(string description) => new() { ["type"] = "string", ["pattern"] = EffectColorPattern, ["description"] = description };
        var item = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = Strings(["kind"]),
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(EffectKinds) },
                ["enabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "Turned on (default true); a turned-off effect keeps its settings." },
                ["color"] = Color("#RRGGBB or #AARRGGBB; the gradient's start color."),
                ["endColor"] = Color("gradient_overlay: color where the direction ends."),
                ["opacity"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
                ["size"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = LayerEffect.MaxSize, ["description"] = "stroke: width (at least 0.5); glows: how far they reach; drop_shadow: softness. Parent pixels." },
                ["spread"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1, ["description"] = "Glows and drop_shadow: share of size that stays solid before it softens." },
                ["position"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(EffectPositions), ["description"] = "stroke: outside (default), center or inside the edge." },
                ["rounded"] = new JsonObject { ["type"] = "boolean", ["description"] = "stroke: round corners (default true) or square." },
                ["blend"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["Normal", "Multiply", "Screen", "Color"]), ["description"] = "drop_shadow: Normal or Multiply; glows: Normal or Screen; overlays: Normal, Multiply, Screen or Color." },
                ["angle"] = new JsonObject { ["type"] = "number", ["minimum"] = -360, ["maximum"] = 360, ["description"] = "drop_shadow: direction it falls; gradient_overlay: direction from color to endColor." },
                ["distance"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = LayerEffect.MaxDistance, ["description"] = "drop_shadow: offset from the layer in parent pixels." }
            }
        };
        return new JsonObject { ["type"] = "array", ["maxItems"] = LayerEffect.MaxCount, ["items"] = item };
    }

    static void ValidateEffectsArray(JsonArray array) => _ = ParseEffects(array);

    /// <summary>The effect list an AI client asked for (null for an empty list), validated like the editor's own.</summary>
    public static LayerEffect[]? ParseEffects(JsonArray array)
    {
        if (array.Count > LayerEffect.MaxCount) throw new ArgumentException($"A layer holds at most {LayerEffect.MaxCount} effects.");
        var effects = new List<LayerEffect>();
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item || item["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) || !EffectFields.TryGetValue(kind, out var allowed))
                throw new ArgumentException($"effects[{i}] needs kind: {string.Join(", ", EffectKinds)}.");
            foreach (var (key, _) in item)
                if (key is not ("kind" or "enabled") && !allowed.Contains(key)) throw new ArgumentException($"effects[{i}]: {key} is not a setting of {kind} (use {string.Join(", ", allowed)}).");
            double Number(string key, double fallback, double min, double max)
            {
                if (item[key] is not { } node) return fallback;
                if (node.GetValueKind() != System.Text.Json.JsonValueKind.Number || !double.IsFinite(NumberValue(item, key)) || NumberValue(item, key) < min || NumberValue(item, key) > max)
                    throw new ArgumentException($"effects[{i}].{key} must be a number between {min} and {max}.");
                return NumberValue(item, key);
            }
            bool Bool(string key, bool fallback) => item[key] is not { } node ? fallback
                : node.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? node.GetValue<bool>() : throw new ArgumentException($"effects[{i}].{key} must be true or false.");
            string? Text(string key) => item[key] is not { } node ? null : node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"effects[{i}].{key} must be a string.");
            uint Color(string key, uint fallback)
            {
                if (Text(key) is not { } text) return fallback;
                if (!Regex.IsMatch(text, EffectColorPattern, RegexOptions.CultureInvariant)) throw new ArgumentException($"effects[{i}].{key}: use #RRGGBB or #AARRGGBB.");
                uint value = Convert.ToUInt32(text[1..], 16);
                return text.Length == 7 ? 0xFF000000 | value : value;
            }
            var kindEnum = (LayerEffectKind)Array.IndexOf(EffectKinds, kind);
            var effect = LayerEffect.Default(kindEnum);
            var blendText = Text("blend");
            if (blendText != null && (!Enum.TryParse<BlendMode>(blendText, false, out var blend) || !LayerEffect.BlendsFor(kindEnum).Contains(blend)))
                throw new ArgumentException($"effects[{i}].blend for {kind}: use {string.Join(", ", LayerEffect.BlendsFor(kindEnum))}.");
            var positionText = Text("position");
            if (positionText != null && !EffectPositions.Contains(positionText)) throw new ArgumentException($"effects[{i}].position: use outside, center or inside.");
            effect = effect with
            {
                Enabled = Bool("enabled", true), ColorArgb = Color("color", effect.ColorArgb), EndColorArgb = Color("endColor", effect.EndColorArgb),
                Opacity = Number("opacity", effect.Opacity, 0, 1), Size = Number("size", effect.Size, kindEnum == LayerEffectKind.Stroke ? .5 : 0, LayerEffect.MaxSize),
                Spread = Number("spread", effect.Spread, 0, 1), Angle = Number("angle", effect.Angle, -360, 360), Distance = Number("distance", effect.Distance, 0, LayerEffect.MaxDistance),
                Position = positionText == null ? effect.Position : (StrokePosition)Array.IndexOf(EffectPositions, positionText), Rounded = Bool("rounded", effect.Rounded),
                Blend = blendText == null ? effect.Blend : Enum.Parse<BlendMode>(blendText)
            };
            try { effect.Validate(); } catch (System.IO.InvalidDataException e) { throw new ArgumentException($"effects[{i}]: {e.Message}"); }
            effects.Add(effect);
        }
        return LayerEffects.Normalize(effects);
    }

    /// <summary>A layer's effects as set_layer_effects takes them (every field of each kind spelled out).</summary>
    public static JsonArray EffectsJson(LayerEffect[]? effects)
    {
        var result = new JsonArray();
        foreach (var effect in effects ?? [])
        {
            string kind = EffectKinds[(int)effect.Kind];
            var item = new JsonObject { ["kind"] = kind, ["enabled"] = effect.Enabled };
            foreach (var key in EffectFields[kind])
                item[key] = key switch
                {
                    "color" => $"#{effect.ColorArgb:X8}", "endColor" => $"#{effect.EndColorArgb:X8}", "opacity" => effect.Opacity, "size" => effect.Size,
                    "spread" => effect.Spread, "position" => EffectPositions[(int)effect.Position], "rounded" => effect.Rounded, "blend" => effect.Blend.ToString(),
                    "angle" => effect.Angle, _ => (JsonNode?)effect.Distance
                };
            result.Add(item);
        }
        return result;
    }

    static JsonObject LayerEffectCapabilities() => new()
    {
        ["command"] = "set_layer_effects", ["kinds"] = Strings(EffectKinds), ["maxPerLayer"] = LayerEffect.MaxCount,
        ["order"] = "List order from the bottom (first) to the top (last). drop_shadow and outer_glow paint behind the layer; the other kinds on it.",
        ["units"] = "size and distance in parent pixels (document pixels for root layers) at any layer scale; angle in degrees, 0 = right, 90 = down",
        ["inspect"] = "get_layer effects", ["batch"] = true, ["undoSteps"] = 1,
        ["exports"] = "PNG/JPEG/TIFF draw effects at the output resolution; .psd draws them into the layer's pixels; PDF/.ai keep strokes on text and shapes as vectors, other layers with effects become images of their visible result"
    };
}
