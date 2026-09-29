# Morupixel 0.2.0 Preview 37 — drawings, boards and presentation work

- **Large drawings move smoothly.** In the design workspace, panning and wheel zoom show the last sharp view immediately and redraw once the wheel or drag pauses; outdated redraws are dropped, only objects in view are drawn, and returning to a view you already saw is instant. Hovering and clicking objects in a drawing with ~19,000 objects no longer rebuilds the object index on every mouse move (hover about 46 ms → 1 ms, pan 7 ms → 1 ms, wheel step 9 ms → 3 ms on a synthetic 19,000-object plan).
- **Drawing import remembers your settings.** The import window opens with the last editing method, drawing cleanup, line weights, hatch treatment, material image, size, layout, layer roles and artboard choice. Choose or drop several drawings at once (for example the 2F, 3F and 4F plans) and the window opens once with "모든 파일에 같은 설정 적용"; a file that cannot be read can be skipped and the others still import. "다음부터 묻지 않고 이 설정으로 가져오기" imports DWG/DXF directly; hold Shift or use File › 도면 가져오기 설정 다시 묻기 to see the window again.
- **An artboard that fits the drawing.** Imports create an artboard around the drawing extents, the chosen layout sheet or the PDF page (option, on by default). A drawing placed into a board with artboards gets its own artboard beside them.
- **Hatch materials are edited on the photo layer tab.** Material (hatch fill) layers now appear on 사진 레이어 with opacity, blend, mask and material editing, while staying under the drawing lines they belong to. The AI connection reports them as `category: "Photo"`.
- **Shift+click on layer eyes.** Click one layer's eye, then Shift+click another: every layer listed between them takes the first one's visibility in one undo step. Alt+click still shows only one layer.
- **Materials for a selection.** With a selection (for example a magic wand pick with W on a plan), the Properties tab shows "선택 영역 재질": built-in materials and your own images, ordered for the surface under the selection. Clicking one turns the selection into an editable material layer below the drawing lines in one undo step; clicking another swaps it; "이미지로 재질 추가…" uses your own image.
- **Purpose profiles (사용 목적), starting with 건축학과.** Choose on the start screen, in 보기 › 사용 목적, the ribbon or the command palette. 기본 keeps the editor as before. 건축학과 switches to design mode, puts 선 정리 (re-apply role-based line weights to an imported drawing, show/hide dimensions and hatch materials) and 리터치 first, orders the tool rail for drawings and offers A1–A3 boards and 도면 가져오기 on the start screen.
- **Shadows.** Layer › 그림자 추가… creates a separate, editable shadow layer: 사실적인 그림자 (sharp at contact, softer farther away) or 그림자 형태 (a crisp fill or outline of the cast shape), as a drop shadow for cut-outs, a plan shadow for buildings on a site plan (height and sun angle) or a shadow laid on the floor, with 오전/정오/오후 presets. 원본에 맞춰 다시 만들기 rebuilds it after the source changes.
- **Before/after in photo develop.** Every adjustment window has 결과 · 좌우 분할 · 나란히 · 전후 전환 above the preview: drag a dividing bar, compare side by side, or hold `\` (₩) or the hold button to see the original.
- **Layered .psd and .ai, clearly named PDFs.** File › PDF · PSD · AI로 내보내기 offers PDF · 한 장으로 합치기 (인쇄·공유용), PDF · 레이어 나누기 (레이어별 켜고 끄기), .psd · 레이어 유지, .psd · 한 장으로 합치기 and .ai · 레이어 유지 (PDF 호환), each explained, with a summary of what is kept and what becomes pixels. Layered .psd keeps groups, Korean names, order, visibility, opacity, blend modes, clipping and masks; layered PDF and .ai make one switchable PDF layer per top-level layer or group and keep lines and text as vectors.
- **Layer order in .psd files is now standard.** Earlier versions wrote and read layered .psd files in reverse order, so other apps showed them upside down. Files written by Preview 35 and earlier are recognized and still open in their original order, with a note.
- **AI connection.** `apply_batch` steps can be named with `ref` and later steps can use `"@name"` in ID arguments (contract 6).
- **Checked before release.** An independent review of the combined work found 31 issues — among them a crash in layered .psd export on ordinary photos, group blend modes and clipped layers drawn wrong in PDF, a photo moving into a drawing folder when dropped next to a hatch row, selection materials offset inside a placed drawing, line cleanup touching only the last placed layer, and shadows including other shadows — all fixed with regression checks.
- **Validation.** 649 source checks passed. [Validation](VALIDATION.md) lists the details.

## Preview 36 — drawing cleanup no longer stops on thin hatches

- **Fixed: importing a DWG with drawing cleanup could fail with "면적이 있는 닫힌 영역을 지정하세요."** A thin hatch sliver (for example a brick hatch along a wall) was measured as having no area after being placed on its material layer, and that single hatch stopped the whole import. The area is now measured reliably, so these hatches get their material.
- **One bad hatch no longer blocks the import.** If a hatch still cannot be used, only that hatch is imported as boundary lines without a material, the other hatches keep theirs, and the import notice says how many were affected.
- **Validation.** 548 source checks passed, and two real site-plan drawings that failed before now import with cleanup. [Validation](VALIDATION.md) lists the details.

## Preview 35 — PSD layers, drawing cleanup and artboard batches for AI connection

- **PSD layers.** `open_document` with `separateLayers: true` imports each PSD/PSB layer instead of the composite image.
- **Drawing cleanup settings.** `cadCleanup: true` applies the same cleanup as the import window: role-based line weights, recommended hatch materials, boundaries only, or one material image. `cadLayerRoles` overrides the detected role of any CAD layer.
- **Layer roles before opening.** `inspect_file` now lists each CAD layer's detected role with object and hatch counts, and how many hatches match each material.
- **Artboards in one batch.** `apply_batch` accepts add, update and delete artboard steps together with layer edits; the whole batch is still one undo step and rolls back if any step fails.
- **Compatibility.** Command contract 5; existing commands and defaults are unchanged.
- **Validation.** 547 source checks passed. [Validation](VALIDATION.md) lists the details.

## Preview 34 — artboards and import options for AI connection

- **Artboards through AI connection.** New commands add, rename, move, resize and delete artboards; each step can be undone. A document without artboards turns its canvas into the first one.
- **Export options match the export window.** `export_image` can export one artboard, scale the output (0.05–8×) and fill transparency with white for PNG/TIFF. The result reports the output size.
- **Import options.** `inspect_file` lists PDF/AI pages and DWG/DXF layouts without opening the file. `open_document` accepts the PDF page and resolution, and the CAD layout, preview size and layer structure.
- **Recent colors are remembered.** The recent-color row and an opened tone grid are saved with the workspace and come back on the next launch. Your new-document presets were already saved per user.
- **Compatibility.** The command contract is now version 4 (33 commands). Existing commands and their defaults are unchanged.
- **Validation.** 545 source checks passed. [Validation](VALIDATION.md) lists the details.

## Preview 33 — clearer panels and dialogs

- **Export.** Choose PNG, JPEG or TIFF with one click; only the options for that format appear (JPEG quality, or keep a transparent background for PNG/TIFF). Scale 1x, 2x or a custom percentage, see the output size and estimated file size, type the file name before saving, and pick an artboard from a list next to a larger preview.
- **Color panel.** Large foreground/background colors, the saturation/value picker, HEX entry and the eyedropper come first, followed by a row of recently used colors and the swatches. The tone grid starts folded.
- **New document.** Sizes are grouped by purpose — Recent, Screen, SNS, Print and Photo. SNS sizes are named by ratio (1:1, 4:5, 9:16 …). A ratio preview follows the typed size. Recently created sizes are remembered, and the current size can be saved as your own preset (right-click to delete).
- **Brush panel.** The current brush and each preset are shown as real stroke samples that follow shape, size, hardness, angle and spacing. Shape tiles use the new icon set.
- **Layer panel.** Blend mode and opacity for the selected layer sit above the list (one undo step per change). An empty category explains what to do, and the footer adds group, mask and adjustment-layer buttons.
- **Tool options bar.** The current tool shows its icon and name; options such as auto select, contiguous and sample visible layers are icon toggles with explanations in the tooltip.
- **AI connection.** Edit commands accept `includeLayers: false` to return only the target document without its layer list (the default is unchanged). Undo/redo report `changed: false` when there is nothing to undo, reopening an open project reports `alreadyOpen: true`, and the MCP server version matches the app version. The connection settings add a connection test and example requests.
- **Small fixes.** The zoom entry hint names the real limit for the current mode (1600% photo, 6400% design). Labels that were assembled from fragments, such as paragraph alignment and selection feather/expand/contract titles, are now whole sentences in every language.
- **Validation.** 541 source checks passed. [Validation](VALIDATION.md) lists the details.

## Preview 32 — English, Japanese and Chinese

- **Four display languages.** View → 언어 · Language switches between 한국어, English, 日本語 and 简体中文 after a restart. Menus, the ribbon, panels, dialogs, notices, tooltips and the command palette follow the language, and the UI font matches the script. The first launch follows the Windows display language.
- **Your content stays yours.** Layer, document and file names are never translated. New documents and the learning sample use the selected language.
- **Clearer AI connection errors.** Blank text arguments, unknown material or region ids and missing files now return specific error codes and messages instead of misleading limits or raw system text.
- **Validation.** 525 source checks passed, including complete tables for every UI string with matching placeholders. Offscreen captures in all three languages were reviewed.

## Preview 31 — ribbon and screen fit

- **Ribbon view.** View → 리본 메뉴로 보기 turns the menus into tabs of titled icon groups. ^ folds the ribbon to its tabs. 내 탭 collects favorite commands: right-click any button to add or remove it, or to move it. The choice is remembered.
- **Fits small screens.** The main window, dialogs, floating panels and the command palette never open larger than the screen's work area or partly off screen. The minimum window size adapts to small or highly scaled displays.
- **Validation.** 521 source checks passed. [Validation](VALIDATION.md) lists the details.

## Preview 30 — drawing cleanup and layer isolation

- **Drawing cleanup on import.** DWG/DXF layers are sorted by name into structure/walls, openings, furniture, annotation, hatch and other. Walls import heavy and dark; furniture and dimensions light and thin. Each layer's role can be changed in the import dialog.
- **Hatch materials.** Recommended materials come first: concrete, brick, wood, tile, stone, insulation, gravel or diagonal, chosen from the hatch pattern and layer name. Hatches can also stay as boundaries, or use your own material image. Fills are editable material layers under the linework.
- **Layer range and isolation.** Shift+click selects a range of layers, Ctrl+click adds or removes one. Alt+click on a visibility icon shows only that layer, then hides only that layer, then restores the original visibility; each step can be undone.
- **Validation.** 518 source checks passed, including real DXF imports with walls, furniture and hatches. [Validation](VALIDATION.md) lists the details.

## Preview 29 — focused start screen and status-bar zoom

- **A calmer start.** Without a document, the start screen uses the whole window; the tool rail, side panels and tool options appear once a document opens, in the same arrangement as before.
- **Quick start sizes.** Square 1080×1080, portrait 4:5 1080×1350, wide 16:9 1920×1080 and A4 print (150 DPI, white background) create a document in one click.
- **Zoom in the status bar.** Step with −/+ or Ctrl+- / Ctrl++, type `150`, `150%` or `1.5x`, pick fit or 25–800% from the list, or use the fit button. Ctrl+0 fits and Ctrl+1 shows actual size.
- **View controls together.** The RGB / CMYK print preview moves from the title bar to the status bar beside the document size and zoom, leaving the title bar for menus, search, work mode and file actions.
- **Validation.** 513 source checks passed and the offscreen captures were reviewed. [Validation](VALIDATION.md) lists the details.

## Preview 28 — command palette, icon panels and saved workspace

- **Your workspace comes back.** Window size and position, the right panel width, photo/design mode, the last tab, docked, floating or pinned panels and folded sections are restored on the next launch. They are stored only in the user's local application data.
- **Find any command with Ctrl+K.** Search menus, tools and panels by name, Korean initial consonants (`ㅂㄹㅅ` → 브러시 도구) or shortcut. Recent commands appear first. The palette also opens from View → 명령 찾기… and the search button in the title bar.
- **Icon-led right panel.** Adjustment layers, creation, selection and masks, retouching, arrangement and layer actions use icon tiles and icon rows instead of plain text buttons, and canvas alignment is one row of icons. Every control keeps its complete name in the tooltip. Section titles fold their groups, and folded sections stay folded.
- **One icon family.** Toolbar tools, panel commands, layer visibility and lock, and notices are redrawn with consistent rounded strokes and subtle layered tints.
- **Cleaner inspector.** Fields share one 30 DIP row style with aligned columns, and the inspector opens with the layer's kind and name.
- **Optional histogram.** The histogram is hidden by default; turn it on from View → 히스토그램 표시.
- **Our own terms.** The photo layer tab is 사진 레이어, the surrounding-pixel retouch command is 주변으로 채우기, and file types are named by their extensions.
- **Validation.** 510 source checks passed, and 38 offscreen captures were reviewed. [Validation](VALIDATION.md) lists the details.

## Preview 27 — studio interface refresh

- **Layered studio layout.** Panels float as cards on a dark window base. Buttons sit above the panel surface and input fields below it, so controls are easier to tell apart; blue marks selection, focus and one primary action per screen. The menu moves into the title bar and tool options into one card below it.
- **More room at small windows.** Layer rows shrink from 60 to 40 DIP and parameter sliders from three rows to two. A 1280×720 window now shows the whole toolbar, both colors and the layer list.
- **Consistent dialogs and notices.** Every dialog shares one frame with a single primary action. Errors and notices use the app's dark notice window with selectable, copyable text instead of the system message box.
- **Start screen and canvas.** The start screen lists recently opened or saved documents, stored only in the user's local application data. The canvas adds a left ruler, and the status bar shows document size, DPI and the RGB/CMYK view.
- **Unchanged.** Editing behavior, shortcuts, document formats and panel width limits stay the same. Typography remains hinted Segoe UI and Malgun Gothic for sharp small text.
- **Validation.** 499 source checks passed, and 33 offscreen captures covering every dialog and three window sizes were reviewed. [Validation](VALIDATION.md) lists the details.

## Preview 26 — editable material mapping

- **Materials through MCP.** Register an existing image, capture a polygon, current selection or closed drawing object, and apply it as an editable 2D pattern. Inner holes remain empty. Change the source, repeat size, rotation and offset without flattening the boundary or original texture. Mapping and updates share the atomic batch and undo path.
- **Embedded originals.** Material sources, region templates and mapping settings persist in native project format 6. These projects require Preview 26 or later; ordinary projects retain their earlier format. Pixel coordinates do not infer physical CAD units, rooms or 3D UVs. Image generation remains the connected AI provider's responsibility.
- **Connect your AI program.** Connection settings offer copyable Codex and Claude Code commands, alongside common MCP JSON. The live contract is version 3 with 29 tools. [Connection setup](AI_CONNECTION.md) and [material workflow](MATERIAL_MAPPING.md) describe supported behavior.
- **Validation.** 495 source checks and 10 real executable/MCP scenarios passed using synthetic documents, including retained rendering, masks and holes, copying, save/reopen, malformed projects, atomic rollback and undo. The asynchronous selection test now holds worker completion explicitly so stale-state assertions are independent of machine speed.

## AI command foundation — earlier source changes

- **Inspectable editing tools.** The MCP catalog now has 23 tools. The running editor reports supported commands, coordinate conventions and limits. Compact state and paged object searches expose drawing/photo categories, source layer names, parent transforms, inherited locks and artboards without expanding every object.
- **Atomic edit plans.** Up to 64 document edits can be validated on a snapshot, then committed as one undo step. Failed, cancelled or stale plans leave no partial edits. Recent successful operation IDs prevent duplicate batch execution after an uncertain response, including after undo.
- **Provider-independent extension.** MCP and local CLI share the command contract and edit handlers. Image generation, material mapping and artboard editing via MCP remain future capabilities. [AI tool architecture](AI_TOOL_ARCHITECTURE.md) documents extension rules and replay limits.

## Morupixel 0.2.0 Preview 25 — drawing layers and artboards

- **Drawing and photo layers.** CAD and retained PDF/AI imports appear inside a collapsed drawing layer. Repeated CAD source layers share one expandable row while their objects remain separately editable and their paint order is preserved. Drawing and photo layer tabs separate the lists.
- **Directional object selection.** With the Move tool, drag from empty space: left to right selects fully enclosed objects; right to left selects touched objects. Retained CAD paths and shape edges are tested against their actual geometry. Shift adds, Alt subtracts, and Shift+Alt intersects. Large selections run in a cancellable worker and cannot overwrite a changed document or selection.
- **Artboards.** Shift+O opens artboard editing. Drag empty space to create, drag a board to move it, or use its handles and properties to resize it. Alt+drag creates inside an existing board. Board edits preserve object positions, support undo/redo, and persist in native projects. Export can choose a board. Imported drawing folders do not crop objects moved to another board.
- **Compatibility.** Native projects with drawing metadata or artboards use format version 5. Older projects still open; use Preview 25 or later for newly saved version 5 projects. Existing vector, photo, PDF/AI and privacy safeguards remain available.

## Morupixel 0.2.0 Preview 24 — publication privacy

- **Private-file safeguards.** Local environment values, personal agent configuration and credential files are excluded from Git and source archives. CI and portable packaging check tracked files and the final package before release.
- **Anonymous verification notes.** Public documentation uses anonymous test descriptions, without private input names or personal session details.
- **Portable builds.** Source paths use a stable virtual root and downloadable packages omit debugging symbols. Editor features and document formats are unchanged.

These controls apply to new source and packages; historical commits and previously downloaded files are not rewritten by this release. [Publication privacy](PUBLICATION_PRIVACY.md).

## Morupixel 0.2.0 Preview 23 — simpler file import

- **Clearer choices.** CAD opens with “편집 방식” and “부분별로 편집 (추천)”. Alternative choices keep source-layer editing or import the whole drawing together.
- **Less text up front.** Drawing layout, working size, DPI and vector retention are under “세부 설정”. Complete conversion notes and document counts are under “변환 안내”. Vector and saved PDF/AI layer retention remain enabled by default.
- **Preview and import.** Supported compatibility files generate a preview on opening, including PSD files. The “가져오기” action stays visible while scrolling details. Changed settings require a refreshed preview; failures stay visible and cannot import stale content.

Source self-tests passed **459/459**. Actual CAD and PDF-compatible AI dialogs were checked offscreen at normal and minimum sizes and 150% rendering scale. This version retains Preview 22's unified features and compatibility limits.

[Downloads](https://github.com/catsony5-web/compositor-windows/releases) · [File compatibility](FILE_COMPATIBILITY.md)

## Morupixel 0.2.0 Preview 22 — unified release

- **One shared version.** CAD object imports, pointer interaction, AI/MCP control, retained vector rendering and precise wand selection are included in one mainline package.
- **Sharp design zoom.** CAD paths, PDF/PDF-compatible AI sources, text and shapes render from retained content at the viewport resolution, with design zoom up to 6400%. Photos retain native pixels. Compatible CAD path runs share a render surface so object imports do not allocate a full viewport per object.
- **Precise magic wand.** Zoom-aware vector boundary sampling, connected/global selection, antialiased edges and a tolerance control. Selection masks and contours retain subpixel coordinates; calculations can be canceled and stale results cannot replace selection in another document.
- **CAD structure.** Choose individual objects in source-layer groups, one object per source layer, or a combined drawing. Includes model/paper space and supported external references, group-aware movement, magnetic alignment, virtualized layer rows and the 32,768-node budget.
- **Preserved sources.** Saved PDF optional-content layers and PDF-compatible AI artboards retain their source content. Native projects preserve vector data and object groups. Compatible documents can export a vector PDF; unsupported effects report limitations.
- **AI connection.** The 19 MCP/local-command tools, document revision checks and explicit per-session connection control are included.
- **Shared release workflow.** Feature branches merge through checked PRs. Mainline source and the portable package must pass tests before the immutable ZIP and SHA-256 are published. The website follows complete GitHub releases.

Existing bitmap imports need reimporting from the original drawing to regain vector/object structure. Non-PDF-compatible legacy AI, unsupported CAD entities, font substitution, and perspective/raster effects retain the documented compatibility limits. Existing open app windows keep their original build until reopened.

[Downloads](https://github.com/catsony5-web/compositor-windows/releases) · [Integration policy](INTEGRATION.md) · [File compatibility](FILE_COMPATIBILITY.md)

## Earlier development history

The separate local candidates below are historical; their supported features are included in Preview 22.

## Morupixel 0.2.0 Preview 21 — local candidate

- **CAD object import.** DWG/DXF import offers individual source objects, one object per CAD layer, or one combined drawing. Individual objects are the default in the import dialog. Closed polylines stay whole; independent LINE entities remain separate, even when endpoints touch.
- **Source layer groups.** Objects retain their source layer names as folders. Repeated blocks remain separate instances, and paper-space viewport clips are preserved. Interleaved layers use separate group runs to preserve drawing order. Save to `.moruproj` to keep this structure.
- **Working with many objects.** Up to 32,768 document nodes, within existing pixel/vector memory limits and the 128 raster/adjustment-layer cap. The layer panel creates visible rows on demand, reveals selected children, and initially collapses imported groups. Simple full-canvas CAD groups support cached individual-object movement.

Reimport an existing flattened drawing from its original DWG/DXF to use the new object mode. This does not reconstruct native objects from ordinary bitmap images, change PDF/PSD import granularity, or add DWG/DXF output. This local candidate has not been published to GitHub or the website.

## Morupixel 0.2.0 Preview 20 — local candidate

- **Selection cursor and preselection.** Move-tool idle/hover uses the selection arrow. A four-way cursor appears only after a drag starts; resize, rotation and pan retain their directional cursors. Hover outlines identify the prospective target without changing selection or undo history.
- **Thin-line acquisition.** Objects within four screen pixels are easier to select at any zoom. Nearby selection preserves masks, clipping, hierarchy and the center hit's occlusion boundary; a locked page background no longer prevents acquiring nearby CAD lines.
- **Magnetic alignment.** Drag a selection by its edges or center to other objects, the canvas or guides. Snap capture and release have separate thresholds to resist jitter. Temporary alignment guides show the attachment. Hold Alt to bypass snapping and Shift to constrain an axis.
- **Responsive movement.** Simple normal-blend single-layer moves cache the fixed layers below and above the moving object and update its display transform per pointer event. Complex compositing and large cache allocations retain the full compositor.

Includes the local CAD/PDF layer, vector-content and interface improvements, alongside Preview 19's MCP/local-command connection. Existing open application folders remain unchanged. This candidate has not been uploaded to the public website or GitHub release.

## Morupixel 0.2.0 Preview 19 — local AI candidate

- **AI editing connection.** Enable **AI 연결 → 로컬 연결 켜기** to control the running editor through MCP stdio or local JSON commands. No additional runtime or SDK is bundled.
- **19 tools.** Inspect documents, create/open work, add images/editable text/shapes, transform/reorder/delete layers, add adjustments, remove a background with the local model, preview, save/export, and undo/redo.
- **Concurrent work protection.** Commands check document identity and revision, respect locks and active dialogs, commit through undo history, and require explicit file overwrite. Connections are restricted to the current Windows account and can be turned off in the editor.
- **Background use.** An explicit headless host is available for command-line jobs. Normal startup remains empty with AI control off.

This candidate has not been uploaded to GitHub or the website. [Connection guide](AI_CONNECTION.md). The public download below is the earlier release.

## Morupixel 0.2.0 Preview 18

Larger images and projects in the same Windows workspace.

[Download for Windows x64](https://github.com/catsony5-web/compositor-windows/releases/download/v0.2.0-preview.18/Morupixel-0.2.0-preview.18-win-x64.zip) · [Website](https://morupixel.arch-t.chatgpt.site/)

- **Expanded image capacity.** Images and canvases can be up to **65,535px per side** and **536,870,897 total pixels (about 536.9MP)**, with an **8GiB combined layer-pixel and mask budget**. Both dimension and pixel limits apply.
- **Larger input files and projects.** PDF/AI, PSD/PSB and DWG/DXF input files can be up to 8GiB; native project PNG entries can be up to 4GiB per layer. Ordinary image opening has no separate encoded-file byte cap. PSB input uses the expanded raster limits; PSD output retains its 30,000px-per-side format limit.
- **Large-image handling.** Layer thumbnails use small preview buffers, large project images use temporary disk staging, and canvas fit and wheel zoom reach 0.1%. Size controls and help reflect the shared limits.

Actual usable size depends on available memory, the decoder and the operation: decoding, rendering and editing still need full-size buffers. The 128-layer ceiling and undo budget of up to 50 entries / 192MiB exclusively retained pixels remain. Large edits can exceed the undo budget and leave no undo entry. The maximum pixel ceiling was checked arithmetically, not benchmarked with a full-size allocation.

**Validation:** the Preview 18 source and local portable executable each passed **329/329 automated checks**. A **110MP (10,000 × 11,000)** image was opened through the editor, composited, rendered offscreen, saved as a native project and reopened with dimensions and edge pixels verified. [Validation details](https://github.com/catsony5-web/compositor-windows/blob/main/docs/VALIDATION.md)

Windows 10 version 2004 (build 19041) or later / Windows 11, x64. Extract the entire ZIP and run `Morupixel.exe`; .NET and the local AI model are included. AI background removal also requires the [Microsoft Visual C++ x64 runtime](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist).

Development preview · Unsigned · RGB 8-bit editing. No dependency changed. [File compatibility](https://github.com/catsony5-web/compositor-windows/blob/main/docs/FILE_COMPATIBILITY.md) · [User and developer guide](https://github.com/catsony5-web/compositor-windows/blob/main/docs/GUIDE.md) · [Complete version history](https://github.com/catsony5-web/compositor-windows/blob/main/docs/RELEASE_NOTES_ARCHIVE.md)

Based in part on [Compositor](https://github.com/robbietilton/Compositor) by Robbie Tilton / Wonder Assembly LLC. Independent project, MIT license. [Attribution](https://github.com/catsony5-web/compositor-windows/blob/main/NOTICE.md)
