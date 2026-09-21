# 0.2.0-preview.18 — Larger image and project capacity

Image and canvas limits increase to 65,535px per side and 536,870,897 total pixels (about 536.9MP), with an 8GiB combined layer-pixel and mask budget. Both dimension and pixel limits apply. PDF/AI, PSD/PSB and DWG/DXF input files can be up to 8GiB, and native project PNG entries can be up to 4GiB per layer. Ordinary image opening has no separate encoded-file byte cap. PSB input uses the larger raster limits; PSD output retains its 30,000px-per-side format limit.

Canvas/image size fields, shape dimension controls and help use the shared limits. Canvas fit and wheel zoom reach 0.1% so large images can fit the viewport. The 128-layer ceiling and the undo budget of up to 50 entries/192MiB exclusively retained pixels remain; a large edit can exceed that history budget. Decoding, rendering and editing still use full-size buffers, so actual usable size depends on available memory and the requested operation.

---

# 0.2.0-preview.17 — Save-before-close dialog

Closing a modified document now uses a themed Morupixel dialog instead of the Windows Yes/No message box. It shows the document name and explicit Cancel, Close Without Saving and Save Then Close actions, with readable spacing and a blue primary save button. The document name wraps, with scrolling available for exceptionally long names.

The dialog defaults to cancellation until an explicit choice is made. Escape and window dismissal return to editing; saving must succeed before closing is allowed. Cancelling the save dialog or a failed save keeps the document open. Document tabs and application exit use the same confirmation; clean documents close without a prompt. No dependency or editing algorithm changed.

---

# 0.2.0-preview.16 — Empty startup and explicit learning sample

The editor starts without an open document, sample image, canvas sheet or document tab. Three concise actions provide New Document, Open and Learn; Learn and the learning menu explicitly open the existing sea-window sample in a normal editable tab. Closing the final document returns to the same empty workspace instead of creating a replacement canvas.

Document-only menus, save/export controls and shortcuts are disabled while empty. Image open/import and clipboard image paste create a document at the image dimensions; subsequent layer import and paste keep their existing editing behavior. Startup file arguments open directly without an extra sample tab. Leaving the final document cancels queued work and clears its image, selection, guides, histogram and undo state; opening a document restores the controls.

The sample remains bundled and loads only on request. No runtime dependency was added. Regression and offscreen verification are recorded in [VALIDATION.md](VALIDATION.md).

---

# 0.2.0-preview.15 — Reduced interface copy

Removed persistent tutorial sentences from workspace sections, layer ordering, brush settings, color palettes, text properties and adjustment/new-document dialogs. Relevant controls retain on-demand tooltips. The idle status bar shows the active tool and mask state; processing and error messages remain visible. Text-property validation occupies space only when there is an error. The decorative Studio version badge is removed.

Names, complete command labels, values, ranges and units remain. Import/export notices are shortened while retaining conversion limits, transparency handling and appearance differences. Quick adjustment dialogs are shorter to match their reduced headings. Editing algorithms and dependencies are unchanged; existing regression and offscreen layout results are recorded in [VALIDATION.md](VALIDATION.md).

---

# 0.2.0-preview.14 — Fine adjustment sliders and selectable steps

The top Adjustment menu's exposure, levels, saturation and Gaussian blur dialogs now pair numeric entry with slider controls. Each control offers continuous movement or suitable 0.01, 0.1, 1 and 5-unit steps. The same step selector is available in adjustment-layer and photo-development dialogs. Stepped values align to multiples of the selected interval, while range endpoints remain reachable. Gaussian blur remains an integer-pixel radius.

Numeric entry remains available for exact values. Switching the movement interval does not itself change the adjustment. Invalid values block Apply, and levels additionally require white input to exceed black input by at least one. Quick adjustments retain their existing selected-layer pixel operation and undo behavior; adjustment-layer dialogs retain their previews and editable layer behavior. The shared controls add no runtime dependency. Verification and layout results are in [VALIDATION.md](VALIDATION.md).

---

# 0.2.0-preview.13 — Image brushes, selected-color tones and photo development

