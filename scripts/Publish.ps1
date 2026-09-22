[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $project = Get-Content -LiteralPath $ProjectPath -Raw
    $Version = [string] $project.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'No version was supplied and the project does not define <Version>.'
    }
}

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Test.ps1') -Configuration $Configuration
}

$runtime = 'win-x64'
$artifactBase = "Morupixel-$Version-$runtime"
$stagingParent = Join-Path $ReleaseRoot 'staging'
$stagingPath = Join-Path $stagingParent $artifactBase
$archivePath = Join-Path $ReleaseRoot "$artifactBase.zip"
$checksumPath = "$archivePath.sha256"

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stagingParent -Force | Out-Null
$stagingPath = Reset-GeneratedDirectory -Path $stagingPath -Parent $stagingParent
Remove-GeneratedFile -Path $archivePath -Parent $ReleaseRoot
Remove-GeneratedFile -Path $checksumPath -Parent $ReleaseRoot

Push-Location $RepositoryRoot
try {
    Invoke-DotNet @(
        'publish', $ProjectPath,
        '--configuration', $Configuration,
        '--runtime', $runtime,
        '--self-contained', 'true',
        '--output', $stagingPath,
        '--nologo',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        "-p:Version=$Version",
        '-p:ContinuousIntegrationBuild=true'
    )
}
finally {
    Pop-Location
}

Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -Destination (Join-Path $stagingPath 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -Destination (Join-Path $stagingPath 'LICENSE')

$packageDocuments = @(
    @{ Source = (Join-Path $RepositoryRoot 'assets\samples\README.md'); Destination = (Join-Path $stagingPath 'assets\samples\README.md') },
    @{ Source = (Join-Path $RepositoryRoot 'assets\fonts\README.md'); Destination = (Join-Path $stagingPath 'assets\fonts\README.md') },
    @{ Source = (Join-Path $RepositoryRoot 'licenses\Pretendard-LICENSE.txt'); Destination = (Join-Path $stagingPath 'licenses\Pretendard-LICENSE.txt') },
    @{ Source = (Join-Path $RepositoryRoot 'README.md'); Destination = (Join-Path $stagingPath 'README.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\editor.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\editor.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\bucket.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\bucket.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\new-document.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\new-document.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\adjustment.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\adjustment.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\colors.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\colors.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\brush.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\brush.png') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\compact.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\compact.png') },
    @{ Source = (Join-Path $RepositoryRoot 'CONTRIBUTING.md'); Destination = (Join-Path $stagingPath 'CONTRIBUTING.md') },
    @{ Source = (Join-Path $RepositoryRoot 'NOTICE.md'); Destination = (Join-Path $stagingPath 'NOTICE.md') },
    @{ Source = (Join-Path $RepositoryRoot 'THIRD_PARTY_NOTICES.md'); Destination = (Join-Path $stagingPath 'THIRD_PARTY_NOTICES.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\PORTING.md'); Destination = (Join-Path $stagingPath 'docs\PORTING.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\CMYK.md'); Destination = (Join-Path $stagingPath 'docs\CMYK.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\ARCHITECTURE.md'); Destination = (Join-Path $stagingPath 'docs\ARCHITECTURE.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\VALIDATION.md'); Destination = (Join-Path $stagingPath 'docs\VALIDATION.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\VALIDATION-0.1.md'); Destination = (Join-Path $stagingPath 'docs\VALIDATION-0.1.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\UPSTREAM_FEATURES.ko.md'); Destination = (Join-Path $stagingPath 'docs\UPSTREAM_FEATURES.ko.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\RELEASE_NOTES.md'); Destination = (Join-Path $stagingPath 'docs\RELEASE_NOTES.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\BACKGROUND_REMOVAL.md'); Destination = (Join-Path $stagingPath 'docs\BACKGROUND_REMOVAL.md') },
    @{ Source = (Join-Path $RepositoryRoot 'licenses\ONNXRuntime-LICENSE.txt'); Destination = (Join-Path $stagingPath 'licenses\ONNXRuntime-LICENSE.txt') },
    @{ Source = (Join-Path $RepositoryRoot 'licenses\ONNXRuntime-THIRD-PARTY-NOTICES.txt'); Destination = (Join-Path $stagingPath 'licenses\ONNXRuntime-THIRD-PARTY-NOTICES.txt') },
    @{ Source = (Join-Path $PSScriptRoot 'README.distribution.txt'); Destination = (Join-Path $stagingPath 'PACKAGING-README.txt') }
)

$modelPath = Join-Path $stagingPath 'models\u2netp.onnx'
foreach ($documentName in @('GUIDE', 'RELEASE_NOTES_ARCHIVE', 'FILE_COMPATIBILITY', 'MAINTENANCE', 'DESIGN_REFERENCES', 'AI_CONNECTION', 'INTEGRATION')) {
    $packageDocuments += @{ Source = (Join-Path $RepositoryRoot "docs\$documentName.md"); Destination = (Join-Path $stagingPath "docs\$documentName.md") }
}
foreach ($licenseName in @('ACadSharp-LICENSE.txt', 'PsdSharp-LICENSE.txt', 'psd-tools-LICENSE.txt', 'CsWinRT-LICENSE.txt', 'WindowsSDK-License.rtf', 'PDFsharp-LICENSE.txt', 'Microsoft-PdfDependencies-LICENSE.txt')) {
    $packageDocuments += @{ Source = (Join-Path $RepositoryRoot "licenses\$licenseName"); Destination = (Join-Path $stagingPath "licenses\$licenseName") }
}
foreach ($imageName in @('startup', 'save-changes', 'color-palette', 'text-properties', 'shape-properties', 'image-properties', 'design', 'brush-settings', 'photo-develop', 'quick-exposure', 'quick-levels', 'quick-saturation', 'quick-blur')) {
    $packageDocuments += @{ Source = (Join-Path $RepositoryRoot "docs\screenshots\$imageName.png"); Destination = (Join-Path $stagingPath "docs\screenshots\$imageName.png") }
}
if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne '309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8') {
    throw 'The bundled U2NetP model is missing or its pinned checksum differs.'
}

foreach ($document in $packageDocuments) {
    if (-not (Test-Path -LiteralPath $document.Source -PathType Leaf)) {
        throw "Required package document is missing: $($document.Source)"
    }
    $destinationDirectory = Split-Path -Parent $document.Destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $document.Source -Destination $document.Destination
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetDocuments = @(
    @{ Source = (Join-Path $dotnetRoot 'LICENSE.txt'); Destination = (Join-Path $stagingPath 'DOTNET-LICENSE.txt') },
    @{ Source = (Join-Path $dotnetRoot 'ThirdPartyNotices.txt'); Destination = (Join-Path $stagingPath 'DOTNET-THIRD-PARTY-NOTICES.txt') }
)

foreach ($document in $dotnetDocuments) {
    if (-not (Test-Path -LiteralPath $document.Source -PathType Leaf)) {
        throw "The .NET SDK notice file is missing: $($document.Source)"
    }
    Copy-Item -LiteralPath $document.Source -Destination $document.Destination
}

$publishedTestDirectory = Join-Path $ArtifactRoot 'published-self-test'
$publishedTestDirectory = Reset-GeneratedDirectory -Path $publishedTestDirectory -Parent $ArtifactRoot
$publishedReportPath = Join-Path $publishedTestDirectory 'self-test.txt'
$publishedExe = Join-Path $stagingPath 'Morupixel.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "Published executable is missing: $publishedExe"
}
$quotedReportPath = '"' + $publishedReportPath + '"'
$process = Start-Process -FilePath $publishedExe -ArgumentList @('--self-test', $quotedReportPath) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Published self-test exited with code $($process.ExitCode). See $publishedReportPath"
}
if (-not (Test-Path -LiteralPath $publishedReportPath -PathType Leaf) -or
    (Get-Item -LiteralPath $publishedReportPath).Length -eq 0) {
    throw "Published self-test did not create a non-empty report: $publishedReportPath"
}

Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$hash  $([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)"
[System.IO.File]::WriteAllText($checksumPath, $checksumLine, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Release archive: $archivePath"
Write-Host "SHA-256 file:   $checksumPath"
