[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Push-Location $RepositoryRoot
try {
    Invoke-DotNet @('restore', $ProjectPath)
    Invoke-DotNet @(
        'build', $ProjectPath,
        '--configuration', $Configuration,
        '--no-restore',
        '--nologo',
        '-p:ContinuousIntegrationBuild=true'
    )
}
finally {
    Pop-Location
}
