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

## Fonts and other assets

Text uses fonts installed on the user's Windows system. No third-party font file is bundled. Demo artwork is constructed in source code. The user's imported photographs and fonts retain their own rights.
