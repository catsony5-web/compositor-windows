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
$artifactBase = "Compositor.Windows-$Version-$runtime"
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
    @{ Source = (Join-Path $RepositoryRoot 'README.md'); Destination = (Join-Path $stagingPath 'README.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\screenshots\editor.png'); Destination = (Join-Path $stagingPath 'docs\screenshots\editor.png') },
    @{ Source = (Join-Path $RepositoryRoot 'CONTRIBUTING.md'); Destination = (Join-Path $stagingPath 'CONTRIBUTING.md') },
    @{ Source = (Join-Path $RepositoryRoot 'NOTICE.md'); Destination = (Join-Path $stagingPath 'NOTICE.md') },
    @{ Source = (Join-Path $RepositoryRoot 'THIRD_PARTY_NOTICES.md'); Destination = (Join-Path $stagingPath 'THIRD_PARTY_NOTICES.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\PORTING.md'); Destination = (Join-Path $stagingPath 'docs\PORTING.md') },
    @{ Source = (Join-Path $RepositoryRoot 'docs\RELEASE_NOTES.md'); Destination = (Join-Path $stagingPath 'docs\RELEASE_NOTES.md') },
    @{ Source = (Join-Path $PSScriptRoot 'README.distribution.txt'); Destination = (Join-Path $stagingPath 'PACKAGING-README.txt') }
)

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

$publishedTestDirectory = Join-Path $ReleaseRoot 'published-self-test'
$publishedTestDirectory = Reset-GeneratedDirectory -Path $publishedTestDirectory -Parent $ReleaseRoot
$publishedReportPath = Join-Path $publishedTestDirectory 'self-test.txt'
$publishedExe = Join-Path $stagingPath 'Compositor.Windows.exe'
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
