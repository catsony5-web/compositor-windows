# Morupixel validation — 2026-09-21

Version under validation: `0.2.0-preview.1`.

## Integrated source validation

Release build: zero errors and zero warnings. The integrated self-test run passed **149/149** checks on this Windows development machine, including the real bundled ONNX model, ICC/EXIF image input, CMYK TIFF profile/DPI/preview consistency, all-channel adjustments, layer hierarchy and project round trips.

The self-contained **published `Morupixel.exe` also passed 149/149 checks**. Source and published reports are in `release/test-results/self-test.txt` and `release/published-self-test/self-test.txt`. The portable ZIP includes the runtime, native ONNX libraries, pinned model, documentation and licenses. NuGet vulnerability metadata initially could not be fetched within the network sandbox; a subsequent successful online restore and transitive package audit reported no known vulnerable packages from the configured NuGet source on this date.

Twelve offscreen command tests call the editor's actual transactions for groups, duplication/deletion, per-tab history, soft masks, multi-selection moves, cancellation, editable text and saved project lifecycle. Two additional tests protect against reopening the same project in multiple tabs / overwriting another tab's path, and asynchronous filters bypassing a parent's lock; these are included in the twelve. Two dialog rendering tests verify the before/after toggle and selection coverage. No desktop mouse/keyboard input or foreground window changes were used for this validation.

The sample WPF screen was rendered offscreen and visually inspected at 1480×920. It verifies layout, labels, thumbnails and transform handles, not physical mouse interaction. The sample stores grouped, editable text layers and uses the Morupixel brand and icon.

A separate background benchmark on a 4096×4096 raster with a 42px brush and 32 interpolated dabs measured local blur at 58ms / 65.1MiB allocated, smudge at 80ms / 64.3MiB and cloning at 66ms / 64.0MiB. This is a single development-machine measurement, not a cross-device performance claim. Each batch copies the full raster once, with local footprint buffers for intermediate dabs.

## Image I/O, interoperability and AI subsystem

An independent Windows .NET 8/WPF harness built the engine and I/O sources without the UI and passed 14/14 checks. These include all EXIF orientations and a real JPEG orientation metadata fixture; embedded sRGB ICC import; exact PNG/TIFF RGBA round trips; JPEG quality and white matte; alpha-correct Lanczos downsampling; a hand-authored Compositor v7 package; unsupported-feature and path validation; five supported adjustment round trips; grouped editable text and grayscale masks; mask composition; a generated ONNX graph with known output; and real inference with the pinned bundled U²-NetP model.

Model SHA-256: `309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8` (4,574,861 bytes). The checksum and inference are also part of the main self-test suite.

The full build and packaged executable use `scripts/Test.ps1` and `scripts/Publish.ps1`; their generated reports are the source of truth for the final integrated test count. `--render-preview` produces an offscreen WPF layout image without displaying a window. It does not exercise mouse/keyboard interaction. Manual 0.2 UI verification, fresh-machine installation, Windows 10 and macOS `.comp` round trips must be recorded separately if performed.

## Historical verification

The previous 0.1 build's 38 tests, interactive UI smoke checks, published hash and GitHub CI run are preserved in [VALIDATION-0.1.md](VALIDATION-0.1.md). Those earlier results do not certify the 0.2 implementation or its additional features.
