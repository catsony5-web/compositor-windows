# Panel layout QA

Run from the repository root, after coordinating with other builds:

```powershell
dotnet run --project tools/qa/panel-layout/PanelLayoutQa.csproj -c Release -- artifacts/qa/panel-layout
```

This runner renders the actual `MainWindow` inspector and its existing movable panes. It never shows a desktop window, focuses a control, or sends user input. It changes only its own in-memory sample document.

Docked controls are captured by rendering the actual editor root and cropping the panel bounds transformed into that root; ancestor offsets and viewport clips are preserved. Floating contents render from their own offscreen root. Every capture also checks alpha coverage, luminance range, and color diversity so transparent or featureless images cannot silently pass glyph/layout checks. The pixel guard is calibrated against fully transparent and solid-background images before capturing the real panels.

Coverage:

- Photo and Design workspaces.
- Workspace actions, image/text/shape properties, colors, brushes, and layers.
- Docked sidebars at the minimum 324 and default 396 device-independent pixels, and detached panel contents at 324/390 (including the narrow client area of a 340-wide tool window).
- Root DPI of 96 and 144 (100% and 150%), including WPF font measurement and layout rounding.
- Additional 192-DPI (200%) narrow floating captures for Photo workspace/brush controls and Design text/shape/color/layer panes.
- Long Korean names, the horizontal vowel stroke `ㅡ`, multiline text, retained shapes, and a layer list long enough to scroll.
- Top and bottom views of panels with scrolling content.
- Actual button labels, accessibility names, tooltip text, enabled state, and whether each action is visible within the captured scroll viewport.

`layout-report.json` separates expected outer viewport scrolling from clipping within individual labels/fields. Measurements use the bounds of actual rendered `GlyphRunDrawing` objects and the actual visual clips, not only `DesiredSize` (which may already be constrained). A glyph's normal overhang beyond an unclipped label is allowed. Editable single-line fields may scroll horizontally, multiline editors may scroll vertically, and deliberate text trimming is permitted.

The action-label review catches literal `…` or `...` in actual panel button labels even when scrolling is needed to reach them. The overflow-menu glyph `⋯` is allowed; document layer names are user content and excluded. `Actions` lists the full observed labels and accessibility names for review without asserting a duplicate hard-coded list of production command names. Rich-content buttons retain their rendered title and description in this report.

`detector-calibration.json` records deliberately clipped text, ordinary scrolling/overhang, abbreviated action labels, complete labels, and a real overflow-menu glyph. These must be classified correctly before capturing the application. Exit code 2 flags possible clipping or abbreviated actions; inspect the corresponding PNG and report before treating it as a defect. Console output summarizes counts and at most 20 unique examples; the full report stays in JSON. These checks complement visual review and do not claim to verify font aesthetics or live monitor rendering.

The runner is isolated from the application's regression tests and generated captures stay under `artifacts/qa/panel-layout`.
