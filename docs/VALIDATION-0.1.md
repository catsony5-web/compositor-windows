# Preview validation — 2026-09-21

Version: `0.1.0-preview.1`

Environment: Windows 11 25H2, build 26200.9457, x64; .NET SDK 8.0.424. The self-contained package includes .NET and Windows Desktop runtime 8.0.30.

## Automated checks

`scripts/Publish.ps1 -Version 0.1.0-preview.1` completed successfully. Release build: 0 warnings and 0 errors. All 38 self-tests passed in both the source build and the published self-contained executable.

The checks cover alpha compositing and blend modes, layer visibility/masks/transforms, levels, brush coverage/selection/eraser, blur transparency, undo/redo and memory limits, PNG/JPEG output, project round-trip, atomic-write failure preservation, invalid save preservation, metadata validation, and oversized image rejection.

The [published executable test report](validation/published-self-test.txt) records the individual results. Run `scripts/Test.ps1` to repeat the source checks; `scripts/Publish.ps1` additionally tests the published EXE.

## Interactive checks

The native WPF window was opened on Windows. Layer creation, brush strokes, Ctrl+Z, the G shortcut, horizontal gradient dragging, and Ctrl+S with a real project save were exercised. The final packaged application then reopened the seven-layer UI-generated project through its native Open dialog, restored the gradient and selection, and closed normally. The [editor screenshot](screenshots/editor.png) was captured from the running Windows application. Detailed steps are recorded in [UI QA](../tests/ui-qa.txt).

The screenshot and interactive checks are smoke tests. Clean-machine setup, Windows 10, ARM64, pen pressure, large-image stress tests, ICC color management, EXIF orientation, and full Mac feature parity have not been verified or implemented as applicable. See [release limitations](RELEASE_NOTES.md).

## Published artifact

The [Windows preview release](https://github.com/catsony5-web/compositor-windows/releases/tag/v0.1.0-preview.1) contains a 72,200,833-byte ZIP. GitHub's reported asset SHA-256 matches the locally tested package:

```text
3088cd7474c1636b368f6d33656d22e9a4bea9fd4f8769e061aaf51661d74eb2
```

Release source commit: `40f55d9927721c2f5d68cfb5eff98cc3a3bdb359`. The independent [GitHub Actions Windows build](https://github.com/catsony5-web/compositor-windows/actions/runs/35561185879) also passed.