The brush panel offers round, square, diamond and star tips, plus PNG/JPEG/BMP/TIFF image import, tip rotation and stamp spacing. Transparent artwork uses alpha; opaque artwork uses inverse luminance and paints with the foreground color. Brush and eraser strokes, including mask painting, use the selected shape. Imports are limited to 32 MiB and the existing source-pixel limits, then reduced proportionally to a maximum 256px tip. Custom tips persist as small masks under `%LOCALAPPDATA%\Morupixel\Brushes`; painted strokes remain document pixels.

Below the swatches, a 9×7 tone grid places the selected color at its center, lighter tints above, darker shades below and related hues across columns. Neutral selections produce neutral steps. Selecting a grid cell retains its base for comparison; an explicit reset or an external color choice establishes a new base. The continuous HSV palette and harmony recommendations remain available.

**Filter → 사진 현상 / Ctrl+Shift+A** opens 13 grouped controls: relative temperature, tint, exposure, contrast, highlights, shadows, whites, blacks, texture, clarity, dehaze, vibrance and saturation. The dialog provides individual/all resets and a persistent before/after toggle. It creates an editable adjustment layer with selection masks and undo; `.moruproj` retains the complete settings for re-editing. Neutral settings preserve pixels exactly, with alpha and source buffers retained by non-neutral rendering.

Photo development processes decoded RGB8 images. It does not add camera RAW decoding or claim Adobe algorithm equivalence. Editable `.comp` export rejects this unsupported adjustment; native projects and composite image/PDF/PSD output retain the appropriate settings or rendered appearance. No new processing dependency or AI model was added. Actual verification results are recorded separately in [VALIDATION.md](VALIDATION.md).

---

# 0.2.0-preview.12 — Directional transform cursors

Hovering a selected layer's transform corners now shows diagonal resize cursors; side handles show horizontal/vertical cursors. Cursor directions follow visible geometry, including rotation, flips and parent transforms, without letting a wide or tall rectangle turn a corner cursor into an axis cursor. The rotation handle uses a hand cursor.

The same visible-handle and lock guards are used for cursor feedback and starting a transform. Active handle drags keep their cursor; panning and brush-size adjustment retain priority. WPF cursor queries derive the current state without changing document pixels or undo history, so no resize override is left behind after leaving a handle or switching tools.

---

# 0.2.0-preview.11 — Complete action names and a quieter workspace

Inspector commands such as layer renaming and detailed transforms now use complete labels in full-width action rows, with matching accessibility names. Text and shape properties appear before common appearance and transform settings. Body text remains 13 DIP, captions 12 DIP and section headings 14 DIP; numeric inputs have a 34 DIP minimum and action rows a 38 DIP minimum with room to wrap.

The shell uses flat neutral surfaces, restrained separators and a simpler tab treatment. The right panel can be resized from 324 to 520 DIP, starting at 396 DIP. Photo/design and RGB/CMYK proof controls show both destinations and select the clicked side explicitly. Existing mixed documents, editing functions and movable panels remain available.

The design is informed by official Figma UI3, Microsoft Fluent 2, Adobe Spectrum and Penpot references, with source access and design judgments documented in [DESIGN_REFERENCES.md](DESIGN_REFERENCES.md). Offscreen QA includes both glyph clipping and literal ellipses in action labels: 210 captures across 100%/150% panel matrices and representative 200% cases reported zero issues in those categories. This bounded check does not establish every possible document or display configuration.

---

# 0.2.0-preview.10 — Readable panels and focused workspaces

Right-hand and floating panels now share Segoe UI/맑은 고딕 typography: 13 DIP body text, 12 DIP captions and 14 DIP section headings. Inputs size to their text with 32–34 DIP minimum height. Panel headers, neutral backgrounds, section separators, reduced nested padding and wider default panes make controls easier to scan. Long labels wrap, deeply nested layer rows retain room for names, and editable font fields no longer inherit a conflicting inner minimum height. Button templates now honor content alignment instead of squeezing stretch content into a centered slot.

Photo editing groups crop/selection and retouch tools first, with adjustment, mask, background-removal and retouch shortcuts. Design prioritizes text, editable shapes, colors, alignment and grouping, and hides the histogram to leave more panel space. All 21 existing tools remain available in each mode; document content, RGB/CMYK proof, undo/redo and user-positioned panes are retained.

