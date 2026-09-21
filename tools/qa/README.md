# Optional background QA runners

These small auxiliary projects were preserved from development sessions so their source does not live among disposable build outputs. `scripts/Test.ps1` remains the authoritative full regression suite; these runners are optional focused diagnostics, not additional release requirements. The supported interaction timing methodology remains in `benchmarks/Interaction`.

Run commands from the repository root in the existing terminal. These tools use headless tests or offscreen WPF controls and do not show an editor window or send desktop input. Do not open a separate visible console or activate the user's Morupixel session for routine checks. The text-panel tool uses reflection into the current inspector and reports possible clipping for review; it is not a substitute for the complete regression suite.

| Folder | Purpose | Output |
| --- | --- | --- |
| `advanced` | Advanced tool regression subset | Console |
| `interaction` | Advanced tools and command lifecycle subset | Console and fixture files |
| `retouch` | One-shot 4096 × 4096 retouch timing diagnostic | Console |
| `io` | Image import/export regression subset | Console; its test suite removes temporary fixtures |
| `layer-export` | Selected-layer export regression subset | Console and fixture files |
| `text-panel` | Offscreen character inspector images at several widths/scales | PNG images and possible-clipping diagnostics |
| `color-palette` | Color palette regression subset and offscreen palette | Console and PNG image |
| `compatibility` | PDF/PSD/DWG import/export layouts and independent Pillow PSD verification | PNG images and console; [fixtures and instructions](compatibility/README.md) |
| `panel-layout` | Actual docked/floating panes at narrow/default widths and 100/150% DPI, glyph clipping review | PNG images and JSON; [instructions](panel-layout/README.md) |

Each project references `src/Compositor.Windows.csproj` and targets `net8.0-windows10.0.19041.0`, so `dotnet run` may build the editor. Do not run them concurrently with another build or a cleanup operation. No runner needs a previously published release or a copied editor DLL.

For runners that write files, pass an explicit output directory after `--`. Generated output belongs in `artifacts/qa/<name>`; all relative arguments and defaults are resolved from the current working directory:

```powershell
dotnet run --project tools/qa/interaction/InteractionHarness.csproj -c Release -- artifacts/qa/interaction
dotnet run --project tools/qa/layer-export/runner.csproj -c Release -- artifacts/qa/layer-export
dotnet run --project tools/qa/text-panel/TextPanelQa.csproj -c Release -- artifacts/qa/text-panel
dotnet run --project tools/qa/color-palette/PaletteRunner.csproj -c Release -- artifacts/qa/color-palette
dotnet run --project tools/qa/compatibility/CompatibilityQa.csproj -c Release -- artifacts/qa/compatibility
dotnet run --project tools/qa/panel-layout/PanelLayoutQa.csproj -c Release -- artifacts/qa/panel-layout
```

The console-only runners accept no output argument. Save a diagnostic transcript under the same generated-output tree when useful:

```powershell
New-Item -ItemType Directory -Path artifacts/qa/advanced -Force | Out-Null
dotnet run --project tools/qa/advanced/AdvancedHarness.csproj -c Release > artifacts/qa/advanced/run.txt
dotnet run --project tools/qa/io/IoHarness.csproj -c Release
dotnet run --project tools/qa/retouch/Benchmark.csproj -c Release
```

`bin/`, `obj/`, and `artifacts/` are already ignored by Git. Keep source files in this directory and generated files in those output locations. Historical outputs remain in their original directories until explicitly cleaned; the source move does not regenerate or replace them.
