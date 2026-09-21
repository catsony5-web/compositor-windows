# Contributing

This is an independent Windows community port of [Compositor by Robbie Tilton](https://github.com/robbietilton/Compositor). Please report Windows-specific problems in this repository.

Build on Windows with the .NET 8 SDK and PowerShell:

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
```

For a bug report, include the Windows version, application version, steps to reproduce, and expected versus actual behavior. Use a small, non-sensitive sample when an image or project is needed to reproduce the issue.

Keep pull requests focused. Changes to compositing, document history, image decoding, or project serialization should include regression coverage in `src/SelfTests.cs`. Include the resulting self-test report and describe manual UI checks when behavior is interactive.

Preserve the upstream MIT license and attribution. Identify any additional upstream code translated or adapted in `NOTICE.md` and `docs/PORTING.md`. Describe unsupported features honestly rather than presenting this preview as feature-equivalent to the Mac application.
