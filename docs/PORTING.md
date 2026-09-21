# Morupixel implementation and compatibility

Morupixel 0.2.0-preview.1 is an independent Windows C#/.NET 8 WPF editor. Its name, executable and document format are independent from Compositor. The original MIT copyright/permission notice remains in LICENSE. Neither complete parity nor endorsement is implied.

Reference source: [Compositor snapshot 9d5582dc59429501e270828b27879de9ca30a853](https://github.com/robbietilton/Compositor/tree/9d5582dc59429501e270828b27879de9ca30a853), inspected 2026-09-21. The upstream store in this snapshot supports `.comp` package versions 1–7.

## Feature audit

| Area | Morupixel implemented scope | Differences from upstream / limits |
| --- | --- | --- |
| Workspace | Up to 8 document tabs, per-document history and view, dark Korean UI, guides and snapping | No assertion of equivalent keyboard coverage, accessibility or measured responsiveness |
| Layers | Up to 128, groups, multiple selection, opacity, visibility, locking and reordering | 384MB source layer/mask limit; groups composite in isolation, unlike upstream pass-through |
| Blends | Normal, Multiply, Screen, Overlay, Soft Light, Darken, Lighten, Difference, Color Dodge, Color Burn, Hue, Saturation, Color, Luminosity | CPU 8-bit sRGB rendering; platform/rounding differences possible |
| Transforms | Numeric and gesture move/scale/rotate/flip, nonuniform scale, multiple layers, four-corner projective warp | Boundary antialiasing and sampling differ from CoreGraphics/CoreImage; not every upstream transform interaction is reproduced |
| Masks | Layer and isolated-group masks, clipping to lower layer, brush editing | No independently placed/unlinked masks or arbitrary live mask reference graph |
| Selection | Rectangle, ellipse, lasso, polygon, contiguous magic wand, add/subtract/intersect/invert, feather/grow/shrink, alpha selection | CPU coverage mask; no upstream selection-geometry or edge-quality equivalence claim |
| Retouch | Clone, healing, blur brush, smudge, liquify displacement, bounded content-aware fill | Algorithms and limits differ; complex fills require visual review and manual cleanup |
| Text | Editable content, system font, size, bold/italic, alignment and color | No paragraph box, tracking or leading controls; WPF metrics differ from AppKit |
| Shapes | Raster rectangles/ellipses and gradients | Not editable vector shape metadata after creation |
| Adjustment layers | Composite and individual RGB-channel Levels/Curves, Hue/Saturation, Exposure+Offset+Gamma, Gradient Map, Grain | No range-aware/colorize HSV |
| Filters | Gaussian blur, motion blur, noise and radial lens distortion | CPU approximations; no professional camera/lens profile calibration |
| Background removal | Bundled 4.6MB U²-NetP, local ONNX Runtime CPU, editable mask result | Not Apple's Vision model; thin/transparent edges need manual correction; no comparative benchmark establishes equal quality |
| Import | WIC PNG/JPEG/BMP/TIFF/GIF; EXIF 1–8; embedded ICC conversion to sRGB; HEIC/HEIF with installed Windows codec | First frame only, normalized to 8-bit; no PSD or preserved source PPI/ICC/CMYK editing |
| Export | PNG, ZIP-compressed TIFF, JPEG quality 1–100, encoded-byte preview; Lanczos3 alpha-aware resizing helper | JPEG transparency uses white matte; no HEIC/PSD output or original metadata preservation |
| Print export | ICC-profiled CMYK TIFF with DPI setting and an sRGB round-trip preview | Native editing remains 8-bit sRGB; this is not a CMYK editing workspace or a press-certified proof. See [CMYK](CMYK.md). |
| Projects | `.moruproj` v2; reads prior `.cwproj` v1/v2; atomic saves | Separate format; unsupported versions are rejected |
| Upstream packages | Restricted `.comp` directory import/export (see below) | Explicitly partial, never blanket compatibility |
| Limits | 8192px per side, 16,777,216 total canvas pixels, 128 layers; 50 undo entries/192MB exclusively retained pixels | Lower than upstream's 30,000px/100MP and layer bounds; full app memory can exceed history/source limits due to rendering buffers |

## Upstream `.comp` bridge

The original format is a directory package containing `manifest.json` and `images/{UUID}.png`, not a ZIP file. On Windows select its folder or the manifest for import. Export creates a new `.comp` directory and refuses to replace an existing package. Copy the complete directory to macOS.

Supported: raster pixels and transforms; visibility/opacity and the 14 named blends; simple normal groups; enabled linked grayscale masks; basic text with cached pixels; composite and individual RGB-channel Levels and Curves, Exposure (including offset/gamma), Gradient Map and Grain adjustment metadata. Adjustment algorithms are based on upstream implementations, but cross-platform arithmetic/premultiplication can differ at rounding boundaries. Imported mask sizes may be resampled with an explicit warning. Text editing can change appearance if a font is unavailable or OS layout metrics differ.

Rejected rather than silently flattened: unsupported format/color space, live mask source references, separately placed/unlinked or disabled masks, shape metadata, layer effects, group masks, blend or adjustment layers inside groups (upstream pass-through semantics differ), color-range HSV, custom text spacing/paragraph bounds, unsupported curve endpoints/count, and our own projective/clipping features when exporting. Bold/italic or alpha text styles are not exported as native upstream text metadata. Layer locking and print-resolution metadata have explicit warnings because the bridge does not preserve them.

An import succeeds only when the complete document validates. Unsupported files leave the active document and original file intact. Fixture tests include hand-authored upstream JSON, five adjustment round trips, text/group/mask metadata, PNG grayscale masks, path escape rejection and unsupported effects. A macOS application round trip has not been run in this Windows environment; schema/pixel tests do not substitute for it.

## Algorithm attribution

| Morupixel code | Upstream source | Relationship |
| --- | --- | --- |
| `Model.cs`, `Layer.Matrix` | `Document/LayerTransform.swift`, `BrushRaster.pixelToDocument` | Centered placement, scale, rotation and flips |
| `Imaging.cs`, `Imaging.Level` | `Document/Levels.swift` | Direct translation of clamped input/output gamma mapping |
| `Imaging.cs`, `BrushStroke.Falloff` | `Document/BrushStroke.swift` | Normalized Gaussian-like falloff and stroke-wide coverage cap |
| `DocumentFeatures.cs` | `Document/Curves.swift`, `Document/ImageAdjustments.swift` and associated kernels | Monotone Hermite curves, linear-light exposure, gradient mapping and deterministic grain |
| `CompositorPackage.cs` | `IO/ProjectStore.swift`, document Codable types | Independent reader/writer for a strictly supported subset of v1–7 |
| `Model.cs`, `History` | `Document/DocumentHistory.swift` | Snapshot and retained backing-store concepts; independent bounds and implementation |
| `BackgroundRemoval.cs` | U²-Net and rembg session documentation | Independent C# local inference integration, original pretrained weights; model terms in `models/README.md` |

`ImportExport.cs` implements WIC color conversion/EXIF orientation, encoders and premultiplied Lanczos3 resizing. The engine uses independent managed BGRA surfaces and CPU rendering, not Apple's SwiftUI/Metal/CoreImage frameworks.

## Native Morupixel format

`.moruproj` is a ZIP containing `document.json`, ordered `layers/<index>.png` and optional raw `layers/<index>.mask`. Version 2 adds layer kind, parent IDs, clipping, nonuniform transforms, text, adjustments and warp metadata. Version 1 remains readable. It is not the `.comp` schema, even when some pixels/metadata can be exchanged by the bridge.

Saves validate before writing, stage a sibling temporary file, flush and replace atomically. The reader checks dimensions, layer counts, names, IDs, parent graph and finite numeric values. Source rasters and masks are immutable by convention so history can share buffers safely.

## Build and packaging

Use `scripts/Build.ps1`, `scripts/Test.ps1`, then `scripts/Publish.ps1 -Version 0.2.0-preview.1`. The portable output is `release/Morupixel-0.2.0-preview.1-win-x64.zip`, with a SHA-256 companion. Packaging verifies the pinned model and runs the published EXE self-tests. Source/runtime/model licenses are included. Windows 10 and a clean machine require separate manual verification. The repository URL and internal namespace retain earlier preview identifiers; no public rename is performed by the build.
