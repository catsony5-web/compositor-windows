# Pretendard fonts

The source tree keeps three unmodified static OpenType faces from Pretendard v1.3.9:

- `Pretendard-Regular.otf` — SHA-256: `3ffbacde6ab8411f1d2db54bb9b1f0b3ee2a738932033722cf0388c06aed1c93`
- `Pretendard-Medium.otf` — SHA-256: `d39e50e4bb52b4993b6a4eeb821a171254745bd824446af01e1f616b89fface0`
- `Pretendard-SemiBold.otf` — SHA-256: `c89bc43027dc7cde5726e96223376f8eec09302b2fc1f8147fd5b57cfc376118`

Source release: [orioncactus/pretendard v1.3.9](https://github.com/orioncactus/pretendard/releases/tag/v1.3.9), release archive [Pretendard-1.3.9.zip](https://github.com/orioncactus/pretendard/releases/download/v1.3.9/Pretendard-1.3.9.zip). The release tag resolves to commit `5c41199`. The files are copied from `public/static/` without modification.

The font family is `Pretendard`. Release packages do not embed these files: since Preview 7 the interface uses Windows system fonts and no code loads them. To use them again, add them back as `<Resource>` items in `src/Compositor.Windows.csproj`; WPF then resolves the family URI `/Morupixel;component/assets/fonts/#Pretendard` (with the appropriate face selected by `FontWeight`) without a system-wide font installation.

Pretendard is copyright © 2021 Kil Hyung-jin and is licensed under the SIL Open Font License, Version 1.1. The complete, unmodified license text is included at [licenses/Pretendard-LICENSE.txt](../../licenses/Pretendard-LICENSE.txt).

Starting with Preview 7, small interface text uses Windows Malgun Gothic with grayscale rendering. The OFL license stays with the source files and in release packages; existing document text styles are not rewritten.
