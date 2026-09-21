# Morupixel 0.2.0-preview.20 local candidate — 2026-09-22

The Release source build has zero warnings/errors and **400/400** checks pass. The source combines the existing CAD/PDF/vector/interface work with the AI connection before adding pointer interaction. New tests cover four-DIP thin-line picking above a locked background, occlusion and hierarchy, zoom-independent snapping, six-DIP capture/ten-DIP release hysteresis, multi-selection, transformed parents, Shift/Alt behavior, cursor priorities, hover/history isolation, cache compositing order and cancellation. Source evidence: `artifacts/pointer/self-tests-final.txt`.

Actual CanvasView renders of hover and magnetic movement were reviewed: `artifacts/pointer/pointer-feedback/hover.png` and `magnetic-move.png`. Offscreen move-preview pixel checks verify the original position is cleared, the moving layer follows its transform, and the fixed foreground still occludes it. Rendering tests use interior pixel samples so normal interpolation at enlarged boundaries is not mistaken for incorrect stacking.

A separate benchmark used 93 sparse line layers plus a locked white background, 200 warmups and 1,000 queries per case at 0.5×, 1× and 4× zoom. Mixed-case picking averaged **0.323 / 0.202 / 0.187 ms**, with **0.676 / 0.279 / 0.233 ms p95** on this PC. Empty-area full-probe cases averaged about 0.2 ms. This measures target acquisition, not a whole-frame FPS guarantee. Evidence: `artifacts/pointer-pick-benchmark/results.json`.

The fast move display is limited to one unmasked, unwarped root layer in a simple normal-blend stack with a bounded cache allocation; interdependent groups, adjustments, clipping, CMYK preview and larger cache requirements use the existing accurate compositor. No operating-system pointer warping or simulated desktop input is used. All verification was offscreen/background, preserving the user's running editors.

---

# Morupixel 0.2.0-preview.19 local candidate — 2026-09-22

The Release source build has zero warnings and errors; **357/357** self-tests pass. Added coverage exercises real current-user named pipes, bounded UTF-8 messages, session lifecycle, disconnect/stop cancellation, CLI JSON output, both supported MCP protocol versions, typed command schemas, and actual offscreen editor transactions. Document revisions, inactive tabs, locked parents, native modal-window disabling, no-op text/history preservation, file overwrite protection, and cancelled requests are covered. Source report: `artifacts/automation/self-tests-final.txt`.

`tools/qa/automation-smoke.cjs` starts only its own hidden editor host and stdio MCP process. The executable integration run exercised all 19 tools and the non-MCP CLI: created an editable Korean composition, placed a photo, transformed/reordered layers, added an adjustment, checked undo/redo and stale revisions, returned an MCP PNG image block, saved a native project, exported the composition and a text layer, rejected an existing output, switched documents, and generated a background-removal mask with the bundled local model. Evidence: `artifacts/automation/smoke-04/result.json` and `ai-edit-preview.png`. The resulting preview was visually reviewed.

The portable packaging script also runs the self-test suite on its output before making the ZIP. This candidate adds no dependency and has not been published remotely. Existing applications, open documents, and portable releases were left intact. The MCP adapter is a local stdio server, not a built-in chatbot or an Adobe application bridge. [Connection scope and setup](AI_CONNECTION.md).

---

# Morupixel 0.2.0-preview.18 validation — 2026-09-21

The Release source build has zero warnings and errors, and **329/329** self-tests pass. New coverage includes a WIC-authored 10,000 x 2,000 PNG import/export/native-project round trip with exact pixel samples, a 480 MB logical layer budget, integer-overflow rejection before allocation, 65,535 x 2 and 2 x 65,535 PNG decode and offscreen WPF rendering, bounded thumbnails, small fit/zoom, and PSD format-specific export preflight. Existing A2/300 DPI and 9,000px assumptions were updated to the expanded limits. Source report: `artifacts/test-results/preview18-source-self-test.txt`. The publish script independently runs the suite against the portable executable before creating its ZIP.

An opt-in **10,000 x 11,000 (110 MP)** check opened the image through `MainWindow.OpenImage`, composited all pixels, rendered the editor offscreen, saved a native project using disk-backed staging, and reopened it with dimensions and edge pixels intact. The final run completed in 4.4 seconds with approximately 2,643 MiB peak working set on this machine. Evidence: `artifacts/large-image-preview18/large-image-qa.txt` and `110mp-editor.png`; runner: `tools/qa/large-image`. The editor capture was visually reviewed, including the adaptive ruler spacing.

