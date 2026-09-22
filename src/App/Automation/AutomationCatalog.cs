using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Compositor.Windows;

/// <summary>One command/schema catalog shared by the local bridge, CLI and MCP transport.</summary>
public static partial class AutomationCatalog
{
    sealed record Field(string Type, string Description, double? Minimum = null, double? Maximum = null,
        int MaxLength = 0, bool EmptyAllowed = false, string[]? Choices = null, bool GuidValue = false, bool Color = false, string? ArrayShape = null)
    {
        public JsonObject Schema()
        {
            if (Type == "array") { var items = ArrayShape == null ? BatchStepsSchema() : MaterialArraySchema(ArrayShape); items["description"] = Description; return items; }
            var schema = new JsonObject { ["type"] = Type, ["description"] = Description };
            if (Minimum is { } min) schema["minimum"] = min;
            if (Maximum is { } max) schema["maximum"] = max;
            if (MaxLength > 0) { schema["maxLength"] = MaxLength; schema["minLength"] = EmptyAllowed ? 0 : 1; }
            if (Choices != null) schema["enum"] = new JsonArray(Choices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            if (GuidValue) schema["format"] = "uuid";
            if (Color) schema["pattern"] = "^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|transparent)$";
            return schema;
        }
    }
    sealed record Command(string Name, string Description, bool ReadOnly, Dictionary<string, Field> Fields, string[] Required);
    static readonly Field Id = new("string", "GUID returned by Morupixel; do not invent an identifier.", MaxLength: 36, GuidValue: true);
    static readonly Field Name = new("string", "Display name.", MaxLength: 4096);
    static readonly Field Path = new("string", "Absolute Windows file path on the computer running Morupixel.", MaxLength: 32767);
    static readonly Field Color = new("string", "#RRGGBB, #AARRGGBB, or transparent.", MaxLength: 11, Color: true);
    static readonly Field Coordinate = Number(-100_000, 100_000, "Position in parent-layer pixels; document pixels for root layers. See get_capabilities for coordinate conventions.");
    static readonly Dictionary<string, Command> Commands = CreateCommands();

    static Field Number(double min, double max, string description = "Numeric value.") => new("number", description, min, max);
    static Field Integer(int min, int max, string description = "Whole-number value.") => new("integer", description, min, max);
    static Field Bool(string description) => new("boolean", description);
    static Field Choice(params string[] choices) => new("string", "One of the listed values (case-sensitive).", Choices: choices);
    static Dictionary<string, Field> Fields(params (string Key, Field Value)[] fields) => fields.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    static Dictionary<string, Command> CreateCommands()
    {
        var commands = new Dictionary<string, Command>(StringComparer.Ordinal);
        void Add(string name, string description, bool readOnly, Dictionary<string, Field> fields, params string[] required)
            => commands.Add(name, new(name, description, readOnly, fields, required));
        Dictionary<string, Field> Mutation(params (string Key, Field Value)[] fields)
        {
            var result = Fields(("documentId", Id), ("expectedRevision", Id));
            foreach (var (key, value) in fields) result.Add(key, value);
            return result;
        }
        string[] WriteRequired(params string[] fields) => new[] { "documentId", "expectedRevision" }.Concat(fields).ToArray();
        var textFields = Fields(("text", new("string", "Text content, including line breaks.", MaxLength: 100_000, EmptyAllowed: true)),
            ("fontFamily", new("string", "Installed font family name.", MaxLength: 256)), ("fontSize", Number(1, 1024)),
            ("color", Color), ("x", Coordinate), ("y", Coordinate), ("name", Name), ("bold", Bool("Bold text.")),
            ("italic", Bool("Italic text.")), ("alignment", Choice("Left", "Center", "Right")),
            ("lineHeight", Number(0, 8192, "Line height in pixels; zero uses natural spacing.")),
            ("tracking", Number(-200, 2000, "Tracking in 1/1000 em.")));
        Add("list_sessions", "List Morupixel windows where the user enabled AI control. Does not open or focus a window.", true, Fields());
        Add("get_state", "Inspect document identifiers, revisions and counts. Use includeLayers=false for a compact overview, then query_layers/get_layer for objects. The legacy default includes all layers.", true,
            Fields(("documentId", Id), ("includeLayers", Bool("Defaults to true for existing clients. Set false to omit the potentially large layer inventory."))));
        Add("get_capabilities", "Read the running editor's command contract, limits, coordinate conventions and unsupported operations before planning edits.", true, Fields());
        Add("query_layers", "Find layers by name, kind or parent without loading the whole drawing. Results use back-to-front storage order. For subsequent pages pass the returned revision as expectedRevision and nextOffset as offset.", true,
            Fields(("documentId", Id), ("expectedRevision", Id), ("parentId", Id), ("rootsOnly", Bool("Only root layers; cannot be combined with parentId.")),
                ("nameContains", new("string", "Case-insensitive literal substring of layer names, not a regular expression.", MaxLength: 256)),
                ("kind", Choice(Enum.GetNames<LayerKind>())), ("visible", Bool("Filter the layer's own visibility flag.")),
                ("category", new("string", "Effective workspace category, including inheritance from the root drawing/photo folder.", Choices: ["Drawing", "Photo"])),
                ("selectedOnly", Bool("Only selected layers; an inactive document reports its active layer as selected.")),
                ("locked", Bool("Filter the layer's own lock flag; inherited locks are reported separately.")),
                ("offset", Integer(0, Document.MaxNodes)), ("limit", Integer(1, 200, "Page size; defaults to 50."))), "documentId");
        Add("get_layer", "Inspect one exact layer, its parent chain, transforms, retained content and supported edits. Frame bounds describe the transformed surface, not ink or a fillable room boundary.", true,
            Fields(("documentId", Id), ("expectedRevision", Id), ("layerId", Id)), "documentId", "layerId");
        Add("new_document", "Create and activate a document (maximum 16,777,216 pixels). Returns its identifiers and revision.", false,
            Fields(("name", Name), ("width", Integer(1, 8192)), ("height", Integer(1, 8192)), ("background", Color), ("dpi", Number(1, 9600))),
            "name", "width", "height");
        Add("open_document", "Open a supported local file as a new document; existing unsaved documents remain open.", false,
            Fields(("path", Path)), "path");
        Add("activate_document", "Activate an existing document by its returned identifier before editing it.", false,
            Fields(("documentId", Id)), "documentId");
        Add("add_image", "Add a local image as a layer in the active document. Obtain a fresh revision first.", false,
            Mutation(("path", Path), ("x", Coordinate), ("y", Coordinate), ("name", Name)), WriteRequired("path"));
        var addText = Mutation(); foreach (var field in textFields) addText.Add(field.Key, field.Value);
        Add("add_text", "Add editable text. Coordinates and font size use document pixels.", false, addText, WriteRequired("text"));
        var updateText = Mutation(("layerId", Id)); foreach (var field in textFields) updateText.Add(field.Key, field.Value);
        Add("update_text", "Change supported properties of an existing editable text layer.", false, updateText, WriteRequired("layerId"));
        Add("add_shape", "Add an editable rectangle or ellipse (maximum 16,777,216 pixels).", false,
            Mutation(("shape", Choice("rectangle", "ellipse")), ("width", Integer(1, 8192)), ("height", Integer(1, 8192)),
                ("x", Coordinate), ("y", Coordinate), ("fill", Color), ("stroke", Color), ("strokeWidth", Number(0, 512)),
                ("cornerRadius", Number(0, 4096)), ("name", Name)), WriteRequired("shape", "width", "height"));
        Add("set_layer", "Change layer properties; opacity is 0–1 and scale values are multipliers. Locked layers may reject editing.", false,
            Mutation(("layerId", Id), ("name", Name), ("x", Coordinate), ("y", Coordinate), ("scaleX", Number(.01, 20)),
                ("scaleY", Number(.01, 20)), ("rotation", Number(-36_000, 36_000)), ("opacity", Number(0, 1)),
                ("visible", Bool("Layer visibility.")), ("locked", Bool("Prevent accidental edits.")), ("blend", Choice(Enum.GetNames<BlendMode>()))), WriteRequired("layerId"));
        Add("delete_layer", "Delete the specified layer through the document undo history.", false, Mutation(("layerId", Id)), WriteRequired("layerId"));
        Add("reorder_layer", "Move a layer one position up or down in the layer stack.", false,
            Mutation(("layerId", Id), ("direction", Choice("up", "down"))), WriteRequired("layerId", "direction"));
        var adjustments = Mutation(("kind", Choice("exposure", "levels", "hue_saturation", "photo_develop")), ("name", Name),
            ("exposure", Number(-20, 20, "Exposure EV; photo_develop accepts -5 to 5.")), ("offset", Number(-.5, .5)),
            ("gamma", Number(.1, 9.99)), ("black", Number(0, 254)), ("white", Number(1, 255)), ("hue", Number(-360, 360)),
            ("saturation", Number(-100, 100)), ("lightness", Number(-100, 100)));
        foreach (var key in new[] { "temperature", "tint", "contrast", "highlights", "shadows", "whites", "blacks", "texture", "clarity", "dehaze", "vibrance" })
            adjustments.Add(key, Number(-100, 100));
        Add("add_adjustment", "Add a reversible RGB8 adjustment layer. photo_develop is an image adjustment, not camera RAW decoding. Supply only parameters for the chosen kind.",
            false, adjustments, WriteRequired("kind"));
        Add("remove_background", "Create a layer mask using the bundled local AI model. Existing edits remain undoable.", false,
            Mutation(("layerId", Id)), WriteRequired("layerId"));
        Add("save_project", "Save the active document to an absolute .moruproj path. Existing files require overwrite=true.", false,
            Mutation(("path", Path), ("overwrite", Bool("Defaults to false; true explicitly permits replacing the destination."))), WriteRequired("path"));
        Add("export_image", "Export the active document or one selected layer to an image file. Existing files require overwrite=true.", false,
            Mutation(("path", Path), ("quality", Integer(1, 100, "JPEG quality; defaults to 95.")),
                ("overwrite", Bool("Defaults to false; true explicitly permits replacing the destination.")), ("layerId", Id)), WriteRequired("path"));
        Add("preview", "Render a scaled PNG preview without changing or exporting the document. maxSide defaults to 512.", true,
            Fields(("documentId", Id), ("maxSide", Integer(1, 1024))), "documentId");
        Add("undo", "Undo one document edit, rejecting a stale expectedRevision.", false, Mutation(), WriteRequired());
        Add("redo", "Redo one document edit, rejecting a stale expectedRevision.", false, Mutation(), WriteRequired());
        Add("apply_batch", "Validate and apply up to 64 ordered document edits atomically as one undo step. dryRun=true validates on a snapshot without editing. Reuse operationId only to retry the identical request; successful receipts are retained for the last 128 batches in this editor session. Does not write files or generate AI images.", false,
            Mutation(("operationId", new("string", "Caller-generated nonempty UUID for this logical batch. Use a new UUID for a new operation.", MaxLength: 36, GuidValue: true)),
                ("label", new("string", "Short undo history label describing the user's intent.", MaxLength: 120)),
                ("dryRun", Bool("Validate every step and document limit without committing; defaults to false.")),
                ("steps", new("array", "Ordered atomic edits; each command has its own strict argument schema."))), WriteRequired("operationId", "steps"));
        Add("register_material", "Register an existing local texture image in the document's embedded material library. Does not generate images or use an AI provider account. Returns materialId; use query_materials after uncertain delivery instead of blindly retrying.", false,
            Mutation(("name", Name), ("path", Path), ("source", new("string", "Optional provenance label supplied by the caller, not a verified credential.", MaxLength: 4096)), ("tileable", Bool("Caller-declared seamless texture; no seam correction is performed."))), WriteRequired("name", "path"));
        foreach (string query in new[] { "query_materials", "query_regions" })
            Add(query, "List document material assets or captured region templates in bounded pages. Pass expectedRevision for further pages. Region templates are snapshots, not semantic room detection.", true,
                Fields(("documentId", Id), ("expectedRevision", Id), ("nameContains", new("string", "Case-insensitive literal name substring.", MaxLength: 256)),
                    ("offset", Integer(0, MaterialEditing.MaxRegions)), ("limit", Integer(1, 50, "Defaults to 20."))), "documentId");
        Add("define_region", "Capture a closed 2D boundary in document pixels. source=polygon needs points and optional inner contours (even-odd fill); closed_layer needs layerId; selection uses the user's current 50% selection contour. Captures a reusable template, not a live link or inferred room.", false,
            Mutation(("name", Name), ("source", Choice("polygon", "closed_layer", "selection")), ("layerId", Id),
                ("points", new("array", "Closed outer contour, at least 3 points. Closure is automatic.", ArrayShape: "points")),
                ("holes", new("array", "Optional inner contours; at most 2048 points across all contours.", ArrayShape: "holes"))), WriteRequired("name", "source"));
        var pattern = Fields(("materialId", Id), ("tileWidth", Number(1, 100000, "One texture repeat width in layer-local pixels, not millimeters.")),
            ("tileHeight", Number(1, 100000, "One texture repeat height in layer-local pixels.")), ("angle", Number(-36000, 36000, "Pattern rotation in degrees around the local origin.")),
            ("offsetX", Coordinate), ("offsetY", Coordinate), ("name", Name));
        Add("apply_material", "Create an editable material layer from a registered image and region. Original texture and vector boundary remain stored. Added above existing layers with Multiply by default to keep drawing lines visible. Returns layerId; can be included in apply_batch.", false,
            Mutation(pattern.Select(p => (p.Key, p.Value)).Concat(new[] { ("regionId", Id), ("opacity", Number(0, 1)), ("blend", Choice(Enum.GetNames<BlendMode>())) }).ToArray()), WriteRequired("materialId", "regionId", "tileWidth", "tileHeight"));
        Add("update_material", "Change the source material or repeat size, direction and offset of an existing material layer, preserving its boundary and layer transform. Can be included in apply_batch.", false,
            Mutation(pattern.Select(p => (p.Key, p.Value)).Append(("layerId", Id)).ToArray()), WriteRequired("layerId"));
        return commands;
    }

    public static bool Known(string command) => command != null && Commands.ContainsKey(command);

    /// <summary>Validates bridge arguments (without the MCP-only sessionId). Never alters the supplied object.</summary>
    public static void Validate(string command, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Commands.TryGetValue(command, out var definition)) throw new ArgumentException($"Unknown command: {command}");
        foreach (var key in definition.Required)
            if (!arguments.ContainsKey(key)) throw new ArgumentException($"Required argument: {key}");
        foreach (var (key, value) in arguments)
        {
            if (!definition.Fields.TryGetValue(key, out var field)) throw new ArgumentException($"Unknown argument for {command}: {key}");
            JsonValueKind kind;
            try { kind = value?.GetValueKind() ?? JsonValueKind.Null; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            { throw new ArgumentException($"Invalid JSON value for {key}.", e); }
            bool validType = field.Type switch
            {
                "string" => kind == JsonValueKind.String,
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "array" => kind == JsonValueKind.Array,
                _ => kind == JsonValueKind.Number
            };
            if (!validType)
                throw new ArgumentException($"Invalid type for {key}: expected {field.Type}.");
            if (field.Type == "array")
            {
                if (field.ArrayShape == null) ValidateBatchSteps(value!.AsArray(), arguments);
                else ValidateMaterialArray(value!.AsArray(), field.ArrayShape);
            }
            else if (field.Type == "boolean")
            {
                // Type validation above already guarantees a JSON boolean.
            }
            else if (field.Type == "string")
            {
                string text = value!.GetValue<string>();
                if ((!field.EmptyAllowed && string.IsNullOrWhiteSpace(text)) || field.MaxLength > 0 && text.Length > field.MaxLength)
                    throw new ArgumentException($"Invalid length for {key} (maximum {field.MaxLength}).");
                if (field.Choices != null && !field.Choices.Contains(text, StringComparer.Ordinal)) throw new ArgumentException($"Invalid {key}: choose {string.Join(", ", field.Choices)}.");
                if (field.GuidValue && (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)) throw new ArgumentException($"{key} must be a nonempty GUID returned by Morupixel.");
                if (field.Color && !Regex.IsMatch(text, "^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|transparent)$", RegexOptions.CultureInvariant))
                    throw new ArgumentException($"Invalid {key}: use #RRGGBB, #AARRGGBB, or transparent.");
            }
            else
            {
                double number;
                try { number = JsonSerializer.Deserialize<double>(value!.ToJsonString()); }
                catch (Exception e) when (e is JsonException or ArgumentException) { throw new ArgumentException($"Invalid finite number for {key}."); }
                if (!double.IsFinite(number) || field.Type == "integer" && number != Math.Truncate(number) ||
                    field.Minimum is { } min && number < min || field.Maximum is { } max && number > max)
                    throw new ArgumentException($"{key} must be {field.Type} between {field.Minimum} and {field.Maximum}.");
            }
        }
        if (command is "new_document" or "add_shape" && (double)NumberValue(arguments, "width") * NumberValue(arguments, "height") > 16_777_216)
            throw new ArgumentException("Automation images must not exceed 16,777,216 pixels.");
        if (command is "query_layers" or "query_materials" or "query_regions")
        {
            if (arguments.ContainsKey("parentId") && arguments["rootsOnly"]?.GetValue<bool>() == true)
                throw new ArgumentException("Use parentId or rootsOnly, not both.");
            if (NumberValue(arguments, "offset") > 0 && !arguments.ContainsKey("expectedRevision"))
                throw new ArgumentException("Paged queries require the previous page's revision as expectedRevision.");
        }
        if (command == "define_region")
        {
            string source = arguments["source"]!.GetValue<string>();
            if ((source == "polygon") != arguments.ContainsKey("points") || (source == "closed_layer") != arguments.ContainsKey("layerId") || source != "polygon" && arguments.ContainsKey("holes"))
                throw new ArgumentException("polygon requires points; closed_layer requires layerId; selection accepts neither. holes belong to polygon only.");
            int count = (arguments["points"] as JsonArray)?.Count ?? 0;
            count += (arguments["holes"] as JsonArray)?.Sum(h => h!.AsArray().Count) ?? 0;
            if (count > 2048) throw new ArgumentException("A region supports at most 2048 points across all contours.");
        }
        if (command == "add_adjustment")
        {
            string kind = arguments["kind"]!.GetValue<string>();
            string[] parameters = kind switch
            {
                "exposure" => ["exposure", "offset", "gamma"],
                "levels" => ["black", "white", "gamma"],
                "hue_saturation" => ["hue", "saturation", "lightness"],
                _ => ["temperature", "tint", "exposure", "contrast", "highlights", "shadows", "whites", "blacks", "texture", "clarity", "dehaze", "vibrance", "saturation"]
            };
            foreach (string key in arguments.Select(p => p.Key))
                if (key is not ("documentId" or "expectedRevision" or "kind" or "name") && !parameters.Contains(key))
                    throw new ArgumentException($"{key} is not a parameter of {kind}.");
            if (kind == "levels" && NumberValue(arguments, "white", 255) < NumberValue(arguments, "black") + 1)
                throw new ArgumentException("white must be at least black + 1.");
            if (kind == "photo_develop" && Math.Abs(NumberValue(arguments, "exposure")) > 5)
                throw new ArgumentException("photo_develop exposure must be between -5 and 5 EV.");
        }
    }

    static double NumberValue(JsonObject arguments, string key, double fallback = 0)
        => arguments[key] is { } value ? JsonSerializer.Deserialize<double>(value.ToJsonString()) : fallback;

    public static JsonArray Tools()
    {
        var result = new JsonArray();
        foreach (var command in Commands.Values)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            if (command.Name != "list_sessions")
            {
                properties["sessionId"] = Id.Schema();
                required.Add("sessionId");
            }
            foreach (var (name, field) in command.Fields) properties[name] = field.Schema();
            foreach (string name in command.Required) required.Add(name);
            result.Add(new JsonObject
            {
                ["name"] = "morupixel_" + command.Name, ["description"] = command.Description,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = command.ReadOnly, ["destructiveHint"] = !command.ReadOnly,
                    ["idempotentHint"] = command.ReadOnly, ["openWorldHint"] = false }
            });
        }
        return result;
    }
}
