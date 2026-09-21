# Morupixel 0.2.0 Preview 18

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