The 536,870,897-pixel ceiling is validated arithmetically against `Array.MaxLength / 4`; a full image at that ceiling was not allocated or benchmarked. Actual capacity and processing time depend on memory, decoder and operation. Undo retains its 192 MiB budget. No dependency was changed. All checks were headless or offscreen; existing portable releases and user sessions were preserved.

---

# Morupixel 0.2.0-preview.17 validation — 2026-09-21

The source suite passes **321/321** checks. Two focused close-confirmation regressions exercise the actual three button click events, default/cancel keyboard configuration, cancellation on unchosen window dismissal, clean/empty-document bypass, the current document name, dirty-state and history preservation, cancelled/unsuccessful save rejection, and a successful real project write before close is allowed. Test seams supply decisions and a false save outcome without opening native dialogs; the successful save is independently loaded from disk and its edited value verified.

The new confirmation was rendered offscreen in **12 layouts**: short and long Korean document names, 560/520 DIP widths, and 100%/150%/200% DPI. Final results contain **zero glyph/action-label issues, zero abbreviated action labels and zero blank captures**. An initial one-pixel Korean glyph overhang into the name scroll area's edge was fixed with an inner text margin. The compact normal dialog and narrow long-name 150% capture were visually reviewed. The filename area wraps and scrolls at its height limit; footer buttons remain outside it. Reports: `artifacts/qa/preview17-dialogs/layout-report.json`; actual preview: `docs/screenshots/save-changes.png`.

Builds completed with zero errors and the existing NU1900 warning for unavailable NuGet vulnerability metadata. No dependency changed. Publishing repeats all checks against the self-contained EXE; reports are retained as `artifacts/test-results/preview17-*-self-test.txt`. Preview captures include the dialog's client area and complete background; native Windows caption rendering, physical key input and native save-picker cancellation were not exercised. No user application was activated or closed, and older portable versions were preserved.

---

# Morupixel 0.2.0-preview.16 validation — 2026-09-21

The source suite passes **319/319** checks. Three startup regression cases exercise the real initialization path without showing a window: empty construction/startup and document shortcuts, explicit learning sample followed by final-tab close and reopen, and opening generated image/project fixtures via normal, startup-path and import routes. They also verify empty-workspace clipboard raster placement, subsequent undoable paste, restored command availability, cleared selection/guides/histogram/history, cancellation generations/timers and rejection of new empty-workspace render requests. The existing final-tab-close case now expects zero tabs; the Studio fixture creates its own small image instead of relying on an implicit sample.

Offscreen QA captured **271 actual WPF panel/dialog layouts** with **zero glyph/action-label issues, zero abbreviated action labels and zero blank captures**. The existing dock/floating, narrow/default width and 100%/150%/representative 200% DPI matrix remains passing. The additional startup screenshot shows the actual editor without an image, checkerboard sheet, document tab or histogram. Normal editor previews now explicitly open the learning sample. Reports: `artifacts/qa/preview16-panels/layout-report.json`; actual controls: `artifacts/qa/preview16-studio` and `docs/screenshots`.

Builds completed with zero errors and the existing NU1900 warning for unavailable NuGet vulnerability metadata. No dependency was added. Publishing repeats the complete suite against the self-contained EXE before creating the archive. Source and package reports are retained under `artifacts/test-results/preview16-*-self-test.txt`. All verification was headless or offscreen, with no user window activated, closed or overwritten. Native open/save dialogs, clipboard access and physical desktop input were not exercised.

---

# Morupixel 0.2.0-preview.15 validation — 2026-09-21

The source suite passes **316/316** checks (22:18 KST). This presentation-only change removes persistent supplementary text and moves relevant instructions into tooltips. The existing photo-development checkbox assertion now checks its concise caption and before/after tooltip; editing, validation, controls, history and format checks remain passing. No new product tests or dependencies were added.

