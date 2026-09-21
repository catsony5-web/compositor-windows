# Contributing

Morupixel is an independent Windows image editor derived in part from [Compositor by Robbie Tilton](https://github.com/robbietilton/Compositor). Please report Windows-specific problems in this repository.

Build on Windows with the .NET 8 SDK and PowerShell:

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
```

Development builds target Windows x64 and use the installed .NET 8 Desktop Runtime; only the Windows x64 ONNX native library is copied. `Publish.ps1` explicitly creates a self-contained package that includes .NET. Build intermediates remain in `src/bin` and `src/obj`; test reports and generated fixtures go to `artifacts/test-results`, and package checks go to `artifacts/published-self-test`. `release/` is reserved for versioned ZIP/SHA-256 files and portable application directories in `release/staging/`.

Use a new version/output directory when an earlier portable editor is open. Publishing refuses to reset a path used by a running process and rejects junctions/symbolic links; it never closes applications. Prefer background builds, self-tests and offscreen previews while another person is using the desktop.

For a bug report, include the Windows version, application version, steps to reproduce, and expected versus actual behavior. Use a small, non-sensitive sample when an image or project is needed to reproduce the issue.

Keep pull requests focused. Changes to compositing, document history, image decoding, or project serialization should include regression coverage under `src/Tests`, registered in `src/Tests/SelfTests.cs`. See [source structure](docs/ARCHITECTURE.md) for the engine, UI and format boundaries. Include the resulting self-test report and describe manual UI checks when behavior is interactive.

Preserve the upstream MIT license and attribution. Identify any additional upstream code translated or adapted in `NOTICE.md` and `docs/PORTING.md`. Describe unsupported features honestly rather than presenting this preview as feature-equivalent to the Mac application.