Design canvas alignment supports selected image, text and shape layers, including rotation and affine parent transforms, in one undo step. Group/adjustment targets and warped parents are excluded with an explanation. Arrangement actions validate the intended active/multiple selection, including newly created layers. These workspaces prioritize existing features; they do not add arbitrary Bezier-path editing or claim full Photoshop/Illustrator parity.

---

# 0.2.0-preview.9 — PDF, Photoshop and CAD image exchange

Open or drop PDF, PDF-compatible Illustrator AI, RGB/gray 8-bit PSD/PSB and AutoCAD DWG/DXF. The import dialog lets the user preview a selected PDF page at a chosen DPI, a stored Photoshop composite or supported basic pixel layers, and CAD model-space images with optional CAD-layer separation. Unsupported content is described before import. Placing a compatibility file into an existing document adds its preview as one composite image layer; opening it as a new document can retain supported layers.

The File menu adds single-page RGB image PDF and RGB8 PSD export. PSD can contain a composite layer or supported basic pixel layers; text, geometry, transforms and masks are baked into each exported pixel layer. Groups, adjustments and clipping require composite export. Source documents and native project history are preserved, and file output uses atomic replacement.

These paths do not preserve arbitrary AI vectors, PSD effects or CAD objects for native editing. PSD CMYK/16/32-bit input, DWG/DXF export, CAD layouts, Xrefs and measured plotting are not implemented. See [file compatibility](FILE_COMPATIBILITY.md) for the exact matrix and limits.

Windows 10 build 19041 or later is now required. PDF rendering uses the OS engine; ACadSharp is bundled for DWG/DXF. No Adobe, AutoCAD or external converter installation is required. The Windows SDK projection increases package size; no additional AI model was added. Embedded external fixtures and independent output checks bring the source suite to 254 passing tests.

---

# 0.2.0-preview.8 — Mixed photo and design workspace

The header has separate glass switches for **Photo editing / Design** and **RGB / CMYK proof**. Workspace mode changes the tool order and the preferred inspector tab while preserving the same mixed image/text/shape document, selection and undo history. CMYK remains an ICC-managed print proof and output workflow; changing workspace mode does not convert document pixels or launch an Adobe application.

The color panel adds a saturation/value gradient pad, a hue slider and foreground-based complementary, analogous and triadic palettes below the existing swatches. Recommendation chips show HEX values and apply their color to the foreground. The pad supports arrow keys and Shift for larger adjustments, retains latent hue through black, and does not emit recursive changes when the main foreground state synchronizes back to it.

Creating or selecting text opens the **Character / Paragraph inspector**, with content, font family, regular/bold/italic style, size, line spacing, tracking, color, paragraph alignment and canvas alignment. Content Enter inserts a newline; Ctrl+Enter or **Apply text** commits content and formatting. Enter in numeric fields applies, while open font lists and active Korean IME composition keep their normal confirmation behavior. Line spacing uses pixels (0 = automatic); tracking uses thousandths of an em (0 = default). Edits preserve masks and undo buffers, and document/layer checks reject stale inspector commits.

Rectangle and ellipse tools now create retained shape layers. Fill and stroke colors/enabled states, stroke width, dimensions and rectangle corner radius remain editable and are saved in `.moruproj`. Direct shape scaling samples the geometry at destination resolution. Shape edits resize masks without changing history buffers and preserve the document-space quadrilateral of warped shapes. Raster operations clear retained shape metadata when converting to pixels, and undo restores the editable shape. Project loading validates shape/cache dimensions and regenerates the cache from the saved shape definition.

**File → Export selected layers as image** combines one or more selected layers as PNG, JPEG or TIFF, with cropped content or full canvas bounds, a format preview, output dimensions, estimated file size and JPEG quality. PNG/TIFF retain transparency; JPEG uses white. Exports preserve document DPI, masks, transforms and required ancestor properties without including unselected siblings. Selected groups include visible descendants; selected layers are made visible for export. Missing clipping bases are explicitly reported and detached for the isolated export. Unselected backgrounds/adjustments and content outside the canvas are excluded, and the live document stays unchanged.

Long inspector labels and buttons wrap, field heights and floating panel defaults are larger, and long color/character panels scroll. These adjustments retain the previous dark captions, Korean glyph rendering, docking and layer drag behavior.

