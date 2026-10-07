using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Compositor.Windows;

/// <summary>One command/schema catalog shared by the local bridge, CLI and MCP transport.</summary>
public static partial class AutomationCatalog
{
    sealed record Field(string Type, string Description, double? Minimum = null, double? Maximum = null,
        int MaxLength = 0, bool EmptyAllowed = false, string[]? Choices = null, bool GuidValue = false, bool Color = false, string? ArrayShape = null, bool Ink = false, string? TextPattern = null)
    {
        /// <summary>Inside apply_batch an ID field may also name an earlier step's ref as "@name".</summary>
        public JsonObject BatchSchema()
        {
            var schema = Schema();
            if (!GuidValue) return schema;
            schema.Remove("maxLength"); schema.Remove("minLength");
            return new JsonObject
            {
                ["description"] = Description + " In apply_batch, \"@name\" refers to the object created or targeted by an earlier step with that ref.",
                ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "string", ["pattern"] = BatchRefPattern })
            };
        }

        public JsonObject Schema()
        {
            if (Type == "array") { var items = ArrayShape == null ? BatchStepsSchema() : ArrayShape == "ids" ? IdArraySchema() : MaterialArraySchema(ArrayShape); items["description"] = Description; return items; }
            if (Type == "object") { var shape = StyleParametersSchema(); shape["description"] = Description; return shape; }
            var schema = new JsonObject { ["type"] = Type, ["description"] = Description };
            if (Minimum is { } min) schema["minimum"] = min;
            if (Maximum is { } max) schema["maximum"] = max;
            if (MaxLength > 0) { schema["maxLength"] = MaxLength; schema["minLength"] = EmptyAllowed ? 0 : 1; }
            if (Choices != null) schema["enum"] = new JsonArray(Choices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            if (GuidValue) schema["format"] = "uuid";
            if (Color) schema["pattern"] = "^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|transparent)$";
            if (Ink) schema["pattern"] = InkPattern;
            if (TextPattern != null) schema["pattern"] = TextPattern;
            return schema;
        }
    }
    sealed record Command(string Name, string Description, bool ReadOnly, Dictionary<string, Field> Fields, string[] Required);
    static readonly Field Id = new("string", "GUID returned by Morupixel; do not invent an identifier.", MaxLength: 36, GuidValue: true);
    static readonly Field Name = new("string", "Display name.", MaxLength: 4096);
    static readonly Field Path = new("string", "Absolute Windows file path on the computer running Morupixel.", MaxLength: 32767);
    static readonly Field Color = new("string", "#RRGGBB, #AARRGGBB, or transparent.", MaxLength: 11, Color: true);
    // Alpha 00 is refused: an invisible ink is no pattern, and 0 is the stored "default" marker.
    const string InkPattern = "^(#[0-9a-fA-F]{6}|#(?!00)[0-9a-fA-F]{8}|default)$";
    static readonly Field InkColor = new("string", "Hatch pattern ink: #RRGGBB, #AARRGGBB with alpha 01-FF, or default (dark grey). Ignored for image materials.", MaxLength: 9, Ink: true);
    const string BackgroundPattern = "^(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|none)$";
    static readonly Field BackgroundColor = new("string", "Line pattern background painted inside the boundary under the lines: #RRGGBB, #AARRGGBB, or none (transparent, the default). An alpha of 00 also means none. Ignored for image materials.", MaxLength: 9, TextPattern: BackgroundPattern);
    // Built-in keys (brick, grass-sparse, dot-screen-30, …) or a user line pattern "custom:<32 hex>" from query_patterns.
    public const string PatternIdPattern = "^([a-z]+(-[a-z0-9]+)*|custom:[0-9a-f]{32})$";
    static readonly Field Coordinate = Number(-100_000, 100_000, "Position in parent-layer pixels; document pixels for root layers. See get_capabilities for coordinate conventions.");
    static readonly Dictionary<string, Command> Commands = CreateCommands();

    static Field Number(double min, double max, string description = "Numeric value.") => new("number", description, min, max);
    static Field Integer(int min, int max, string description = "Whole-number value.") => new("integer", description, min, max);
    static Field Bool(string description) => new("boolean", description);
    static Field IncludeLayers => new("boolean", "Response size for edits. Defaults to true (legacy: every open document with its full layer list). false returns only the target document's summary without layers.");
    static Field Choice(params string[] choices) => new("string", "One of the listed values (case-sensitive).", Choices: choices);
    static Dictionary<string, Field> Fields(params (string Key, Field Value)[] fields) => fields.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    static Dictionary<string, Command> CreateCommands()
    {
        var commands = new Dictionary<string, Command>(StringComparer.Ordinal);
        void Add(string name, string description, bool readOnly, Dictionary<string, Field> fields, params string[] required)
            => commands.Add(name, new(name, description, readOnly, fields, required));
        Dictionary<string, Field> Mutation(params (string Key, Field Value)[] fields)
        {
            var result = Fields(("documentId", Id), ("expectedRevision", Id), ("includeLayers", IncludeLayers));
            foreach (var (key, value) in fields) result.Add(key, value);
            return result;
        }
        string[] WriteRequired(params string[] fields) => new[] { "documentId", "expectedRevision" }.Concat(fields).ToArray();
        var textFields = Fields(("text", new("string", "Text content, including line breaks.", MaxLength: 100_000, EmptyAllowed: true)),
            ("fontFamily", new("string", "Installed font family name.", MaxLength: 256)), ("fontSize", Number(1, 1024)),
            ("color", Color), ("x", Coordinate), ("y", Coordinate), ("name", Name), ("bold", Bool("Bold text.")),
            ("italic", Bool("Italic text.")),
            ("alignment", new("string", "Left, Center, Right or Justify. Justify spreads the wrapped lines of a paragraph box (boxWidth) to both edges; the last line of each paragraph stays left.", Choices: ["Left", "Center", "Right", "Justify"])),
            ("lineHeight", Number(0, 8192, "Line height in pixels; zero uses natural spacing.")),
            ("tracking", Number(-200, 2000, "Tracking in 1/1000 em.")),
            ("boxWidth", Number(0, TextSpec.MaxBoxWidth, "Paragraph box width in pixels: lines wrap between words to fit it (Korean keeps whole words). 0 = no wrapping, one line per line break.")),
            ("outline", Bool("Letter outline around the glyphs. Omitted: true when another outline argument is given, otherwise the current value (false for new text).")),
            ("outlineWidth", Number(.5, TextSpec.MaxOutlineWidth, "Outline width in pixels; defaults to 4.")),
            ("outlineColor", Color with { Description = "Outline color: #RRGGBB, #AARRGGBB, or transparent; defaults to black." }),
            ("outlinePosition", new("string", "outside (default): the outline grows outward from the letter shapes; center: it straddles the letter edges.", Choices: ["outside", "center"])),
            ("outlineOnly", Bool("Hollow letters: draw only the outline, without the fill. Turns the outline on unless outline=false.")));
        Add("list_sessions", "List Morupixel windows where the user enabled AI control. Does not open or focus a window.", true, Fields());
        Add("get_state", "Inspect document identifiers, revisions and counts. Use includeLayers=false for a compact overview, then query_layers/get_layer for objects. The legacy default includes all layers.", true,
            Fields(("documentId", Id), ("includeLayers", Bool("Defaults to true for existing clients. Set false to omit the potentially large layer inventory."))));
        Add("get_capabilities", "Read the running editor's command contract, limits, coordinate conventions and unsupported operations before planning edits.", true, Fields());
        Add("query_layers", "Find layers by name, kind or parent without loading the whole drawing. Results use back-to-front storage order. For subsequent pages pass the returned revision as expectedRevision and nextOffset as offset.", true,
            Fields(("documentId", Id), ("expectedRevision", Id), ("parentId", Id), ("rootsOnly", Bool("Only root layers; cannot be combined with parentId.")),
                ("nameContains", new("string", "Case-insensitive literal substring of layer names, not a regular expression.", MaxLength: 256)),
                ("kind", Choice(Enum.GetNames<LayerKind>())), ("visible", Bool("Filter the layer's own visibility flag.")),
                ("category", new("string", "Effective workspace category, including inheritance from the root drawing/photo folder. Material layers (hatch fills, apply_material) are always Photo.", Choices: ["Drawing", "Photo"])),
                ("selectedOnly", Bool("Only selected layers; an inactive document reports its active layer as selected.")),
                ("locked", Bool("Filter the layer's own lock flag; inherited locks are reported separately.")),
                ("offset", Integer(0, Document.MaxNodes)), ("limit", Integer(1, 200, "Page size; defaults to 50."))), "documentId");
        Add("get_layer", "Inspect one exact layer, its parent chain, transforms, retained content and supported edits. Frame bounds describe the transformed surface, not ink or a fillable room boundary.", true,
            Fields(("documentId", Id), ("expectedRevision", Id), ("layerId", Id)), "documentId", "layerId");
        Add("new_document", "Create and activate a document (maximum 16,777,216 pixels). Returns its identifiers and revision.", false,
            Fields(("name", Name), ("width", Integer(1, 8192)), ("height", Integer(1, 8192)), ("background", Color), ("dpi", Number(1, 9600)), ("includeLayers", IncludeLayers)),
            "name", "width", "height");
        Add("inspect_file", "Read import choices of a local PDF/AI or DWG/DXF file without opening it: PDF page count, page size and layer count; CAD layouts plus each layer's detected cleanup role, object and hatch counts. Use the results as open_document page, cadLayout or cadLayerRoles.", true,
            Fields(("path", Path)), "path");
        Add("open_document", "Open a supported local file as a new document; existing unsaved documents remain open. A .moruproj file that is already open is activated instead and the result has alreadyOpen=true; other formats are imported again as a new document. PDF/AI accept page and dpi; DWG/DXF accept cadLayout (from inspect_file), cadLongEdge and cadStructure.", false,
            Fields(("path", Path), ("includeLayers", IncludeLayers),
                ("page", Integer(1, 100_000, "PDF/AI page number, 1-based; defaults to 1.")),
                ("dpi", Number(36, 600, "PDF/AI render resolution; defaults to 150.")),
                ("cadLayout", new("string", "CAD layout key or name from inspect_file; defaults to model space.", MaxLength: 256)),
                ("cadLongEdge", Integer(512, 8192, "Long edge in pixels of the CAD preview; defaults to 2400.")),
                ("cadStructure", Choice("objects", "layers", "combined")),
                ("separateLayers", Bool("PSD/PSB: import each layer separately instead of the composite image. Defaults to false.")),
                ("cadCleanup", Bool("DWG/DXF: apply drawing cleanup (role line weights, hatch materials). Defaults to false.")),
                ("cadLineWeights", Bool("With cadCleanup: heavier structure, lighter furniture and annotation. Defaults to true.")),
                ("cadHatches", Choice("suggest", "keep", "image", "pattern")),
                ("cadMaterialImage", new("string", "With cadHatches=image: absolute path of the material image used for every hatch.", MaxLength: 32767)),
                ("cadLayerRoles", new("array", "With cadCleanup: role overrides per CAD layer name, e.g. from inspect_file layers.", ArrayShape: "roles"))), "path");
        Add("activate_document", "Activate an existing document by its returned identifier before editing it.", false,
            Fields(("documentId", Id), ("includeLayers", IncludeLayers)), "documentId");
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
        var adjustments = Mutation(("kind", Choice("exposure", "levels", "hue_saturation", "photo_develop", "threshold", "halftone", "paper_texture", "glow")), ("name", Name),
            ("exposure", Number(-20, 20, "Exposure EV; photo_develop accepts -5 to 5.")), ("offset", Number(-.5, .5)),
            ("gamma", Number(.1, 9.99)), ("black", Number(0, 254)), ("white", Number(1, 255)), ("hue", Number(-360, 360)),
            ("saturation", Number(-100, 100)), ("lightness", Number(-100, 100)));
        foreach (var key in new[] { "temperature", "tint", "contrast", "highlights", "shadows", "whites", "blacks", "texture", "clarity", "dehaze", "vibrance" })
            adjustments.Add(key, Number(-100, 100));
        foreach (var (key, field) in StyleEffectFields()) adjustments.Add(key, field);
        Add("add_adjustment", "Add a reversible RGB8 adjustment layer. photo_develop is an image adjustment, not camera RAW decoding. threshold makes a black/white bitmap, halftone a printed dot screen, paper_texture paper/photocopy texture with rough edges, glow light that spreads from bright areas; their lengths are document pixels and their seeded patterns are deterministic. Supply only parameters for the chosen kind; omitted ones use the app's defaults.",
            false, adjustments, WriteRequired("kind"));
        Add("remove_background", "Create a layer mask using the bundled local AI model. Existing edits remain undoable.", false,
            Mutation(("layerId", Id)), WriteRequired("layerId"));
        Add("clean_sketch", "Turn a photographed hand sketch (a raster photo layer) into clean line art, like the app's sketch photo cleanup: find the sheet (or use corners), flatten its perspective, even out shadows and uneven light, and keep the dark pen and pencil lines as a transparent layer (lineColor=original keeps each pen's color) above an optional white layer, in a new group directly above the photo, which stays in the document hidden. If the photo is the document's only layer the canvas becomes the flattened sheet; otherwise the result is fitted into the photo's bounds. One undo step; can be included in apply_batch. Returns layerId (the new group), lineLayerId, backgroundLayerId, the corners used and the detection confidence.", false,
            Mutation(("layerId", Id with { Description = "The photo layer (kind Raster) holding the sketch photo." }),
                ("corners", new("array", "Sheet corners in the photo layer's own pixels, in the order top-left, top-right, bottom-right, bottom-left. Omit to find the sheet automatically.", ArrayShape: "corners")),
                ("flatten", Bool("Flatten the sheet's perspective (default true). false cleans the whole photo as it is and cannot be combined with corners.")),
                ("threshold", Number(0, 1, "Line threshold in paper-normalized darkness, 0 paper … 1 ink; lower keeps fainter lines. Omit for the automatic value measured from the photo.")),
                ("speckSize", Integer(0, 1_000_000, "Marks smaller than this many pixels (at the result's resolution) are removed; 0 keeps every mark. Omit for the automatic size that removes dust.")),
                ("boldness", Number(0, 1, "0..1: makes faint lines and soft edges more solid. Defaults to 0.")),
                ("lineColor", new("string", "original (default: each pen's own color), black, or #RRGGBB for one color.", MaxLength: 8, TextPattern: "^(original|black|#[0-9a-fA-F]{6})$")),
                ("background", new("string", "white (default) adds a white layer under the lines; none leaves the lines on transparency.", Choices: ["white", "none"])),
                ("name", Name with { Description = "Name of the new group; defaults to the app's own name for it." })), WriteRequired("layerId"));
        Add("save_project", "Save the active document to an absolute .moruproj path. Existing files require overwrite=true.", false,
            Mutation(("path", Path), ("overwrite", Bool("Defaults to false; true explicitly permits replacing the destination."))), WriteRequired("path"));
        Add("export_image", "Export the active document, one artboard or one selected layer to PNG, JPEG or TIFF, optionally scaled. Existing files require overwrite=true.", false,
            Mutation(("path", Path), ("quality", Integer(1, 100, "JPEG quality; defaults to 95.")),
                ("overwrite", Bool("Defaults to false; true explicitly permits replacing the destination.")), ("layerId", Id),
                ("artboardId", Id), ("scale", Number(.05, 8, "Output scale multiplier; 1 is actual size, 2 doubles pixels.")),
                ("keepTransparency", Bool("PNG/TIFF only; false fills transparent areas with white. Defaults to true."))), WriteRequired("path"));
        Add("export_document", "Export the active document or one artboard as PDF, .psd or PDF-compatible .ai through the same writers as the app's PDF/PSD/AI export dialog. layers=keep (default) makes one PDF layer per top-level layer or group (pdf, ai) or keeps the .psd layer tree; layers=flatten writes one page or one image layer. RGB at document resolution. Existing files require overwrite=true. Not part of apply_batch.", false,
            Mutation(("path", new("string", "Absolute Windows path whose extension matches format (.pdf, .psd or .ai).", MaxLength: 32767)),
                ("format", Choice(DocumentExportFormats)),
                ("layers", Choice("keep", "flatten")),
                ("vectors", Bool("format=pdf with layers=flatten only: keep lines, text and shapes as vectors (true) or place one image of the page (false). Defaults to the app dialog's suggestion: true when the document has text, shapes, drawings or materials.")),
                ("artboardId", Id),
                ("overwrite", Bool("Defaults to false; true explicitly permits replacing the destination."))), WriteRequired("path", "format"));
        Add("add_artboard", "Add an artboard in document pixels. The canvas grows to include it; a document without artboards first turns its whole canvas into one. Returns artboardId.", false,
            Mutation(("name", Name), ("x", Coordinate), ("y", Coordinate), ("width", Number(1, 16384)), ("height", Number(1, 16384))), WriteRequired("width", "height"));
        Add("update_artboard", "Rename, move or resize an existing artboard by artboardId from get_state.", false,
            Mutation(("artboardId", Id), ("name", Name), ("x", Coordinate), ("y", Coordinate), ("width", Number(1, 16384)), ("height", Number(1, 16384))), WriteRequired("artboardId"));
        Add("delete_artboard", "Delete an artboard; its layers stay on the canvas. The last artboard cannot be deleted.", false,
            Mutation(("artboardId", Id)), WriteRequired("artboardId"));
        Add("preview", "Render a scaled PNG preview without changing or exporting the document. maxSide defaults to 512.", true,
            Fields(("documentId", Id), ("maxSide", Integer(1, 1024))), "documentId");
        Add("undo", "Undo one document edit, rejecting a stale expectedRevision. Returns changed=false when there is nothing to undo.", false, Mutation(), WriteRequired());
        Add("redo", "Redo one document edit, rejecting a stale expectedRevision. Returns changed=false when there is nothing to redo.", false, Mutation(), WriteRequired());
        Add("apply_batch", "Validate and apply up to 64 ordered document edits atomically as one undo step. dryRun=true validates on a snapshot without editing. Reuse operationId only to retry the identical request; successful receipts are retained for the last 128 batches in this editor session. Does not write files or generate AI images.", false,
            Mutation(("operationId", new("string", "Caller-generated nonempty UUID for this logical batch. Use a new UUID for a new operation.", MaxLength: 36, GuidValue: true)),
                ("label", new("string", "Short undo history label describing the user's intent.", MaxLength: 120)),
                ("dryRun", Bool("Validate every step and document limit without committing; defaults to false.")),
                ("steps", new("array", "Ordered atomic edits; each command has its own strict argument schema."))), WriteRequired("operationId", "steps"));
        Add("register_material", "Register an existing local image in the document's embedded material library. kind=image (default) keeps it as a texture; kind=line_pattern turns its dark lines into a user line pattern (like the app's add-pattern-from-image command: light paper becomes transparent, lines take ink, line weight and background) with patternId custom:<id>. Does not generate images or use an AI provider account. Returns materialId; use query_materials after uncertain delivery instead of blindly retrying.", false,
            Mutation(("name", Name), ("path", Path), ("kind", Choice("image", "line_pattern")),
                ("source", new("string", "kind=image only: optional provenance label supplied by the caller, not a verified credential.", MaxLength: 4096)),
                ("tileable", Bool("kind=image only: caller-declared seamless texture; no seam correction is performed.")),
                ("threshold", Number(0, 1, "kind=line_pattern only: line detection threshold, 0 paper … 1 ink; defaults to the automatic threshold the app suggests.")),
                ("trim", Bool("kind=line_pattern only: crop blank margins around the drawing. Defaults to false.")),
                ("saveToMyPatterns", Bool("kind=line_pattern only: also add the pattern to the user's My patterns library on this PC. Defaults to false (document only)."))), WriteRequired("name", "path"));
        foreach (string query in new[] { "query_materials", "query_regions" })
            Add(query, "List document material assets or captured region templates in bounded pages. Pass expectedRevision for further pages. Region templates are snapshots, not semantic room detection.", true,
                Fields(("documentId", Id), ("expectedRevision", Id), ("nameContains", new("string", "Case-insensitive literal name substring.", MaxLength: 256)),
                    ("offset", Integer(0, MaterialEditing.MaxRegions)), ("limit", Integer(1, 50, "Defaults to 20."))), "documentId");
        Add("define_region", "Capture a closed 2D boundary in document pixels. source=polygon needs points and optional inner contours (even-odd fill); closed_layer needs layerId; selection uses the user's current 50% selection contour. Captures a reusable template, not a live link or inferred room.", false,
            Mutation(("name", Name), ("source", Choice("polygon", "closed_layer", "selection")), ("layerId", Id),
                ("points", new("array", "Closed outer contour, at least 3 points. Closure is automatic.", ArrayShape: "points")),
                ("holes", new("array", "Optional inner contours; at most 2048 points across all contours.", ArrayShape: "holes"))), WriteRequired("name", "source"));
        var pattern = Fields(("materialId", Id),
            ("patternId", new("string", "Pattern from query_patterns instead of materialId: a built-in key (" + string.Join(", ", HatchPatterns.All.Select(HatchPatterns.Key)) + ") or a user line pattern \"custom:<32 hex>\".", MaxLength: 39, TextPattern: PatternIdPattern)),
            ("tileWidth", Number(1, 100000, "One texture repeat width in layer-local pixels, not millimeters. Cannot be combined with scale.")),
            ("tileHeight", Number(1, 100000, "One texture repeat height in layer-local pixels. Cannot be combined with verticalRatio.")),
            ("scale", Number(.1, 10, "Repeat size relative to the material's default size for this document, like the app's size % / 100; 1 is the default size. Cannot be combined with tileWidth.")),
            ("verticalRatio", Number(.25, 4, "Vertical ratio like the app's vertical ratio % / 100; 1 keeps the material's own proportions. Patterns keep their marks and change vertical spacing only. Cannot be combined with tileHeight.")),
            ("angle", Number(-36000, 36000, "Pattern rotation in degrees around the local origin.")),
            ("offsetX", Coordinate), ("offsetY", Coordinate), ("name", Name), ("ink", InkColor),
            ("lineWeight", Number(.1, 8, "Hatch pattern line and dot weight multiplier; 1 is the default. Screentone dots and lines scale with it, so their ink coverage changes. Ignored for image materials, solid-black and the gradient screentones.")),
            ("background", BackgroundColor),
            ("gradientAngle", Number(-36000, 36000, "dot-gradient and stipple-gradient only: direction in degrees along which the density runs across the region, clockwise from left-to-right (0); 90 runs top to bottom (the default).")),
            ("gradientStart", Number(0, 1, "dot-gradient and stipple-gradient only: ink coverage 0-1 at the region's first edge along the direction (0.1 = 10%, the default).")),
            ("gradientEnd", Number(0, 1, "dot-gradient and stipple-gradient only: ink coverage 0-1 at the region's last edge along the direction (0.9 = 90%, the default).")),
            ("gradientSeed", Integer(0, ToneGradient.MaxSeed, "dot-gradient and stipple-gradient only: another random arrangement of the stipple's dots at the same density (default 0); kept but unused by dot-gradient.")),
            ("opacity", Number(0, 1)), ("blend", Choice(Enum.GetNames<BlendMode>())));
        Add("query_patterns", "List the built-in line hatch patterns (lawn, sand, pavers, brick, …) ordered for a surface, then the screentones (dot, line and grid screens by ink coverage, solid-black poché, and the dot-gradient and stipple-gradient fills whose density runs across the region), then the user's line patterns (My patterns library and those carried by the document) with patternId custom:<id>. Each entry has group (basic, screentone, custom), coverage (nominal ink coverage 0-1 of a uniform screentone) and gradient. No document is needed; with documentId (default: the active document) its own user patterns are included. apply_material registers a pattern automatically.", true,
            Fields(("documentId", Id), ("nameContains", new("string", "Case-insensitive literal substring of patternId, Korean name or display name.", MaxLength: 256)),
                ("surface", Choice("general", "wall", "floor", "ground"))));
        Add("apply_material", "Create an editable material layer from a registered image (materialId) or a built-in hatch pattern (patternId, or its materialId from query_patterns) inside a boundary: an existing regionId, or inline points/holes or a closed boundaryLayerId, stored as a new region template in the same step. Size with tileWidth/tileHeight in pixels or scale/verticalRatio relative to the default; omitted sizes use the app's default. dot-gradient and stipple-gradient take gradientAngle, gradientStart, gradientEnd and gradientSeed (density across the region). Original texture and vector boundary remain stored. Added above existing layers with Multiply by default to keep drawing lines visible. Returns layerId (and regionId); can be included in apply_batch.", false,
            Mutation(pattern.Select(p => (p.Key, p.Value)).Concat(new[] { ("regionId", Id),
                ("points", new Field("array", "Inline closed outer contour in document pixels instead of regionId, at least 3 points; stored as a polygon region template.", ArrayShape: "points")),
                ("holes", new Field("array", "Optional inner contours for points; at most 2048 points across all contours.", ArrayShape: "holes")),
                ("boundaryLayerId", Id with { Description = "Closed shape or closed CAD path layer used as the boundary instead of regionId, like define_region source=closed_layer." }),
                ("regionName", Name with { Description = "Name of the region template created from points or boundaryLayerId." }) }).ToArray()), WriteRequired());
        Add("query_styles", "List the design styles: styleId, Korean name and description, what each suits (drawing, photo or any) and its 1–3 parameters with type, range, default and choices. Also lists the style folders of a document (documentId, default the active document) with their groupId, styleId and parameters. No document is needed for the registry.", true,
            Fields(("documentId", Id)));
        Add("apply_style", "Apply a design style as one undo step. It adds an editable pass-through folder (named like folderName in query_styles) of adjustment, pattern fill, texture and text layers above what it reads; the original layers stay unchanged (the screentone plan hides the drawing's own hatch fills while it is on and shows them again when its folder is deleted). parameters maps a parameter key to a number (sliders 0–100), a boolean (toggles) or a choice key (or index) from query_styles; omitted keys use defaults. targetLayerIds limits what the style reads (default: the whole document, placed on top). groupId re-applies that style folder in place, keeping its ID, position, visibility and opacity, with new parameters or another styleId. Returns groupId. Remove a style with delete_layer on its groupId. The poster style cuts out the subject with the bundled local AI model.", false,
            Mutation(("styleId", Choice(DesignStyles.All.Select(style => style.Id).ToArray())),
                ("parameters", new("object", "Parameter key → value for the chosen style (see query_styles).")),
                ("targetLayerIds", new("array", "Layers the style reads (with their descendants); the folder goes right above the topmost of them. Omit for the whole document.", ArrayShape: "ids")),
                ("groupId", Id with { Description = "A style folder to re-apply in place (from query_styles or a previous apply_style)." })), WriteRequired("styleId"));
        Add("update_material", "Change the source material (a registered image by materialId, or a built-in hatch pattern by patternId) or repeat size (tileWidth/tileHeight or scale/verticalRatio), direction, offset, pattern ink, line weight, gradient (gradientAngle, gradientStart, gradientEnd, gradientSeed of dot-gradient and stipple-gradient), opacity and blend of an existing material layer, preserving its boundary and layer transform. Omitted fields keep their values. Can be included in apply_batch.", false,
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
                "object" => kind == JsonValueKind.Object,
                _ => kind == JsonValueKind.Number
            };
            if (!validType)
                throw new ArgumentException($"Invalid type for {key}: expected {field.Type}.");
            if (field.Type == "array")
            {
                if (field.ArrayShape == null) ValidateBatchSteps(value!.AsArray(), arguments);
                else if (field.ArrayShape == "ids") ValidateIdArray(value!.AsArray());
                else ValidateMaterialArray(value!.AsArray(), field.ArrayShape);
            }
            else if (field.Type is "boolean" or "object")
            {
                // Type validation above already guarantees a JSON boolean.
            }
            else if (field.Type == "string")
            {
                string text = value!.GetValue<string>();
                if (!field.EmptyAllowed && string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException($"{key} must not be empty or whitespace.");
                if (field.MaxLength > 0 && text.Length > field.MaxLength)
                    throw new ArgumentException($"{key} exceeds the maximum length of {field.MaxLength} characters.");
                if (field.Choices != null && !field.Choices.Contains(text, StringComparer.Ordinal)) throw new ArgumentException($"Invalid {key}: choose {string.Join(", ", field.Choices)}.");
                if (field.GuidValue && (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)) throw new ArgumentException($"{key} must be a nonempty GUID returned by Morupixel.");
                if (field.Color && !Regex.IsMatch(text, "^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|transparent)$", RegexOptions.CultureInvariant))
                    throw new ArgumentException($"Invalid {key}: use #RRGGBB, #AARRGGBB, or transparent.");
                if (field.TextPattern != null && !Regex.IsMatch(text, field.TextPattern, RegexOptions.CultureInvariant))
                    throw new ArgumentException($"Invalid {key}: {field.Description}");
                if (field.Ink && !Regex.IsMatch(text, InkPattern, RegexOptions.CultureInvariant))
                    throw new ArgumentException($"Invalid {key}: use #RRGGBB, #AARRGGBB with a nonzero alpha, or default.");
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
        if (command is "add_text" or "update_text" && NumberValue(arguments, "boxWidth") is > 0 and < 1)
            throw new ArgumentException("boxWidth must be 0 (no wrapping) or at least 1 pixel.");
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
        if (command is "apply_material" or "update_material") ValidateMaterialFields(command, arguments);
        if (command == "clean_sketch" && arguments.ContainsKey("corners") && arguments["flatten"]?.GetValue<bool>() == false)
            throw new ArgumentException("corners need flatten=true (the default); flatten=false keeps the whole photo.");
        if (command == "apply_style") ValidateStyleArguments(arguments);
        if (command == "register_material" && arguments["kind"]?.GetValue<string>() != "line_pattern" && new[] { "threshold", "trim", "saveToMyPatterns" }.Any(arguments.ContainsKey))
            throw new ArgumentException("threshold, trim and saveToMyPatterns apply to kind=line_pattern only.");
        if (command == "register_material" && arguments["kind"]?.GetValue<string>() == "line_pattern" && new[] { "source", "tileable" }.Any(arguments.ContainsKey))
            throw new ArgumentException("A line pattern is always seamless and marked as a line pattern; source and tileable apply to kind=image only.");
        if (command == "export_document")
        {
            string format = arguments["format"]!.GetValue<string>(), layers = arguments["layers"]?.GetValue<string>() ?? "keep";
            if (format == "ai" && layers != "keep") throw new ArgumentException(".ai export keeps layers; use format=pdf with layers=flatten for one flat page.");
            if (arguments.ContainsKey("vectors") && (format != "pdf" || layers != "flatten")) throw new ArgumentException("vectors applies to format=pdf with layers=flatten only.");
        }
        if (command == "add_adjustment")
        {
            string kind = arguments["kind"]!.GetValue<string>();
            string[] parameters = kind switch
            {
                "exposure" => ["exposure", "offset", "gamma"],
                "levels" => ["black", "white", "gamma"],
                "hue_saturation" => ["hue", "saturation", "lightness"],
                "threshold" or "halftone" or "paper_texture" or "glow" => StyleEffectParameters[kind],
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

    // Parameters of the design-style adjustment kinds (names unique within add_adjustment). A method, not a
    // field: the command table is built while the type initializes, before later static fields exist.
    static (string Key, Field Field)[] StyleEffectFields() =>
    [
        ("level", Number(0, 255, "threshold: 8-bit luminance where white begins; a gray of exactly this value is white. Default 128.")),
        ("smoothness", Number(0, 64, "threshold: soft transition width in luminance levels; 0 (default) is pure black and white.")),
        ("keepAlpha", Bool("threshold: keep the original transparency (default true); false also makes alpha 0 or 255 at 50%.")),
        ("cellSize", Number(2, 256, "halftone: screen period in document pixels. Default 8.")),
        ("angle", Number(-360, 360, "halftone: screen angle in degrees, clockwise. Default 45.")),
        ("dotShape", new("string", "halftone: round (default), line or square.", Choices: ["round", "line", "square"])),
        ("ink", new("string", "halftone: dot color #RRGGBB or #AARRGGBB (default #000000); transparent prints dots in the image's own colors.", MaxLength: 11, Color: true)),
        ("paper", new("string", "halftone: color between dots #RRGGBB or #AARRGGBB (default #FFFFFF); transparent keeps the image between dots.", MaxLength: 11, Color: true)),
        ("seed", Integer(0, int.MaxValue, "paper_texture: pattern number; the same number always draws the same texture. Default 1.")),
        ("textureSize", Number(.5, 32, "paper_texture: size of the finest paper grain in document pixels; fibres and specks scale with it. Default 2.")),
        ("paperTint", Number(0, 1, "paper_texture: how much white takes paperColor (0-1). Default 0.5.")),
        ("paperColor", new("string", "paper_texture: paper color #RRGGBB or #AARRGGBB. Default #F1EADA.", MaxLength: 11, Color: true)),
        ("grain", Number(0, 1, "paper_texture: paper tooth and cloudy density (0-1). Default 0.35.")),
        ("fibers", Number(0, 1, "paper_texture: paper fibres (0-1). Default 0.3.")),
        ("toner", Number(0, 1, "paper_texture: photocopy toner specks and dropouts (0-1). Default 0.")),
        ("streaks", Number(0, 1, "paper_texture: photocopy streaks (0-1). Default 0.")),
        ("edges", Number(0, 1, "paper_texture: rough, burned edges (0-1). Default 0.")),
        ("edgeWidth", Number(.01, .5, "paper_texture: edge reach as a fraction of the shorter page side. Default 0.08.")),
        ("edgeColor", new("string", "paper_texture: color the edges turn toward #RRGGBB or #AARRGGBB; dark burns, white fades. Default #2A1D12.", MaxLength: 11, Color: true)),
        ("threshold", Number(0, 1, "glow: brightness (0-1, strongest color channel) where light starts to glow. Default 0.7.")),
        ("radius", Number(1, 1000, "glow: how far light spreads in document pixels. Default 32.")),
        ("intensity", Number(0, 4, "glow: strength; 0 leaves the image unchanged. Default 1.")),
        ("glowColor", new("string", "glow: light color; #AARRGGBB alpha is how much the glow takes it (#RRGGBB = fully). Default transparent (each light's own color).", MaxLength: 11, Color: true)),
    ];
    static readonly Dictionary<string, string[]> StyleEffectParameters = new(StringComparer.Ordinal)
    {
        ["threshold"] = ["level", "smoothness", "keepAlpha"],
        ["halftone"] = ["cellSize", "angle", "dotShape", "ink", "paper"],
        ["paper_texture"] = ["seed", "textureSize", "paperTint", "paperColor", "grain", "fibers", "toner", "streaks", "edges", "edgeWidth", "edgeColor"],
        ["glow"] = ["threshold", "radius", "intensity", "glowColor"]
    };

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