Offscreen QA captured **271 actual WPF layouts** with **zero glyph/action-label issues, zero abbreviated action labels and zero blank captures**. Coverage includes both workspaces, all panels, minimum/default dock and floating widths, 100%/150% DPI and representative 200% cases, long Korean names, quick adjustment dialogs and scrolled content. The full design workspace, brush controls, color palette and shortened exposure dialog were visually reviewed. Reports: `artifacts/qa/preview15-panels/layout-report.json`; representative screenshots: `artifacts/qa/preview15-studio` and `docs/screenshots`.

An initial layout pass produced 424 false positives from layer text outside its scroll viewport at 150% DPI. The detector previously exempted the scroll presenter but then compared the original offscreen ink against an ancestor Grid clip. It now carries only vertically visible ink into ancestor checks, while preserving local control and horizontal clipping checks. Nested clipped-parent scroll calibration covers both scroll positions; deliberate clipping, abbreviated labels and blank captures remain detectable. No product layout change was needed for these reports.

Builds completed with zero errors and the existing NU1900 warning for unavailable NuGet vulnerability metadata. Publishing repeats the full suite on the self-contained EXE before creating the ZIP; source and packaged reports are retained as `artifacts/test-results/preview15-*-self-test.txt`. All checks ran headless or offscreen. Existing editor windows and previous portable releases were preserved; no desktop mouse or keyboard interaction was used.

---

# Morupixel 0.2.0-preview.14 validation — 2026-09-21

The source suite passes **316/316** checks (22:09 KST). Nineteen added cases cover continuous and zero-anchored stepped movement, positive/negative ranges, inclusive endpoints, exact typed values, mode switching without value changes, fine arrows and Shift movement, accumulated pointer deltas, integer-radius constraints, invalid input, resets and notification counts. A real routed Thumb drag sequence exercises the precision slider's class event path without a native window or desktop mouse input.

Quick-dialog tests exercise actual slider and text controls, Apply rejection, Cancel, cross-field levels validation, immutable accepted values and the same factory used by the four live menu commands. Integration tests for Levels and Photo Develop confirm that step changes reach the adjustment spec and that editing one parameter does not erase another pending numeric edit. Existing brush controls, preview processing, editing, formats and history checks remain passing.

Offscreen QA captured **271 actual WPF layouts**, recording **zero glyph/action-label issues, zero abbreviated action labels and zero blank captures**. This includes the previous complete panel matrix, quick exposure/levels/saturation/blur at 400/480 DIP and 100%/150% DPI, displayed 5-unit choices, and existing adjustment dialogs at minimum size with both scroll positions. Quick exposure/levels, narrow 150% step controls and photo development were visually reviewed. Reports: `artifacts/qa/preview14-panels/layout-report.json`; representative actual controls: `artifacts/qa/preview14-studio` and `docs/screenshots`.

The build has zero errors and the existing NU1900 warning for unavailable NuGet vulnerability metadata. No dependency changed. Publishing repeats the full suite on the self-contained EXE before creating the ZIP. Source and packaged reports are retained under `artifacts/test-results/preview14-*-self-test.txt`. No existing editor was activated, closed or overwritten; all checks were headless or offscreen. Physical mouse/keyboard operation on the user's desktop was not exercised.

---

# Morupixel 0.2.0-preview.13 validation — 2026-09-21

The source suite passes **297/297** checks (21:52 KST). New cases cover built-in and imported brush silhouettes, rotation, event-independent stamp spacing, whole-stroke opacity, immutable pixels, erasing, masks, selection bounds, image normalization and preset persistence. An actual button/combobox/slider integration case confirms that the chosen settings reach the same stroke constructor used by mouse input without dirtying the document or discarding redo. The importer tests use artifact folders, not the user's saved brushes.

The 9×7 tone palette checks exact base-color and alpha retention, hue wrapping, light-to-dark ordering, neutral colors, stable chip selection, external foreground changes, keyboard accessibility and narrow-panel bounds. Fifteen photo-development checks exercise every parameter, neutral identity, transparency, selective tonal changes, local contrast, cancellation, invalid inputs, deterministic output, real dialog controls/reset, selection masks, re-editing, undo/redo and project round-trip. This is RGB8 image processing; neither camera RAW decoding nor numerical parity with Adobe Camera Raw is claimed.

