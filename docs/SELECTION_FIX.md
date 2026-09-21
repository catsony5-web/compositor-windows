# Preview 2 selection UX validation — 2026-09-21

Version: `0.1.0-preview.2`

Environment: Windows 11, x64.

## Automated checks

All 46 self-tests passed in both the source build and the self-contained published executable. The eight new layer-picking checks cover layer order, transparent holes, hidden layers, zero opacity, masks, locked layers, transforms, and canvas boundaries. Existing rendering, history, and file-persistence checks also passed.

Reports: [source](validation/preview2-source-self-test.txt), [published executable](validation/preview2-published-self-test.txt). The executable runs its self-tests without opening an editor window. Source review also confirmed that clicks below the system drag threshold do not change layer position, and selection alone does not create an undo entry.

## Interactive selection checks

The following behavior was checked in the native Windows 11 application:

- Clicking the visible moon on the canvas selected its layer.
- Clicking a layer row's name, thumbnail, and blank space selected that layer.
- Toggling another layer's visibility kept the current layer selected.
- Turning off **자동 선택** preserved the layer selected in the list when the canvas was clicked.

The **자동 선택** control means canvas layer picking: the move tool chooses the topmost layer with a visible pixel at the clicked point. It is separate from image-area selection tools. Preview 2 does not include a magic wand.

## Related validation

[VALIDATION.md](VALIDATION.md) remains the historical validation record for `0.1.0-preview.1`, including that release's published executable tests and artifact details. This file records only the Preview 2 selection UX fix and its current verification status.
