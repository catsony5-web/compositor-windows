# Source structure

The application remains a single .NET/WPF project with a portable distribution. Code is grouped by responsibility; existing projects remain readable. Preview 8 stores retained shape definitions and extended typography in the version 2 `.moruproj` document, so use Preview 8 or later when editing those properties.

| Directory | Responsibility |
| --- | --- |
| `src/App` | Entry point, application theme API and embedded starting sample |
| `src/Core` | Raster/layer/document state, history, text/adjustment/shape definitions and color harmonies |
| `src/Engine` | Compositing, selections, paint bucket, retouching, filters and local AI |
| `src/Formats` | Project persistence, image import/export, ICC/CMYK, PDF/PSD/CAD exchange and upstream package I/O |
| `src/UI` | Window layout, theme dictionary, menus/tabs, inspector, interaction, keyboard, tool options and rendering |
| `src/UI/Controls` | Canvas, layer rows, segmented workspace/proof controls, foreground/background swatches, HSV palette and character inspector |
| `src/UI/Dialogs` | Color, adjustment and export dialogs, plus the retained legacy text dialog |
| `src/Tests` | Offscreen command checks and image/format regression checks |
| `assets`, `models`, `licenses` | Brand/sample assets, pinned AI model and notices |
| `scripts` | Build, test and portable packaging entry points |
| `benchmarks/Interaction` | Reproducible offscreen interaction component timings and methodology |
| `tools/qa` | Optional QA runner source and project files, kept separate from generated results |
| `artifacts` | Generated test reports, benchmark results and storage audit records |
| `release` | Versioned portable applications, ZIPs and checksums; older validation output is retained |

Repository-wide `Directory.Build.props` defaults development and QA projects to Windows x64 and the installed .NET Desktop Runtime. `Publish.ps1` explicitly includes the runtime in portable packages. This avoids copying native ONNX libraries for other platforms into development output. See [maintenance and disk usage](MAINTENANCE.md) for safe background cleanup and the folder inventory.

## Editing and rendering rules

- Document and layer snapshots share immutable raster data. Pixel edits replace backing buffers; an in-progress brush owns a mutable buffer that must be detached before a background render reads it.
- One gesture creates one history entry. UI state changes such as brush diameter, zoom and tool selection do not create edits or remove redo history.
- Long operations work from a snapshot and check document identity, revision, selection and cancellation before committing. Switching documents must not publish a result into another tab.
- Render requests are coalesced during pointer movement. The final committed image is rendered through the same compositor used for export.
- A simple topmost text layer can use a cached background while moving. Masks, clipping, groups and non-normal blends continue through the compositor.
- Paint-bucket boundaries come from either the visible composite or the active raster layer. Only the selected layer changes. This is raster region fill, not Illustrator's vector Live Paint implementation.
- Keyboard routing is isolated from pointer routing. Text entry owns its keys before editor shortcuts can modify image pixels.

## Photo/design workspace and color controls

`UI/MainWindow.Modes.cs` and `Controls/GlassSwitch.cs` control the Photo editing / Design choice. The historical `GlassSwitch` class name is retained, but its presentation is a flat two-segment selector with both destination labels visible. Clicking a half explicitly selects that destination, including a no-op when already selected; keyboard interaction remains available. This is session UI state: both modes use the same document model and compositor, and both permit image, text and shape layers. Mode changes rebuild groups of the same live tool buttons, show corresponding shortcuts and reorder panel tabs without modifying the document or removing redo. Design hides the histogram; user docking locations remain intact. `MainWindow.WorkspaceActions.cs` supplies real photo and design commands. Canvas alignment maps document-space deltas into affine parent space and commits all supported selected layers in one transaction, rejecting locked/group/adjustment/warped-parent cases before editing. The separate RGB/CMYK selector controls the existing asynchronous ICC proof pipeline; it does not select a different editing engine or a four-channel storage format.

Typography uses `Theme.BodySize` (13), `CaptionSize` (12) and `HeadingSize` (14), with Segoe UI and Korean Malgun Gothic fallback. Numeric inputs use a 34 DIP minimum height rather than a restrictive fixed height. The Button template must preserve content-alignment bindings and wrap string content without replacing arbitrary visual content. The editable ComboBox's internal TextBox has no independent minimum height. `Theme.ActionRow` gives inspector commands a full-width, wrapping label, a separate chevron slot and a 38 DIP minimum height. Its `AutomationProperties.Name` uses the same complete command name; do not restore abbreviated source strings such as `이름…` to make rows fit.