Panel QA captured **239 actual WPF layouts**, including minimum/default docks, floating panels, both workspaces, 100%/150% DPI and representative 200% cases. Photo development was checked at 1040×760 and minimum 860×580, at both ends of the scroll area; a long Korean custom-brush name was checked at 324 DIP. The report records **zero glyph/action-label issues, zero abbreviated action labels and zero blank captures**. The actual new palette, brush controls, long custom names and photo-dialog footer were also visually reviewed. Reports: `artifacts/qa/preview13-panels/layout-report.json`; representative images: `artifacts/qa/preview13-studio` and `docs/screenshots`.

The build has zero errors and the existing NU1900 warning because NuGet vulnerability metadata could not refresh. No package dependencies were added. Publishing repeats the complete suite on the self-contained EXE before creating the ZIP; source and package reports are retained separately. All verification ran offscreen, without desktop input, application activation or access to the user's brush preset directory. Previous portable releases and user documents are preserved.

---

# Morupixel 0.2.0-preview.12 validation — 2026-09-21

The source suite passes **263/263** checks (21:33 KST). Three focused cases cover all corner/side cursor directions on very wide and tall images, rotation, reflection, transformed parents and the existing 7-DIP handle hit tolerance across zoom levels. Integration checks cover locks, hidden handles, adjustment layers, background jobs, active transform/pan/brush-size gestures, ordinary tool fallback, and unchanged pixels, geometry, document revision and undo/redo state.

An actual routed `QueryCursor` event, already marked handled, confirms the canvas registration reaches the cursor resolver despite WPF's normal `Cursor` handling. These checks run without creating a native editor window or moving the user's mouse. They validate the chosen standard Windows cursor objects and event routing; no physical desktop cursor screenshot was taken. Source report: `artifacts/test-results/self-test.txt`.

The build completed with zero errors and the existing NU1900 warning for unavailable NuGet vulnerability metadata. No dependencies changed. Packaging repeats the complete suite on the self-contained EXE before writing the ZIP, with its report in `artifacts/published-self-test/self-test.txt`. Previous portable versions and user documents are preserved.

---

# Morupixel 0.2.0-preview.11 validation — 2026-09-21

The source suite passes **260/260** checks (21:17 KST). The added routed-input regression exercises actual panel resize and workspace controls without native windows: width changes and bounds, repeated selection of the active segment, real workspace changes, unchanged layer positions/pixels, and preserved redo history. The input guard prevents resize and segment arrow keys from becoming layer-movement shortcuts. Existing editing and format checks remain passing. Source report: `artifacts/test-results/self-test.txt`.

Final panel QA captured **210 actual WPF layouts** at minimum/default dock widths of 324/396 DIP and floating content widths of 324/390 DIP. Both workspaces and every panel are covered at 100%/150% root DPI, with additional representative narrow cases at 200%. The report records **zero imposed glyph clips and zero abbreviated action labels**. It now detects literal ellipses in command strings, which previous size checks could not detect. Calibration verifies deliberately clipped text, ordinary scrolling, shortened labels, complete labels and the allowed overflow-menu glyph. Reports and PNGs: `artifacts/qa/preview11-panels`.

The complete image-property pane, design workspace and representative narrow/high-DPI captures were visually reviewed; normal panels were regenerated in `docs/screenshots`. The reference investigation, actual public file links, access limits and independent design choices are recorded in [DESIGN_REFERENCES.md](DESIGN_REFERENCES.md). No third-party UI kit assets or new runtime dependencies were added.

Builds completed with zero errors and the existing NU1900 warning because NuGet vulnerability metadata could not refresh. Packaging runs the full suite again against the self-contained EXE before creating the archive; its result is stored in `artifacts/published-self-test/self-test.txt`. Layout and input checks were offscreen: no user window was activated, closed or edited. These bounded checks do not cover every font, monitor configuration or physical mouse/IME interaction. Long panels remain scrollable.

---

# Morupixel 0.2.0-preview.10 validation — 2026-09-21

The final source suite passes **259/259** checks (20:50 KST), including five added workspace cases for tool/docking preservation, real quick actions, multi-layer alignment with rotated affine parents, undo/redo, locks, no-op alignment, and grouping a newly created layer after stale selection. Korean fallback coverage resolves actual rendered glyph runs, and the horizontal vowel stroke remains visible at 100/125/150% scale. Source report: `artifacts/test-results/self-test.txt`.

