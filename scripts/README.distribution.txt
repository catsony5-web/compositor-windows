Morupixel / 모루픽셀
===================

Extract the entire ZIP, then run Morupixel.exe.
The Windows x64 package contains .NET 8 and an offline U²-NetP AI model.
No separate .NET installation or image upload is required.

AI background removal requires the Microsoft Visual C++ x64 runtime
(2019 or later/current supported release). It is not included in this ZIP.
If the AI engine fails to load, install the official x64 package and restart:
https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

Keep the models folder next to the executable. Copying only the EXE will
omit required runtime files and the AI model.

Verify package SHA-256:
  Get-FileHash .\Morupixel-*-win-x64.zip -Algorithm SHA256

Run checks without showing the UI:
  .\Morupixel.exe --self-test .\self-test.txt

Render a noninteractive layout preview:
  .\Morupixel.exe --render-preview .\preview.png

This preview is not a substitute for interactive UI testing.
See README.md, docs/PORTING.md and docs/RELEASE_NOTES.md for supported
features, interoperability limits and validation status.

Licenses: LICENSE, NOTICE.md, THIRD_PARTY_NOTICES.md,
DOTNET-LICENSE.txt, DOTNET-THIRD-PARTY-NOTICES.txt,
licenses/ONNXRuntime-* and models/U2NET-LICENSE.txt.
