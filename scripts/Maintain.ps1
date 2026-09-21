[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [switch] $CleanBuildCaches,
    [switch] $KeepCurrentBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

# Deliberately finite allowlist: archives, portable applications, project documents,
# source, models and test reports are never cleanup targets.
$cachePaths = @(
    'src\bin', 'src\obj', 'artifacts\interaction-build',
    'artifacts\advanced-harness\bin', 'artifacts\advanced-harness\obj',
    'artifacts\interaction-harness\bin', 'artifacts\interaction-harness\obj',
    'artifacts\retouch-benchmark\bin', 'artifacts\retouch-benchmark\obj',
    'release\io-harness\bin', 'release\io-harness\obj',
    'release\test-harness-layer-export\bin', 'release\test-harness-layer-export\obj',
    'release\text-panel-qa\bin', 'release\text-panel-qa\obj',
    'release\test-results\color-palette-runner\bin', 'release\test-results\color-palette-runner\obj',
    'benchmarks\Interaction\bin', 'benchmarks\Interaction\obj'
)
foreach ($name in @('advanced', 'interaction', 'retouch', 'io', 'layer-export', 'text-panel', 'color-palette', 'compatibility', 'panel-layout')) {
    $cachePaths += "tools\qa\$name\bin", "tools\qa\$name\obj"
}
if ($KeepCurrentBuild) {
    $cachePaths = @($cachePaths | Where-Object { $_ -notin @('src\bin', 'src\obj') })
}

function Get-InventoryFiles {
    param([string] $Directory)
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            # Never traverse junctions, symlinks or hydrate cloud placeholders.
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) } else { $entry }
        }
    }
}

$filesBefore = @(Get-InventoryFiles -Directory $RepositoryRoot)
$totalBefore = [long](($filesBefore | Measure-Object -Property Length -Sum).Sum)
$changes = [System.Collections.Generic.List[object]]::new()
foreach ($relative in $cachePaths) {
    $path = Assert-SafeChildPath -Path (Join-Path $RepositoryRoot $relative) -Parent $RepositoryRoot
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
    $prefix = $path.TrimEnd('\') + '\'
    $files = @($filesBefore | Where-Object { $_.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    $bytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    $entry = [pscustomobject]@{ Path = $relative; Files = $files.Count; Bytes = $bytes; Status = 'Candidate'; Reason = '' }
    if ($CleanBuildCaches -and $PSCmdlet.ShouldProcess($path, 'Remove reproducible build cache')) {
        $deletionStarted = $false
        try {
            Assert-NoGeneratedReparsePoints -Path $path
            Assert-GeneratedPathNotInUse -Path $path
            # Check all file locks before touching any file in this candidate.
            # These are snapshot checks, not a lock across the entire cleanup.
            # Do not start builds while cleaning their output directories.
            foreach ($file in $files) {
                $probe = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
            }
            $deletionStarted = $true
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            $entry.Status = 'Removed'
        } catch {
            $entry.Status = if ($deletionStarted) { 'Incomplete' } else { 'Preserved' }
            $entry.Reason = $_.Exception.Message
            Write-Warning "$($entry.Status) $relative : $($entry.Reason)"
        }
    }
    $changes.Add($entry)
}

$filesAfter = if ($CleanBuildCaches) { @(Get-InventoryFiles -Directory $RepositoryRoot) } else { $filesBefore }
$totalAfter = [long](($filesAfter | Measure-Object -Property Length -Sum).Sum)
$rootPrefix = $RepositoryRoot.TrimEnd('\') + '\'
$folders = $filesAfter | Group-Object { $_.FullName.Substring($rootPrefix.Length).Split('\')[0] } | ForEach-Object {
    [pscustomobject]@{ Path = $_.Name; Files = $_.Count; Bytes = [long](($_.Group | Measure-Object Length -Sum).Sum) }
} | Sort-Object Bytes -Descending
$report = [pscustomobject]@{
    Time = [DateTimeOffset]::Now.ToString('O'); Root = $RepositoryRoot
    Mode = if ($CleanBuildCaches) { 'CleanBuildCaches' } else { 'AuditOnly' }
    KeepCurrentBuild = [bool] $KeepCurrentBuild
    Measure = 'Logical file lengths; reparse/cloud placeholders excluded; not physical allocated disk space'
    BeforeBytes = $totalBefore; AfterBytes = $totalAfter; RemovedBytes = $totalBefore - $totalAfter
    Folders = @($folders); CacheCandidates = @($changes.ToArray())
}
if (-not $WhatIfPreference) {
    $reportRoot = Assert-SafeChildPath -Path (Join-Path $ArtifactRoot 'maintenance') -Parent $RepositoryRoot
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $reportPath = Join-Path $reportRoot ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
$changes | Select-Object Path, @{Name='MiB';Expression={[math]::Round($_.Bytes / 1MB, 2)}}, Status | Format-Table -AutoSize
Write-Host "Logical total: $([math]::Round($totalAfter / 1GB, 3)) GiB. Removed: $([math]::Round(($totalBefore - $totalAfter) / 1MB, 2)) MiB."
if ($WhatIfPreference) { Write-Host 'WhatIf: no files removed or report written.' }
else { Write-Host "Report: $reportPath" }