`tools/qa/panel-layout` captured **200 actual WPF pane layouts** across both workspaces, all panels, 340/396 DIP docks, 324/390 DIP floating content, 100/150% root DPI, long Korean names and scrolled top/bottom positions. Final result: **zero imposed glyph/layout clips** (`artifacts/qa/panel-layout/layout-report.json`). The detector is calibrated against deliberately clipped content and ordinary scrolling. Normal font overhang is not classified as clipping. Initial review exposed a real 1.086 DIP descender clip in the editable font field: TextBox padding had been applied both by the native text host and again in the custom template. The redundant host margin/alignment was removed and the final capture set rerun.

Representative image, text, design and full-editor captures were visually inspected. Long panels intentionally scroll; screenshots and measurement do not claim to cover every system font, every OS scale, or live monitor behavior. No desktop focus or mouse/keyboard input was used. Existing user release processes remained open. Updated normal editor previews are in `artifacts/qa/preview10-studio` and copied to `docs/screenshots`.

Builds complete with zero errors. NU1900 occurred because NuGet vulnerability metadata was unreachable; dependencies have not changed since Preview 9. Packaging additionally executes the full suite from the self-contained EXE and writes `artifacts/published-self-test/self-test.txt`.

---

# Morupixel 0.2.0-preview.9 validation — 2026-09-21

The Release source build has zero warnings and errors and passes **254/254** checks (19:30 KST). Report: `artifacts/test-results/self-test.txt`. The 18 new checks cover Windows PDF decoding, independent two-page PDF page selection, DPI/physical size, alpha and orientation, PDF-compatible AI and legacy AI rejection, RGB8 PSD output, raw/RLE/ZIP/prediction PSD and PSB input, names/order/masks/transforms and immutable sources, atomic failure, malformed/oversized input, actual external Photoshop files, independently authored DXF, real binary DWG and cancellation.

External fixtures are pinned to revisions and licenses in `tools/qa/compatibility/README.md`. Pillow independently decoded our composite, layered and masked PSD outputs and verified their expected pixels/alpha (`tools/qa/compatibility/verify_psd.py`). PsdSharp provides an additional independent PSD header/layout check. Those are separate decoders, not a Photoshop application round trip.

Actual import controls were rendered offscreen at 940 and 780px widths with PDF, PSD and DWG results; export controls were checked at 510px. Settings and long conversion notices scroll while confirmation buttons remain visible. Images are under `artifacts/qa/compatibility`. No desktop focus or input was taken from the user. Adobe/AutoCAD interactive checks, every DWG version, and a clean Windows 10 machine were not tested. Native app support requires Windows 10 build 19041 or later.

The supported scope is described in [FILE_COMPATIBILITY.md](FILE_COMPATIBILITY.md): page/model images and limited basic Photoshop/CAD pixel-layer separation, not complete Adobe/CAD native editing. The PSD parser currently accepts RGB/gray 8-bit only and does not apply embedded ICC. CAD hatch fill, plot styles, layout and external references are not reproduced. These limits are exposed before import. Package execution is verified separately by `Publish.ps1`; its report is `artifacts/published-self-test/self-test.txt`.

All eight optional QA projects and the interaction benchmark compile against the new Windows target. Several auxiliary restores emitted NU1900 because NuGet audit metadata was unreachable from the sandbox; all nine builds completed with zero errors. The main source build and compatibility QA build completed without warnings.

---

# Morupixel 0.2.0-preview.8 validation — 2026-09-21

The final source and self-contained executable suites both pass **236/236** checks. New coverage verifies selected-layer exports with group transforms, masks, clipping and transparent crop; HSV/harmony color synchronization and keyboard interaction; text content/font/line spacing/tracking, Korean shaping, IME routing, atomic undo and project persistence; retained shape scaling, fill/stroke alpha, masks, warps, project integrity and rasterization; workspace switches preserving the mixed document, redo and CMYK proof independently. The final packaging build has zero errors and zero warnings. Earlier incremental builds reported NU1900 when NuGet audit metadata could not refresh; dependencies are unchanged.