Retained geometry is limited to rectangles and ellipses. SVG, Bezier/path editing, path boolean operations and a complete Figma/Illustrator feature set are not included. Groups still composite their children into a raster surface before the group transform. Fixed-width text boxes, automatic paragraph wrapping and justification remain unsupported. `.comp` output rasterizes shapes with a warning; editable geometry and extended typography should be kept in `.moruproj`.

The final source and self-contained Windows executable both passed **236/236 automated checks**, including a fix for exporting a newly created layer when the previous selection set was stale. Desktop interaction results and the interrupted final recheck are recorded in [VALIDATION.md](VALIDATION.md).

---

# 0.2.0-preview.7 — Windows workspace update

Dark native window captions now match the editor and dialogs. Windows-hinted Malgun Gothic replaces the small UI font, with grayscale rendering and no bitmap shadow effect on text-bearing glass panels.

New documents prioritize the current Windows monitor, common display resolutions and A2–A5 paper. Pixel/mm inputs and DPI persist in project snapshots and files, RGB image output, and the CMYK export dialog. Print presets default to 150 DPI; existing image size limits remain enforced before allocation.

RGB and ICC CMYK proof are switchable from the header or Ctrl+Shift+Y. Proof renders asynchronously, rejects stale results, preserves RGB source/undo, disables incompatible fast text previews, and exports with the selected ICC profile. CMYK view is a print preview rather than four-channel document editing.

Adjustment, property, color, brush and layer panels can float, dock left/right, lock their header drag, and reset. Layout is session-local. Ordinary layer drag shows insertion feedback, scrolls near list edges, supports group centers, and rejects locked/cyclic destinations. Reordering participates in undo/redo.

# Morupixel 0.2.0-preview.6 · Glass Studio

The workspace now uses reflective dark panels, fine highlight borders, vector tool icons and a neutral image surround. The sidebar groups adjustment launchers, existing layer properties, color swatches and brush settings into four tabs; its content area contracts on shorter windows so layers stay reachable. All Preview 5 input, font, menu and layer improvements are retained.

A live RGB histogram uses alpha-weighted sampling of the composited image (up to approximately 65,536 samples). Adjustment dialogs combine this histogram with colored slider tracks, numeric entry and individual reset controls. Preset swatches set the actual foreground color; brush presets and diameter/hardness controls share the current tool settings. New documents have nine size presets, orientation exchange and transparent/white/black backgrounds, with existing raster limits enforced.

Glass styling is rendered inside the application and does not alter image pixels or apply OS backdrop blur. Existing color-range, RAW, pressure and stabilization limitations remain unchanged. Validation details are in VALIDATION.md.

---

# Morupixel 0.2.0-preview.5

The editor now uses embedded Pretendard Regular, Medium and SemiBold for UI text, with consistent dark input fields, menus, flyouts and dialogs. The font is distributed unmodified with its SIL Open Font License; no system font installation is required. Document text formatting is unchanged.

The top menu is organized into eight categories, with compositing and adjustment layers under Layer and CMYK output under File. The context toolbar names the active tool and keeps viewport controls in a fixed position. Document tabs show a close button, an unsaved-change indicator, and a clear selected underline; closing another document preserves the active document and viewport.

The right inspector groups blend mode, opacity, position and transform values. Numeric values apply on Enter or focus loss, invalid input is marked inline, and Escape restores the original input. Layer rows use separate eye and lock icons, larger names and compact metadata, with selection across the row. Groups, multiple selection and reordering keep their existing behavior. Long menus and the layer list scroll, including at the minimum 1200px window width.

See [validation](VALIDATION.md) for automated and offscreen UI coverage. This release does not replace or close already-running editor windows.

---

# Morupixel 0.2.0-preview.4

Alt+Delete and Ctrl+Delete now fill with foreground and background colors; the Backspace aliases remain available. Delete alone clears pixels. Fills respect selections, masks and locks, and unchanged pixels leave undo/redo history intact.

The new raster paint bucket (G) supports connected regions or matching colors across the image, tolerance, opacity, active-layer or visible-composite sampling, selections and transformed layers. Work runs in the background, can be canceled with Escape, and is discarded if the target document changes. Shift+G selects the gradient tool. The bucket fills pixels; it does not create Illustrator-style editable vector Live Paint groups.

