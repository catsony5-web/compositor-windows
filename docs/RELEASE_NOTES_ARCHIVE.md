# Morupixel 0.2.0-preview.1

The application now has an independent product name: **Morupixel / 모루픽셀**. Run `Morupixel.exe`; save editable work as `.moruproj`. Old `.cwproj` files remain readable. The upstream Compositor MIT attribution is retained.

This update adds document tabs, grouped layers and clipping masks, 14 blend modes, nonuniform/projective transforms, editable text, non-destructive adjustment layers, advanced selections and retouch tools, additional filters, local AI background removal, image I/O improvements, and restricted upstream `.comp` interoperability.

AI background removal works offline using the bundled 4.6MB U²-NetP model and Microsoft ONNX Runtime CPU. No image upload or subscription is used. EXIF orientation and embedded ICC-to-sRGB conversion are handled on import; JPEG quality preview and TIFF export are available. The separate print dialog adds ICC-managed CMYK TIFF output, DPI selection and a profile-conversion preview; the native editing workspace remains sRGB.

This is a development preview, not a claim of complete Compositor parity. Fine text layout, live/unlinked masks, pass-through group compositing, vector shape metadata, range-specific HSV adjustments, layer effects, PSD and native high-bit-depth/CMYK editing workflows remain incomplete or unsupported. The lightweight segmentation model has limitations on fine and transparent edges. Package interoperability is intentionally strict about unsupported semantics. See [the complete matrix](PORTING.md).

The source and self-contained published executable each passed **149/149 automated checks**, including native ONNX inference, project round trips, CMYK profile output and offscreen editor transactions. The WPF screen was rendered offscreen and visually inspected. This is separate from live mouse/keyboard testing and does not establish full upstream quality parity. See [validation details](VALIDATION.md).

Extract the complete portable ZIP before running. The distribution includes .NET, ONNX Runtime, the model, and applicable licenses. No GitHub publication or repository rename is implied by a local build.

AI inference also requires the system Microsoft Visual C++ x64 runtime, which is not bundled. A missing-runtime error includes the official installation link; see [AI setup](BACKGROUND_REMOVAL.md).