Actual WPF controls were rendered offscreen at 1200 and 1480px editor widths. Text controls were also inspected at 310/350px and 150% scale, with no internal glyph clipping; smaller scroll viewports intentionally reveal the remaining fields by scrolling. Numeric input padding, wrapping layer names, panel width and layer-list space were adjusted. Source reports: `release/test-results/preview8-review-self-test.txt` (233 checks before the last two IME cases), `release/test-results/text-panel-qa.txt` (235 checks).

CMYK remains ICC proof/output over RGB8 editing. Photo/design switches change tool order and the active panel while retaining the same document and every tool. Retained vector editing covers rectangles, rounded rectangles and ellipses, with fill/stroke and masks. Layer scaling samples retained geometry, but transformed parent groups still use an intermediate raster surface; the canvas itself renders at document resolution. SVG import/export, Bezier nodes, boolean paths, text frames and full Figma compatibility are outside this release. Advanced typography and shapes export to upstream `.comp` as pixels with an explicit warning; `.moruproj` retains editable metadata.

The final reports are `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`, both 236/236 (18:00 KST). Desktop testing launched the packaged executable and verified dark native chrome, the photo/design switch, drawing a retained rectangle, creating a text layer without a modal, Korean text including ㅡ, Ctrl+Enter applying the text, and three undo operations restoring the clean sample.

Desktop testing found that exporting immediately after adding a shape could use the old selection set. Export now follows the active layer unless it belongs to an explicit multiple selection; a regression verifies new-shape output dimensions, multiple selection and undo. The final package was rebuilt and all 236 checks passed. The final package launched and a test rectangle was created. The user stopped Computer Use with physical Escape during the last export-dialog recheck; no further app input was sent. That final dialog interaction and a desktop Save-file roundtrip were not completed. Export pixels/encoders and selection routing are covered by the automated suite. The original Preview 7 window remains untouched; the new Preview 8 window contains one unsaved verification rectangle that can be undone with Ctrl+Z.

---

# Morupixel 0.2.0-preview.7 validation — 2026-09-21

Compilation succeeds with zero errors. The initial Release builds had zero warnings; the final packaging run reported NU1900 because the sandbox could not refresh NuGet vulnerability metadata. Package dependencies did not change. Source and the self-contained Windows executable pass **199/199** checks. The new checks cover horizontal Korean vowel raster visibility at 100/125/150% scaling; monitor/paper dimensions and oversize rejection; DPI snapshot/project/RGB output/undo; above/below layer insertion and no-op redo; group cycles and locked targets; independent panel docking; source-preserving CMYK toggles and invalid profiles. Existing CMYK ICC/TIFF checks remain included.

The actual published Windows app was exercised using Computer Use: main and new-document native title bars are dark; Korean labels show horizontal strokes; CMYK proof visibly updates both canvas and histogram; a brush panel was dragged to a floating window, dragged to the left dock and returned to the right; a layer was reordered with plain drag and restored with Ctrl+Z; A4 selection shows 210 × 297 mm, 150 DPI, 1240 × 1754 px, and white background. Earlier user editor windows were left open.

Limitations: CMYK is an ICC proof/output mode over RGB8 editing, not native four-channel editing. Panel placement lasts for the current execution session. A-series presets default to 150 DPI to fit all paper sizes within the existing 16,777,216-pixel allocation limit; A2/A3 at 300 DPI are rejected. Dark native caption attributes depend on Windows support (verified on the current Windows 11 machine). In high contrast mode the OS caption colors are retained.

## Previous releases

# Morupixel 0.2.0-preview.6 validation — 2026-09-21

The design update retains all 185 Preview 5 checks and adds three checks for alpha-weighted histogram data, new-document dimensions/backgrounds/validation, and sidebar/brush setting changes preserving pixels and redo. The source suite passes **188/188** checks.

Actual WPF controls were rendered at 1480×920 and 1200×750, including the compact layout, color and brush panels, new-document presets and the hue/saturation dialog. These are offscreen layout checks, distinct from desktop input. The UI glass material is a gradient/reflection treatment with shadows, not Windows Acrylic or a blurred image preview. Histogram sampling is bounded to approximately 65,536 pixels and skips pointer-move renders.

The final Release build completed with zero warnings and errors; both source and self-contained published executable passed **188/188** tests. Reports are at `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`.

