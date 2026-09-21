[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration
}

$reportDirectory = Join-Path $ArtifactRoot 'test-results'
$reportPath = Join-Path $reportDirectory 'self-test.txt'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
Remove-GeneratedFile -Path $reportPath -Parent $reportDirectory

Push-Location $RepositoryRoot
try {
    Invoke-DotNet @(
        'run',
        '--project', $ProjectPath,
        '--configuration', $Configuration,
        '--no-build',
        '--',
        '--self-test', $reportPath
    )
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "Self-test succeeded without creating the expected report: $reportPath"
}

if ((Get-Item -LiteralPath $reportPath).Length -eq 0) {
    throw "Self-test created an empty report: $reportPath"
}

Write-Host "Self-test report: $reportPath"
