# Windows porting notes

## Scope and identity

Compositor for Windows 0.1.0 Preview is an independent C#/.NET 8 WPF implementation for Windows. It is a community port inspired by the open-source macOS project [Compositor](https://github.com/robbietilton/Compositor), created by Robbie Tilton and released by Wonder Assembly LLC under MIT. It is not an official upstream port and does not promise behavioral, visual, performance, or file-format parity.

The port has a separate application model (`Document`, `Layer`, and `Raster`), a separate renderer, and a separate `.cwproj` storage format. The original project stores `.comp` packages and currently describes versions 1–6; Windows reads and writes only its own `.cwproj` version 1.

## Feature matrix

The matrix describes the current Windows source, not the complete upstream feature list.

| Area | Windows 0.1.0 Preview | Upstream relationship / current limitation |
| --- | --- | --- |
| Raster document | Canvas up to 8,192 px per side and 16,777,216 pixels; up to 32 layers | Separate limits from upstream; no parity claim |
| Layer stack | Add, delete, duplicate, rename, reorder, visibility, lock, opacity | Folders/groups and nested layers are not implemented |
| Blend modes | Normal, Multiply, Screen, Overlay, Soft Light, Darken, Lighten, Difference, Color Dodge, Color Burn | Matches the corresponding basic names; no Hue/Saturation/Color/Luminosity modes |
| Transform | Move, scale, rotate, horizontal/vertical flip; numeric entry | No free distort, multi-layer transform, snapping, guides, or sampling setting |
| Masks | One raster mask per layer; paint, fill, invert, remove; white reveals and black hides | No folder masks, linked/live masks, or clipping masks |
| Selections | Rectangle and ellipse marquee, select all, deselect, selection-limited edits | No lasso, polygonal lasso, magic wand, feather/expand/contract, or selection move |
| Paint | Brush and eraser with size, hardness, opacity; selection clipping; undo | No spot healing, clone stamp, smudge/liquify, or content-aware fill |
| Shapes/gradient | Raster rectangle, ellipse, and foreground-to-transparent gradient | Not the upstream editable shape/text model |
| Text | Dialog-created text is rendered into a raster layer | No editable text metadata, paragraph boxes, or text clipping-mask behavior |
| Adjustments | Destructive Levels, Exposure, Saturation, Grayscale, Invert, Gaussian Blur | No adjustment layers, Curves, Gradient Map, Grain/Noise, Motion Blur, Lens Correction, or live preview |
| Input | PNG, JPEG, BMP, TIFF, GIF through WPF decoding | No HEIC, PSD, or upstream `.comp` input |
| Output | Flattened PNG/JPEG; JPEG uses a white background for transparency | No upstream project package export; no resolution/ICC/CMYK metadata promise |
| Clipboard | Copy merged image and paste image as a layer | Windows clipboard image path only |
| Projects | `.cwproj` ZIP with `document.json`, indexed PNG layers, and optional masks | Incompatible with `.comp`; no multi-document tabs |
| AI/network | No AI model and no network service | The port does not implement upstream background removal or an AI feature |

## Source mapping and algorithm attribution

Reference snapshot: [`9d5582dc59429501e270828b27879de9ca30a853`](https://github.com/robbietilton/Compositor/tree/9d5582dc59429501e270828b27879de9ca30a853), inspected on 2026-09-21. The upstream MIT license is preserved byte-for-byte in this repository's LICENSE.

The following relationships are recorded because the Windows source itself names the upstream counterparts. They are attribution and implementation notes, not a claim that the whole upstream application was mechanically translated.

| Windows source | Upstream reference | What is carried over |
| --- | --- | --- |
| `src/Model.cs`, `Layer.Matrix` | `Compositor/Document/LayerTransform.swift`, `BrushRaster.pixelToDocument` | Centered layer placement, scale, rotation, and flips. The C# source includes a port comment. |
| `src/Imaging.cs`, `Imaging.Level` | `Compositor/Document/Levels.swift`, `LevelRange.normalized` and `apply` | Input/output clamping and gamma mapping. The C# source labels this a direct C# translation. |
| `src/Imaging.cs`, `BrushStroke.Falloff` | `Compositor/Document/BrushStroke.swift`, `BrushRaster.falloff` | Normalized Gaussian-like soft brush falloff and a stroke-wide coverage cap. The C# source labels the falloff as ported. |
| `src/Imaging.cs`, `Render`/`Composite` | `Compositor/Rendering/LayerRenderer.swift` and `Document/LayerAppearance.swift` | Layer compositing concepts, opacity, transforms, masks, and named blend modes; the Windows code uses its own WPF/BGRA implementation and bilinear premultiplied sampling. |
| `src/Model.cs`, `History` | `Compositor/Document/DocumentHistory.swift` | Snapshot-based undo/redo and retention-aware trimming; Windows uses C# object snapshots and different 50-entry/192MB limits, while upstream defaults differ. |
| `src/ProjectStore.cs` | Upstream project-store documentation | Atomic replacement and validation are independent Windows implementation choices. The schema and file extension are different. |

The upstream files above are available in the upstream repository and remain subject to the upstream MIT notice. See [`../NOTICE.md`](../NOTICE.md) and [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md).

## `.cwproj` format

Version 1 is a ZIP container containing:

```text
document.json
layers/0.png
layers/0.mask       (optional, raw 8-bit mask bytes)
layers/1.png
...
```

`document.json` stores canvas width/height, document name, active layer ID, and ordered layer metadata: ID, name, visibility, lock state, opacity, blend mode, X/Y, scale, rotation, flips, and mask presence. PNG files retain each layer raster and masks use the corresponding layer pixel dimensions. Saving writes a temporary file, flushes it, and replaces the destination atomically.

The loader accepts only manifest version 1, rejects duplicate IDs and invalid finite numeric ranges, limits layers to 32, validates raster dimensions, and rejects missing layer or mask entries. These checks protect this port's format; they do not make a `.cwproj` a reader for an upstream `.comp` document.

## Build and release

The source project targets `net8.0-windows` with WPF. The Windows x64 distribution is a portable ZIP containing the application and .NET runtime. Extract the entire archive before running the executable. The preview is unsigned.

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1 -Version 0.1.0-preview.1
```

The scripts use `dotnet restore`, `dotnet build`, and `dotnet publish -r win-x64 --self-contained true`. `Publish.ps1` creates `release\Compositor.Windows-<version>-win-x64.zip`, a matching `.sha256` file, and a published self-test report in `release/published-self-test/`. The source also has a `--self-test <result-file>` entry point. The ZIP includes the original MIT notice and the installed .NET SDK's full license and third-party notices. Clean-machine and Windows 10 compatibility checks remain outstanding.

## Rendering limits

The renderer uses premultiplied bilinear color sampling, but fractional translation and rotation do not calculate full pixel coverage at the transformed layer boundary. Edge aliasing is visible in those cases. Gaussian blur stays within the original layer raster rather than expanding it. Large documents are processed on the UI thread with parallel pixel loops; interactive responsiveness still needs improvement. EXIF auto-orientation and ICC/CMYK color management are not implemented.
