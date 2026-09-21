Compositor for Windows
======================

Requirements
------------
Windows 10 or later, 64-bit. The application is self-contained and does not
require a separate .NET installation.

Run
---
Extract the entire ZIP archive, then run Compositor.Windows.exe.

Verify the download
-------------------
The release includes a matching .zip.sha256 file. In PowerShell, run:

  Get-FileHash .\Compositor.Windows-*-win-x64.zip -Algorithm SHA256

Compare the result with the hash in the .sha256 file.

Diagnostics
-----------
To run the built-in checks without opening the user interface:

  .\Compositor.Windows.exe --self-test .\self-test.txt

The process returns zero when the checks pass and writes details to the path
provided.

License information is in LICENSE.txt, NOTICE.md, THIRD_PARTY_NOTICES.md,
DOTNET-LICENSE.txt, and DOTNET-THIRD-PARTY-NOTICES.txt.
