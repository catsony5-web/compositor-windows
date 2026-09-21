# Third-party notices

This file records the third-party software and upstream attribution relevant to the current source tree. The project file currently has no `PackageReference` entries.

## Compositor (upstream project)

The project is an independent Windows port of **Compositor** by **Robbie Tilton / Wonder Assembly LLC**.

- Source: <https://github.com/robbietilton/Compositor>
- License: MIT
- Copyright: `Copyright (c) 2026 Wonder Assembly LLC`
- Local license text: [`LICENSE`](LICENSE)

The upstream license applies to the upstream material and permissions described by that license. This port does not claim that the Windows implementation is an official upstream release or that it is compatible with every upstream feature or file format.

## .NET and WPF

The Windows project targets `net8.0-windows` and uses the WPF framework supplied by the .NET SDK/runtime. A self-contained publish includes the applicable Microsoft .NET runtime components in the output. Consult the notices shipped with the selected .NET SDK/runtime and the official license sources when redistributing a packaged build:

- .NET repository license: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- WPF repository license: <https://github.com/dotnet/wpf/blob/main/LICENSE.TXT>

The current source tree does not declare an additional NuGet package or an external image-processing library. Windows codecs and WPF imaging are used through the framework. The exact runtime and framework notices in a future release bundle should be reviewed against the SDK/runtime version used to produce that bundle.

## Project assets

No separately licensed third-party font, icon pack, stock image, or AI model is declared by the current source tree. Text uses fonts available on the local Windows system; their licenses remain the responsibility of the user or distributor.
