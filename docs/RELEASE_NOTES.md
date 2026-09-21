# Morupixel 0.2.0 Preview 17

Layers, color, and composition — in a Windows workspace.

This release collects the changes since the previous public Preview 1, bringing photo and design work into the same document.

[Download for Windows x64](https://github.com/catsony5-web/compositor-windows/releases/download/v0.2.0-preview.17/Morupixel-0.2.0-preview.17-win-x64.zip) · [Website](https://morupixel.arch-t.chatgpt.site/)

- **A clearer start and close.** Preview 16 opens an empty workspace with New Document, Open and Learn. The bundled sample opens only when requested, and closing the last document returns to the empty workspace. Preview 17 adds a dark save-before-close dialog with the document name and explicit Save Then Close, Close Without Saving and Cancel actions. Escape, window dismissal, a cancelled save or a failed save keeps the document open.
- **Photo and design workspaces.** Switch the preferred tools and panels while keeping images, editable text, rectangles and ellipses together. Character and paragraph controls, canvas alignment, movable panels, layer dragging and full command labels make the workspace easier to arrange.
- **Brushes, color and photo development.** Use built-in or imported image tips with angle and spacing controls, resize brushes with Alt-drag, choose selected-color tones and harmony palettes, and apply 13 photo-development adjustments as an editable layer. Numeric sliders offer continuous movement or selectable steps.
- **More file exchange.** Import PDF pages, PDF-compatible AI, RGB/gray 8-bit PSD/PSB and DWG/DXF previews. Export selected layers as an image, or the document as an RGB image PDF or supported pixel-layer PSD. ICC-managed CMYK proof and TIFF output remain available alongside sRGB editing.

Windows 10 version 2004 (build 19041) or later / Windows 11, x64. Extract the entire ZIP and run `Morupixel.exe`; .NET and the local AI model are included. AI background removal also requires the [Microsoft Visual C++ x64 runtime](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist).

Development preview · Unsigned · RGB 8-bit editing. PDF/AI/CAD imports are rendered images, with limited CAD-layer separation; PSD retains only supported basic pixel layers or a stored composite. Native AI/CAD object editing, DWG/DXF export, arbitrary vector paths, camera RAW decoding and native CMYK/16/32-bit editing are not supported. Keep editable work in `.moruproj`. See the [file compatibility matrix](https://github.com/catsony5-web/compositor-windows/blob/main/docs/FILE_COMPATIBILITY.md) for exact limits.

**Validation:** the Preview 17 source suite passed **321/321 automated checks**. The close dialog passed 12 offscreen layouts at 100%/150%/200% DPI with no detected glyph or action-label clipping. These checks do not replace physical desktop input or native file-picker testing. [Validation details](https://github.com/catsony5-web/compositor-windows/blob/main/docs/VALIDATION.md)

[User and developer guide](https://github.com/catsony5-web/compositor-windows/blob/main/docs/GUIDE.md) · [Supported features](https://github.com/catsony5-web/compositor-windows/blob/main/docs/PORTING.md) · [Complete version history](https://github.com/catsony5-web/compositor-windows/blob/main/docs/RELEASE_NOTES_ARCHIVE.md)

Based in part on [Compositor](https://github.com/robbietilton/Compositor) by Robbie Tilton / Wonder Assembly LLC. Independent project, MIT license. [Attribution](https://github.com/catsony5-web/compositor-windows/blob/main/NOTICE.md)
