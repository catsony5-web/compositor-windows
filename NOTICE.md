# NOTICE

Morupixel (모루픽셀) is an independent Windows image editor derived in part from **Compositor**, originally created by **Robbie Tilton** and released by **Wonder Assembly LLC**.

- Original source: https://github.com/robbietilton/Compositor
- Original copyright: Copyright (c) 2026 Wonder Assembly LLC
- Original license: MIT, preserved in the root LICENSE file.

Morupixel is not an official Windows edition of Compositor, is not endorsed by the original author or company, and does not imply feature, rendering, or file-format parity. Product branding is independent. The repository path and C# namespace retained from the earlier preview are internal identifiers, not the product name.

The implementation uses C#/.NET 8 WPF, its own document model and renderer, and the `.moruproj` format. Earlier `.cwproj` files remain readable. The optional `.comp` bridge supports an explicitly limited subset of the upstream format. Algorithm and format mappings are documented in docs/PORTING.md.

Windows contributions are distributed under the MIT terms while preserving the original copyright and permission notice. Third-party runtime and model terms are listed in THIRD_PARTY_NOTICES.md and shipped with the package.
