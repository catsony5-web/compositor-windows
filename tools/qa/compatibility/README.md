# Compatibility checks

Run `scripts/Test.ps1` first; the suite creates PDF/AI/PSD/PSB/DXF fixtures and extracts the pinned external PSD and DWG fixtures below `artifacts/test-results/compatibility`.

```powershell
dotnet run --project tools/qa/compatibility/CompatibilityQa.csproj -c Release -- artifacts/qa/compatibility
python tools/qa/compatibility/verify_psd.py
```

The C# runner renders import/export controls offscreen at regular and minimum sizes. It never opens a desktop window. Python uses Pillow as an independent decoder to verify our PSD writer's composite pixels and alpha; it is a development dependency, not bundled in the application.

`fixtures/2layers.psd` and `layer_mask_data.psd` are from the MIT-licensed [psd-tools test corpus](https://github.com/psd-tools/psd-tools/tree/30bf79c2a88e0fc63104ee85713e23b04362153d/tests/psd_files). The associated license is in `fixtures/psd-tools-LICENSE.txt`. `block-rotation.dwg` is the MIT-licensed [ACadSharp BLOCKROTATIONPARAMETER sample](https://github.com/DomCR/ACadSharp/blob/3feabba4b2cbcb226f10b288aaee32442b03af6f/samples/dynamic-blocks/BLOCKROTATIONPARAMETER.dwg); see `licenses/ACadSharp-LICENSE.txt`. Exact source revisions are in `fixtures/sources.json`. These small fixtures are embedded for regression tests of the portable executable.

These tests do not establish complete Photoshop/Illustrator/AutoCAD parity or exercise those commercial applications.
