# Third-party notices

## Compositor

Morupixel contains adaptations of algorithms and format definitions from Compositor by Robbie Tilton / Wonder Assembly LLC. Copyright (c) 2026 Wonder Assembly LLC. MIT license text: [LICENSE](LICENSE). Source: https://github.com/robbietilton/Compositor. See [NOTICE.md](NOTICE.md) and [source mappings](docs/PORTING.md).

## .NET 8 and WPF

The self-contained Windows build includes Microsoft .NET and WPF. The distribution includes DOTNET-LICENSE.txt and DOTNET-THIRD-PARTY-NOTICES.txt copied from the SDK used for packaging. Official sources: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.

## Microsoft ONNX Runtime 1.30.0

CPU inference runtime, NuGet package `Microsoft.ML.OnnxRuntime` 1.30.0 (including its managed binding). MIT license and dependency notices are preserved in [licenses/ONNXRuntime-LICENSE.txt](licenses/ONNXRuntime-LICENSE.txt) and [licenses/ONNXRuntime-THIRD-PARTY-NOTICES.txt](licenses/ONNXRuntime-THIRD-PARTY-NOTICES.txt).

Official source: https://github.com/microsoft/onnxruntime

## U²-NetP model

The unmodified 4,574,861-byte ONNX model is distributed with its original U²-Net Apache-2.0 license, author attribution, retrieval source and checksum in [models/README.md](models/README.md) and [models/U2NET-LICENSE.txt](models/U2NET-LICENSE.txt).

Original authors: Xuebin Qin, Zichen Zhang, Chenyang Huang, Masood Dehghan, Osmar R. Zaiane and Martin Jagersand. Original project: https://github.com/xuebinqin/U-2-Net. ONNX asset publisher: https://github.com/danielgatis/rembg/releases/tag/v0.0.0. Model licensing is separate from the rembg application's MIT license; it is not relabeled as Morupixel/MIT.

The local inference preprocessing follows the U²-Net model contract also documented by rembg's BaseSession and U2netpSession. No Python/rembg executable or service is bundled.

## ACadSharp 3.7.16

The bundled DWG/DXF reader is [ACadSharp](https://github.com/DomCR/ACadSharp), Copyright (c) 2026 Albert Domenech, under the MIT license. The full notice is in [licenses/ACadSharp-LICENSE.txt](licenses/ACadSharp-LICENSE.txt). The embedded `block-rotation.dwg` self-test fixture is from its `samples/dynamic-blocks/BLOCKROTATIONPARAMETER.dwg` at revision `3feabba4b2cbcb226f10b288aaee32442b03af6f`, under the same license.

## PDFsharp 6.2.4

[PDFsharp](https://github.com/empira/PDFsharp/tree/v6.2.4), Copyright (c) 2001-2026 empira Software GmbH, is used to read PDF optional-content layers and prepare layer-specific content for the Windows renderer. Its MIT notice is in [licenses/PDFsharp-LICENSE.txt](licenses/PDFsharp-LICENSE.txt). The NuGet dependencies Microsoft.Extensions.Logging.Abstractions 8.0.3, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2 and System.Security.Cryptography.Pkcs 8.0.1 are MIT-licensed .NET Foundation components; see [licenses/Microsoft-PdfDependencies-LICENSE.txt](licenses/Microsoft-PdfDependencies-LICENSE.txt).

## PSD verification dependencies and fixtures

[PsdSharp 1.2.0](https://github.com/kaelon141/PsdSharp), Copyright (c) 2025 Jordy de Koning, is included for an independent PSD header/layout check in the built-in self-tests. See [licenses/PsdSharp-LICENSE.txt](licenses/PsdSharp-LICENSE.txt) (MIT). Production PSD/PSB import and PSD export use Morupixel's own bounded format implementation.

Embedded `2layers.psd` and `layer_mask_data.psd` test fixtures are from the [psd-tools test corpus](https://github.com/psd-tools/psd-tools/tree/30bf79c2a88e0fc63104ee85713e23b04362153d/tests/psd_files), Copyright (c) 2019 Kota Yamaguchi, MIT. The original notice is preserved in [licenses/psd-tools-LICENSE.txt](licenses/psd-tools-LICENSE.txt). The Python psd-tools library is not bundled. Exact fixture revisions are recorded in the development tree at `tools/qa/compatibility/fixtures/sources.json`.

## Windows SDK .NET projection and C#/WinRT

PDF rendering calls the operating system's Windows.Data.Pdf API through `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.56. The portable package includes `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll`; it does not bundle a separate PDFium or Ghostscript engine. Microsoft Windows SDK terms are identified by the NuGet package at [Windows SDK license](https://aka.ms/WinSDKLicenseURL), with a downloaded copy in [licenses/WindowsSDK-License.rtf](licenses/WindowsSDK-License.rtf). Copyright Microsoft Corporation. [C#/WinRT](https://github.com/microsoft/CsWinRT) is MIT licensed; see [licenses/CsWinRT-LICENSE.txt](licenses/CsWinRT-LICENSE.txt).

## Fonts and other assets

Morupixel bundles unmodified static OpenType faces from Pretendard v1.3.9 by Kil Hyung-jin under the SIL Open Font License, Version 1.1. The bundled files, source release URL, tag, checksums, family name, resource path, and license are documented in [assets/fonts/README.md](assets/fonts/README.md) and [licenses/Pretendard-LICENSE.txt](licenses/Pretendard-LICENSE.txt). The bundled seaside sample was generated for Morupixel with OpenAI's built-in image generation tool; it is not copied from Compositor or the visual references. Its native size is 1586 × 992 pixels. See [sample provenance](assets/samples/README.md). The user's imported photographs retain their own rights.
