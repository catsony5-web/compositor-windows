Set-StrictMode -Version Latest

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ProjectPath = Join-Path $script:RepositoryRoot 'src\Compositor.Windows.csproj'
$script:ReleaseRoot = Join-Path $script:RepositoryRoot 'release'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

function Assert-SafeChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify '$fullPath' because it is not below '$fullParent'."
    }

    return $fullPath
}

function Reset-GeneratedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Parent
    )

    $safePath = Assert-SafeChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Remove-GeneratedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Parent
    )

    $safePath = Assert-SafeChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Force
    }
}