`StudioPane`, inspector sections and shell chrome use flat neutral surfaces and restrained separators. `MainWindow.Docking.cs` owns the right column's default width of 396 DIP and limits of 324–520 DIP; a splitter exposes resizing without changing document pixels. Text and shape controls precede common appearance, transform and layer actions so the selected object's relevant settings stay near the top. Official references and the distinction between source facts and Morupixel's own choices are recorded in [DESIGN_REFERENCES.md](DESIGN_REFERENCES.md).

`tools/qa/panel-layout` renders live panes at real root DPI, reviews imposed glyph clips separately from normal scrolling, and preserves screenshots/JSON under artifacts. It also reads panel button text and accessibility names and flags literal `…`/`...` in command labels. User-authored layer names are excluded from that command-label rule. This semantic check catches labels already shortened in source, which geometric clipping checks alone miss. The Preview 11 matrix covers 100%/150% and representative 200% cases; 210 captures reported zero glyph-clipping or abbreviated-action-label issues in that bounded set.

`Controls/ColorPalettePanel.cs` supplies the modeless HSV controls and harmony chips. `SetColor(Color)` synchronizes external foreground state without emitting `ColorChanged`; input-originated changes emit the event once. Echo synchronization leaves latent hue and saturation intact at black instead of re-quantizing the drag position through 8-bit RGB. `Core/ColorHarmony.cs` produces complementary, analogous and triadic companions while retaining foreground alpha. Neutral bases remain selectable and receive useful colored companions. `MainWindow.Studio.cs` connects this control below the existing swatches. The shortcut focus guard excludes `SaturationValuePad`, so its arrow keys adjust color instead of nudging the selected layer.

`Core/ColorShadePalette.cs` generates the 9×7 selected-color grid: the center is the exact source color, neighboring columns vary hue, and rows mix toward white or black. Neutral sources remain neutral and alpha is retained. The palette panel separates the grid's base from the currently chosen cell, so trying tones does not regenerate the grid on each click. External foreground changes and the explicit base-reset action rebuild it; ordinary owner event echoes stay silent. Swatch buttons expose HEX values and positions to accessibility tools.

## Brush tips and photo development

`Core/BrushTip.cs` holds immutable built-in silhouettes and bounded image masks. `Formats/BrushTipStore.cs` accepts PNG/JPEG/BMP/TIFF up to 32 MiB, validates the source dimensions before copying pixels, and decodes a proportionally reduced tip with longest side at most 256px. Transparent images use alpha; fully opaque images use inverse luminance. Content-derived identities deduplicate shapes. Small normalized mask PNGs are atomically stored in `%LOCALAPPDATA%\Morupixel\Brushes` and restored independently of the project; painted strokes themselves are ordinary raster pixels. Loading is bounded to 256 preset files and skips invalid individual files.

`MainWindow.BrushTips.cs` connects tip selection/import, previews and the saved-tip picker; the studio adds rotation and stamp spacing. Brush/eraser and mask painting use the same silhouette and retain the existing single-gesture history transaction, selection coverage and immutable-source rules. Image-tip opacity comes from the imported mask rather than a separate hardness curve. Retouch tools retain their existing behavior. This is a monochrome stamp brush library, not an Adobe ABR importer or a persistent vector-stroke model.

`Core/PhotoDevelopSpec.cs` defines 13 immutable, finite and bounded parameters nested in `AdjustmentSpec.PhotoDevelop`; `AdjustmentKind.PhotoDevelop` is appended to retain earlier enum values. `Engine/PhotoDevelop.cs` performs relative white balance and exposure in linear sRGB, smooth luminance-weighted tonal changes, bounded veil adjustment and separate saturation/vibrance. Texture and clarity use alpha-weighted normalized local contrast at different spatial scales, sharing two float scratch planes. Work checks cancellation, skips hidden RGB, preserves alpha and never writes to the source raster; all-zero settings return an exact independent clone.

`AdjustmentDialog` groups light/color/detail sliders in a scrollable 340 DIP panel with reset-all and a before/after toggle outside the scrolling region. Existing generation/cancellation guards and the serialized render gate handle previews. `MainWindow` routes Filter → 사진 현상 and Ctrl+Shift+A through its normal adjustment-layer transaction, selection mask, re-edit and undo paths. Native version-2 projects serialize the complete specification and require Preview 13 or later to open this new adjustment kind. The upstream `.comp` exporter rejects the unsupported kind rather than discarding it. Composite exports render its appearance.