The published Preview 6 window was also tested on the desktop: the hue/saturation dialog opened, clicking the saturation track changed it to approximately -49.34, the image and histogram updated, and Escape restored the unchanged sample. Ctrl+N opened the new-document dialog; selecting the vertical-story preset updated its dimensions to 1080×1920. Cancel returned to the original clean document. The older Preview 4 window was kept open. No physical pen/pressure testing was performed.

---

# Morupixel 0.2.0-preview.5 validation — 2026-09-21

The source suite passed **185/185** checks. New cases cover inactive/final document closing, preservation of the active viewport, inline opacity/transform commits and undo, no-op history, inline invalid-value correction, committing before another layer is selected, stale control isolation and parent locks. A font check resolves Korean and Latin glyphs from the bundled resources at actual Regular, Medium and SemiBold weights.

Real WPF workspace controls were rendered offscreen at 1480px and 1200px. The editor, bucket toolbar, text dialog, dark blend dropdown, and scrollable Layer menu were visually inspected. Menu/dropdown children were laid out without opening their native popups, and the editor/dialog window handles stayed zero. The editable font chooser retains its required text-input template part. This verifies layout and control contracts, not physical mouse or IME input.

The Release build succeeded with zero errors. The existing NU1900 warning means NuGet vulnerability metadata could not refresh in the network sandbox; package dependencies did not change. Pretendard v1.3.9 assets and license provenance are recorded in `assets/fonts/README.md`. The self-contained published `Morupixel.exe` also passed **185/185** checks, including loading all three embedded font weights. The portable ZIP contains the font provenance and OFL license, .NET runtime and AI model. Existing preview.4 and earlier release folders remain intact.

---

# Morupixel 0.2.0-preview.4 validation — 2026-09-21

The integrated source suite passed **176/176** checks. Added coverage exercises foreground/background Delete aliases, selection and lock behavior, no-op history preservation, bucket tolerance/connectivity/alpha/transforms, an asynchronous bucket transaction with undo, text keyboard routing/default buttons/IME decisions, text preview eligibility and offscreen pixels, and render queue shutdown. The text and bucket UI checks use an offscreen dispatcher and routed events; they do not inject desktop input or exercise a physical Korean IME session.

The Release build succeeded with zero errors. NuGet vulnerability metadata could not refresh in the network sandbox (NU1900); no dependency was added or changed. The self-contained published `Morupixel.exe` also passed **176/176** checks. Reports are `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`. The portable ZIP includes the runtime, model, licenses and architecture documentation.

Actual WPF controls were rendered without creating native window handles. The new bucket toolbar was checked at 1480px and the 1200px minimum window width, and the text dialog was checked for its apply/cancel controls and keyboard hint. No user's running window was activated, closed or edited.

An offscreen component benchmark measured a five-run median of **162.947ms → 66.853ms** for 24 completed text-move frames. It compares repeated full composites with one cached background plus transformed text. This is not mouse latency or a universal speedup: canceled intermediate frames, final full rendering and interactive scheduling differ in real use. Skipping 120 unchanged rounded brush-size events measured **292.207ms → 0.002ms**; this is avoided no-op work, not changing brush strokes. The reproducible harness, workload and CSV are in `benchmarks/Interaction` in the source repository.

Source files now live under App/Core/Engine/Formats/UI/Tests. The original project-file path and build/test/package entry points remain unchanged. The portable preview.4 folder is separate from existing releases.

---

# Morupixel 0.2.0-preview.3 validation — 2026-09-21

The source and self-contained published executable each passed **156/156** existing checks, including the new embedded sample's render/save/load roundtrip and editable-text preservation. The source Release build completed with zero warnings and errors. Publishing completed successfully; its NuGet audit refresh reported NU1900 because the network sandbox could not reach nuget.org. No package dependency was changed in this theme update.

The real WPF workspace was rendered offscreen and visually checked for the powder-blue logo, navy controls, blue selected layer/tab, and embedded seaside sample. Both SVG and executable ICO use the same M geometry and colors; the ICO has validated 16, 24, 32, 48, 64, 128 and 256 pixel entries. The window title icon and header use a matching vector image.

This verification did not open, close, or modify the user's running editor windows. The new portable build is in its own preview.3 directory. Existing preview.1 and preview.2 directories remain available.

---

# Morupixel 0.2.0-preview.2 validation — 2026-09-21

