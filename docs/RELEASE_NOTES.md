# Morupixel 0.2.0 Preview 21 — local candidate

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
