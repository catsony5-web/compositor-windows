Set-StrictMode -Version Latest

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ProjectPath = Join-Path $script:RepositoryRoot 'src\Compositor.Windows.csproj'
$script:ReleaseRoot = Join-Path $script:RepositoryRoot 'release'
$script:ArtifactRoot = Join-Path $script:RepositoryRoot 'artifacts'

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

    # Checking every existing ancestor also catches a junction between the
    # repository and a generated child whose final component does not exist yet.
    $ancestor = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to modify '$fullPath' through reparse point '$ancestor'."
            }
        }
        $ancestor = Split-Path -Parent $ancestor
    }

    return $fullPath
}

function Assert-GeneratedPathNotInUse {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $prefix = $fullPath + [System.IO.Path]::DirectorySeparatorChar
    # Get-Process works without the WMI/CIM permission needed for command lines.
    # Inspect loaded modules for framework-dependent dotnet.exe applications.
    # Never close an editor to make cleanup succeed.
    $processes = @(Get-Process -ErrorAction Stop)
    foreach ($process in $processes) {
        if ($process.Id -eq $PID) { continue }
        $relevantHost = $process.ProcessName -match '^(Morupixel|Compositor\.Windows|dotnet|MSBuild|VBCSCompiler)$'
        $executable = ''
        try { $executable = [string] $process.Path } catch {
            if ($relevantHost) { throw "Cannot inspect $($process.ProcessName) (PID $($process.Id)); generated files were preserved." }
        }
        $usesPath = $executable.Equals($fullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $executable.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $usesPath -and $relevantHost) {
            $modulePaths = @()
            try { $modulePaths = @($process.Modules | ForEach-Object { $_.FileName }) } catch {
                throw "Cannot inspect loaded files for $($process.ProcessName) (PID $($process.Id)); generated files were preserved."
            }
            if ($modulePaths.Count -eq 0) {
                throw "Cannot verify loaded files for $($process.ProcessName) (PID $($process.Id)); generated files were preserved."
            }
            foreach ($modulePath in $modulePaths) {
                if ($modulePath.Equals($fullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $modulePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $usesPath = $true
                    break
                }
            }
            # A build may be writing output without loading it as a module yet.
            # Idle Roslyn VBCSCompiler hosts are safe; active MSBuild hosts are not.
            if (-not $usesPath -and ($modulePaths | Where-Object { [System.IO.Path]::GetFileName($_) -match '^MSBuild\.(dll|exe)$' })) {
                throw "An MSBuild host (PID $($process.Id)) is running; wait for the build before modifying generated output."
            }
        }
        if ($usesPath) {
            throw "Refusing to modify '$fullPath' while $($process.ProcessName) (PID $($process.Id)) is using it. Keep the current application open and choose another output/version."
        }
    }
}

function Assert-NoGeneratedReparsePoints {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    # Enumerate one level at a time so a junction is rejected before traversal.
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove generated files containing reparse point '$($child.FullName)'."
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
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
        if (-not (Test-Path -LiteralPath $safePath -PathType Container)) {
            throw "Expected a generated directory at '$safePath'."
        }
        Assert-NoGeneratedReparsePoints -Path $safePath
        Assert-GeneratedPathNotInUse -Path $safePath
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
        if (-not (Test-Path -LiteralPath $safePath -PathType Leaf)) {
            throw "Expected a generated file at '$safePath'."
        }
        Assert-GeneratedPathNotInUse -Path $safePath
        Remove-Item -LiteralPath $safePath -Force
    }
}