This pipeline is an RGB8 photo filter on decoded images, not camera RAW decoding or an implementation of Adobe Camera Raw's proprietary algorithms. Relative temperature is not Kelvin metadata, and existing document precision/size limits remain unchanged. `BrushTipTests` and `PhotoDevelopTests`, alongside palette tests, cover pixel behavior, persistence, bounds and relevant UI paths; release counts belong in `VALIDATION.md`.

## Text and retained geometry

`Controls/TextPropertiesPanel.cs` and `MainWindow.TextProperties.cs` are the primary text workflow. Selecting or creating text opens the dockable inspector. Its content editor owns plain Enter for newlines; Ctrl+Enter and the apply button commit the candidate `TextSpec`. Numeric Enter also commits, except while a list or IME composition owns confirmation. Inspector commits capture document identity, layer identity and inspector version. Candidates render on a snapshot before entering one undo transaction, and pending edits are checked before changing the selected target.

`TextSpec` stores content, font/style, pixel size, line height, tracking, alignment and color. Line height 0 keeps automatic line spacing; tracking is in thousandths of an em. Text masks resize onto the changed text surface without mutating old buffers. Existing warped text keeps its established destination placement. Canvas alignment uses transformed document bounds and accounts for affine parent groups; a projectively warped parent is explicitly excluded from exact alignment. The legacy modal text dialog remains available in code for compatibility and tests, but its old Enter shortcut help does not describe the new character inspector.

`Core/VectorShapes.cs` defines `ShapeSpec` for rectangles and ellipses and creates editable `LayerKind.Shape` layers. A retained shape has both a canonical definition and a same-size raster cache. The definition is authoritative; the cache supports thumbnails and later pixel editing. `Imaging.Composite` renders retained geometry directly at destination scale, then applies masks, opacity and blending. Editing geometry replaces the cache, resizes a mask onto the new dimensions, and adjusts local warp coordinates to keep all four destination corners fixed. Ordinary unwarped size edits retain the layer's X/Y, scale, rotation and flip properties, using the existing center-based affine transform.

`MainWindow.ShapeProperties.cs` exposes fill/stroke colors and enable states, stroke width, dimensions and rounded rectangle corners. `DocumentFeatures.Rasterize` clears shape metadata when a pixel tool converts the layer; undo snapshots retain the original specification. `ProjectStore` preserves the specification, validates decoded cache dimensions and rebuilds shape pixels from that specification on load. `.comp` export emits a raster representation and an explicit compatibility warning.

This geometry implementation does not include SVG, Bezier anchors, path boolean operations or a complete Figma/Illustrator document model. Group composition still uses a raster intermediate before applying the group's own transform; direct retained-shape scaling and scaling an entire group can therefore have different edge quality. Fixed-width text boxes, automatic wrapping and justified paragraphs are also outside this release.

## Selected-layer export

`Formats/SelectedLayerExport.cs` builds an isolated document snapshot from selected layer IDs. Selected groups expand to descendants before ancestor groups are added, so retaining an ancestor cannot accidentally export unselected siblings. Selected layers and their ancestors are made visible only in the snapshot. Mask, transform, opacity and group semantics are retained. Clipped layers whose original base is absent are detached and counted for the dialog's warning rather than being clipped against an unrelated sibling.

`UI/MainWindow.LayerExport.cs` presents cropped-content/full-canvas choices, PNG/JPEG/TIFF formats, JPEG quality and encoded previews. Rendering uses document coordinates and excludes off-canvas pixels. Cropping scans nonzero alpha and rejects an empty crop; full-canvas export may retain an empty transparent image. Format encoding retains document DPI, preserves transparency for PNG/TIFF and flattens JPEG to white. Preview generations and cancellation prevent outdated settings from enabling a stale save. Writes use the existing atomic output path and do not alter document history.

`ColorPaletteTests`, `VectorShapeTests`, `SelectedLayerExportTests`, text inspector tests and mixed-workspace tests cover these behaviors. Current source/package counts and interactive verification are maintained in `VALIDATION.md`.

## CAD source objects and large layer lists

Preview 21 adds `CadImportStructure` to compatibility options. Null preserves the former `SeparateLayers` behavior for existing callers; the dialog explicitly defaults to `Objects`. Each traversal occurrence has its own object identity, so repeated block inserts and paper-space viewports do not merge. Connected source polylines remain single objects; independent LINE entities are never joined by endpoint proximity. Dimension and hatch descendants share the source owner's identity. Contiguous source-layer runs form groups to preserve paint order across interleaved layers.

