# Morupixel interaction component benchmark

Run `dotnet run --project Morupixel.PerformanceQa.csproj -c Release -- performance-results.csv` from this folder after building the editor. This is an offscreen WPF component benchmark; it opens no editor window and does not drive desktop input.

`text_move` moves one 220 × 70 topmost, normal-blend text layer across a 1280 × 800 document in 24 steps. The `CanvasView` is 760 × 520 at 50% zoom. The legacy path performs `Document.Snapshot`, `Imaging.Render`, `Raster.Bitmap`, and a `CanvasView` draw for each completed frame. The current path renders the document without the text once, then updates the text bitmap transform and draws `CanvasView` for each step. Rapid legacy drags could cancel intermediate render jobs, so the legacy timing represents 24 completed frames, not actual input latency. The current implementation also waits for its background frame before showing the moved text and performs a full render on mouse-up.

`unchanged_brush_hud` sends 120 pointer updates whose rounded brush diameter remains 42 px. The legacy path invalidates and draws the canvas each time. The current path skips the update when the rounded size is unchanged. The timing measures the avoidable work for those no-op updates; it is not a frames-per-second claim.

The CSV contains the median of five alternating legacy/current runs on this machine. WPF composition, scheduling, machine load, cache state, and real pointer dispatch differ in the interactive editor. The benchmark checks the cost of the changed code paths, while the editor self-tests check rendering correctness.