The updated automated suite passes **156/156** checks. Seven added checks exercise brush resizing without painting or losing redo, clamping and cancellation, clone/heal source preservation, tool switching and Alt selection isolation, foreground/background exchange and selected alpha fill, premultiplied two-color gradients, and HSV conversion across the RGB gamut. These call the same gesture methods used by the editor; they do not inject a physical Alt+mouse gesture.

The 1480×920 WPF workspace was rendered offscreen and visually inspected. NuGet restore and a transitive package vulnerability audit succeeded against the configured official NuGet source on this date with no reported vulnerable packages.

The source build and self-contained published executable each passed 156/156 checks; the final Release build had zero errors and zero warnings. Reports: `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`.

Interactive verification of the new published window covered opening the foreground picker, clicking the hue strip and observing RGB changes, entering HEX `FA7527` and observing RGB 250/117/39, applying the color, X exchange, D black/white defaults, and the brush-specific toolbar. The old preview.1 window with unsaved work was preserved. The available UI automation API cannot hold Alt during a mouse drag, so the Alt gesture itself was verified through the automated gesture-path tests above, not desktop input injection.

The earlier release's broader validation is retained below as historical evidence.

---

# Morupixel validation — 2026-09-21

Version under validation: `0.2.0-preview.1`.

## Integrated source validation

Release build: zero errors and zero warnings. The integrated self-test run passed **149/149** checks on this Windows development machine, including the real bundled ONNX model, ICC/EXIF image input, CMYK TIFF profile/DPI/preview consistency, all-channel adjustments, layer hierarchy and project round trips.

The self-contained **published `Morupixel.exe` also passed 149/149 checks**. Source and published reports are in `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`. The portable ZIP includes the runtime, native ONNX libraries, pinned model, documentation and licenses. NuGet vulnerability metadata initially could not be fetched within the network sandbox; a subsequent successful online restore and transitive package audit reported no known vulnerable packages from the configured NuGet source on this date.

Twelve offscreen command tests call the editor's actual transactions for groups, duplication/deletion, per-tab history, soft masks, multi-selection moves, cancellation, editable text and saved project lifecycle. Two additional tests protect against reopening the same project in multiple tabs / overwriting another tab's path, and asynchronous filters bypassing a parent's lock; these are included in the twelve. Two dialog rendering tests verify the before/after toggle and selection coverage. No desktop mouse/keyboard input or foreground window changes were used for this validation.

The sample WPF screen was rendered offscreen and visually inspected at 1480×920. It verifies layout, labels, thumbnails and transform handles, not physical mouse interaction. The sample stores grouped, editable text layers and uses the Morupixel brand and icon.

A separate background benchmark on a 4096×4096 raster with a 42px brush and 32 interpolated dabs measured local blur at 58ms / 65.1MiB allocated, smudge at 80ms / 64.3MiB and cloning at 66ms / 64.0MiB. This is a single development-machine measurement, not a cross-device performance claim. Each batch copies the full raster once, with local footprint buffers for intermediate dabs.

## Image I/O, interoperability and AI subsystem

An independent Windows .NET 8/WPF harness built the engine and I/O sources without the UI and passed 14/14 checks. These include all EXIF orientations and a real JPEG orientation metadata fixture; embedded sRGB ICC import; exact PNG/TIFF RGBA round trips; JPEG quality and white matte; alpha-correct Lanczos downsampling; a hand-authored Compositor v7 package; unsupported-feature and path validation; five supported adjustment round trips; grouped editable text and grayscale masks; mask composition; a generated ONNX graph with known output; and real inference with the pinned bundled U²-NetP model.

Model SHA-256: `309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8` (4,574,861 bytes). The checksum and inference are also part of the main self-test suite.

The full build and packaged executable use `scripts/Test.ps1` and `scripts/Publish.ps1`; their generated reports are the source of truth for the final integrated test count. `--render-preview` produces an offscreen WPF layout image without displaying a window. It does not exercise mouse/keyboard interaction. Manual 0.2 UI verification, fresh-machine installation, Windows 10 and macOS `.comp` round trips must be recorded separately if performed.

## Historical verification

The previous 0.1 build's 38 tests, interactive UI smoke checks, published hash and GitHub CI run are preserved in [VALIDATION-0.1.md](VALIDATION-0.1.md). Those earlier results do not certify the 0.2 implementation or its additional features.