Individual object previews are tightly cropped. Source-layer groups use identity transforms and full document dimensions, sharing one transparent raster so moving a child does not clip it at its original bounds. Shared group pixel buffers count once toward the memory budget; ordinary image-layer accounting is unchanged. Version-4 native projects can reference an earlier group's pixel payload instead of encoding/decoding the same large empty canvas thousands of times. References must point backward to a group. `Document.MaxNodes` bounds total nodes to 32,768, while `MaxLayers` retains the 128 raster/adjustment ceiling and legacy format-import limits. Native metadata is bounded to 32MiB on both read and write; legacy `.comp` and layered PSD exports reject more than 128 nodes before writing.

`Controls/LayerList` uses recycling virtualization and creates rows/thumbnails only for the viewport. Hierarchy and depth are indexed once per list refresh, and magnetic snapping resolves parent transforms through an indexed lookup. New groups start collapsed, and an explicit child selection reveals ancestors without modifying document history. Cached movement may flatten full-canvas identity groups only in isolated preview snapshots; real parent IDs and source objects remain unchanged. The compositor likewise bypasses redundant full-canvas intermediates only for eligible normal-blend group subtrees. Transformed, masked, translucent or cropped groups retain isolated compositing.

## Parameter controls and quick adjustments

`UI/Controls/ParameterSlider.cs` combines numeric input, a gradient track and optional movement-step selection. Adjustment controls opt into continuous/0.01/0.1/1/5 steps and fine keyboard/Shift movement; ordinary brush controls retain their original input behavior. Step selection is interaction state, not document state. User movement snaps around zero with inclusive endpoints; model synchronization, exact numeric input and resets bypass the selected movement step. A separate minimum step keeps Gaussian radius in integer pixels. Raw drag accumulation prevents small pointer deltas from being lost when the displayed value is snapped.

`UI/Dialogs/ParameterDialog.cs` validates all fields and cross-field rules before returning accepted values. It owns no document; Apply returns values and Cancel leaves the caller's state alone. `MainWindow.AdjustmentControls.cs` defines the actual exposure/levels/saturation/blur menu dialogs and forwards accepted values to the existing raster job and undo path. `AdjustmentDialog` uses the same parameter controls for editable adjustment layers and photo development, retaining its asynchronous preview pipeline. Both dialog types reject invalid pending numeric edits on Apply. Offscreen QA uses the live quick-dialog factory, not duplicated field definitions.

## Working on the project

`Formats/CompatibilityImport.cs` dispatches bounded PDF/AI/PSD/PSB/DWG/DXF imports. PDF uses Windows.Data.Pdf; PSD/PSB uses the bounded managed `PsdReader` (RGB/gray 8-bit, raw/RLE/ZIP/prediction). ACadSharp parses CAD and `CadCompatibility` draws supported model-space objects into raster layers on an isolated STA. Conversion warnings accompany the candidate document. The dialog previews a candidate without committing it; cancel and failed decoding leave the active document unchanged. Opening adds a new native project tab, while placing imports one cached composite layer.

`PdfCompatibility.Write` creates a single-page image PDF with a soft alpha mask. `PhotoshopCompatibility.Write` creates RGB8 PSD with a stored composite and either one merged layer or basic pixel layers; transforms/text/shapes/masks are baked without modifying the source. Unsupported group/adjustment/clipping semantics disable layered output. Both run from snapshots through `ProjectStore.AtomicWrite`. Keep the supported subset and user-facing conversion notices aligned with [FILE_COMPATIBILITY.md](FILE_COMPATIBILITY.md).

The main project and auxiliary runners target `net8.0-windows10.0.19041.0` for desktop WinRT PDF APIs. Small pinned external PSD/DWG fixtures are embedded so packaged `--self-test` exercises the real parsers. Python/Pillow is only used by the optional independent QA script and is not shipped. `CompatibilityTests` and `tools/qa/compatibility` cover format data and offscreen import/export layouts respectively.

Use `scripts/Build.ps1`, `scripts/Test.ps1` and `scripts/Publish.ps1`. The project file remains at `src/Compositor.Windows.csproj`; .NET includes source subdirectories automatically. Tests run via `--self-test` without displaying an editor. `--render-preview` renders real WPF controls offscreen for visual checks. Keep previous portable release directories intact while a user may have one open.