The text editor applies with Enter or Ctrl+Enter, inserts a newline with Shift+Enter, and cancels with Escape. Composition-aware routing leaves Korean IME confirmation available before applying the dialog.

Repeated brush-size updates skip unchanged rounded values and reuse the diameter label. Pointer-driven render requests are coalesced. Moving a simple topmost text layer reuses its background while transforming the text preview, followed by the full compositor on release. Grouped, clipped, masked and other complex text continues through the compositor. These changes reduce repeated rendering work; they do not establish a universal frame-rate guarantee.

Source files are now grouped into App, Core, Engine, Formats, UI and Tests. Keyboard commands, pixel fills, bucket options, the text dialog and render scheduling have separate files. Project paths, document formats and packaging commands remain compatible. See [architecture](ARCHITECTURE.md) and [validation](VALIDATION.md).

---

# Morupixel 0.2.0-preview.3

The application icon, header logo, tools, selected layers and document tabs now use the website's powder blue and navy palette, with ivory interface text. Export and color dialogs use the same blue action buttons. The executable icon includes multiple Windows display sizes.

The starting sample is now an original seaside window image embedded in the app, so it is available offline without a loose image file. A hidden group contains an editable title and caption; enable the group to try typography. Existing user documents are not changed. The brush and color-picker improvements from preview 2 are retained.

---

# Morupixel 0.2.0-preview.2

Adds Alt + horizontal left/right-button drag to resize brush, eraser and retouch tools from 1 to 1,000px with a live diameter overlay. Escape, loss of capture, tool changes and window deactivation cancel the resize. Resizing does not paint, dirty the document or discard redo. Clone/heal Alt+click still sets a source; dragging preserves it.

The tool rail now has overlapping foreground/background swatches, real X color exchange and D black/white defaults. The color picker includes an HSV square, hue strip, RGB/alpha/HEX inputs and previous-color restore. Background fill (Ctrl+Backspace), background eyedropper (Alt+click), and a foreground-to-background gradient mode use the second color. The existing foreground-to-transparent gradient remains the default. Options are shown for the selected tool.

Seven new automated checks cover size changes/cancellation, clone sampling, selection-tool isolation, color exchange/fill, alpha-correct gradients and HSV conversion. See [validation](VALIDATION.md) for the results and interactive coverage.

---

# Morupixel 0.2.0-preview.1

The application now has an independent product name: **Morupixel / 모루픽셀**. Run `Morupixel.exe`; save editable work as `.moruproj`. Old `.cwproj` files remain readable. The upstream Compositor MIT attribution is retained.

This update adds document tabs, grouped layers and clipping masks, 14 blend modes, nonuniform/projective transforms, editable text, non-destructive adjustment layers, advanced selections and retouch tools, additional filters, local AI background removal, image I/O improvements, and restricted upstream `.comp` interoperability.

AI background removal works offline using the bundled 4.6MB U²-NetP model and Microsoft ONNX Runtime CPU. No image upload or subscription is used. EXIF orientation and embedded ICC-to-sRGB conversion are handled on import; JPEG quality preview and TIFF export are available. The separate print dialog adds ICC-managed CMYK TIFF output, DPI selection and a profile-conversion preview; the native editing workspace remains sRGB.

This is a development preview, not a claim of complete Compositor parity. Fine text layout, live/unlinked masks, pass-through group compositing, vector shape metadata, range-specific HSV adjustments, layer effects, PSD and native high-bit-depth/CMYK editing workflows remain incomplete or unsupported. The lightweight segmentation model has limitations on fine and transparent edges. Package interoperability is intentionally strict about unsupported semantics. See [the complete matrix](PORTING.md).

The source and self-contained published executable each passed **149/149 automated checks**, including native ONNX inference, project round trips, CMYK profile output and offscreen editor transactions. The WPF screen was rendered offscreen and visually inspected. This is separate from live mouse/keyboard testing and does not establish full upstream quality parity. See [validation details](VALIDATION.md).

Extract the complete portable ZIP before running. The distribution includes .NET, ONNX Runtime, the model, and applicable licenses. No GitHub publication or repository rename is implied by a local build.

AI inference also requires the system Microsoft Visual C++ x64 runtime, which is not bundled. A missing-runtime error includes the official installation link; see [AI setup](BACKGROUND_REMOVAL.md).
