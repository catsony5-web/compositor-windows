Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-AppChange {
    param([string[]] $Paths)
    return @($Paths | Where-Object {
        $_ -cmatch '^(src/|assets/|models/|licenses/|global\.json$|scripts/(Build|Publish|Common|Test)\.ps1$)'
    }).Count -gt 0
}

function Get-ReleasePlan {
    param(
        [string] $SourceVersion, [string] $EventName, [string] $Ref,
        [string] $Commit, [long] $RunNumber, [string[]] $ChangedPaths,
        [hashtable] $ExistingTags = @{}
    )
    if ($SourceVersion -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
        throw 'The project version must be a SemVer without build metadata.'
    }
    if ($SourceVersion.Contains('-')) {
        foreach ($identifier in $SourceVersion.Substring($SourceVersion.IndexOf('-') + 1).Split('.')) {
            if ($identifier -cmatch '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
        }
    }
    if ($Commit -cnotmatch '^[a-f0-9]{40}$') { throw 'Expected the exact source commit SHA.' }
    $publish = $false
    $automatic = $false
    $version = $SourceVersion
    if ($EventName -eq 'push' -and $Ref.StartsWith('refs/tags/v')) {
        if ($Ref -cne "refs/tags/v$SourceVersion") { throw 'The pushed tag must match the project version.' }
        $publish = $true
    }
    elseif ($EventName -eq 'push' -and $Ref -ceq 'refs/heads/main' -and (Test-AppChange $ChangedPaths)) {
        if ($RunNumber -lt 1) { throw 'Automatic releases require a positive workflow run number.' }
        $publish = $true
        $automatic = $true
        if (-not $version.Contains('-')) {
            if ($ExistingTags.ContainsKey("v$version") -and $ExistingTags["v$version"] -cne $Commit) {
                # A preview of an already released stable version sorts below it.
                # Advance the patch so the website can discover this newer work.
                $parts = $version.Split('.')
                $parts[2] = ([long]::Parse($parts[2]) + 1).ToString([Globalization.CultureInfo]::InvariantCulture)
                $version = $parts -join '.'
            }
            $version += "-preview.build.$RunNumber"
        }
        elseif ($ExistingTags.ContainsKey("v$version") -and $ExistingTags["v$version"] -cne $Commit) {
            $version += ".build.$RunNumber"
        }
    }
    $tag = "v$version"
    if ($publish -and $ExistingTags.ContainsKey($tag) -and $ExistingTags[$tag] -cne $Commit) {
        throw "The selected release tag $tag belongs to another commit; it will not be replaced."
    }
    return [pscustomobject]@{
        Version = $version; Tag = $tag; Publish = $publish; Automatic = $automatic
        Prerelease = $version.Contains('-'); Commit = $Commit
    }
}

function Invoke-ReleaseGh {
    param([string[]] $Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed ($LASTEXITCODE): $($Arguments[0]) $($Arguments[1])" }
    return ($output -join "`n")
}

function Get-RepositoryReleases {
    param([string] $Repository)
    $pages = Invoke-ReleaseGh @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
    return @($pages | ForEach-Object { $_ } | ForEach-Object { $_ })
}

function Assert-ReleaseAssets {
    param([object[]] $Assets, [string] $ArchiveName)
    $zip = @($Assets | Where-Object name -CEQ $ArchiveName)
    $checksum = @($Assets | Where-Object name -CEQ "$ArchiveName.sha256")
    if ($zip.Count -ne 1 -or $checksum.Count -ne 1 -or $zip[0].size -le 0 -or $checksum[0].size -le 0 -or
        $zip[0].state -ne 'uploaded' -or $checksum[0].state -ne 'uploaded' -or
        $zip[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$') {
        throw 'The release must contain a complete ZIP and checksum, including the GitHub ZIP digest.'
    }
    return $zip[0].digest.Substring(7)
}

function Test-PublishedWindowsRelease {
    param([object] $Release)
    try {
        if ($Release.draft -or $Release.tag_name -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
            return $false
        }
        $archiveName = "Morupixel-$($Release.tag_name.Substring(1))-win-x64.zip"
        Assert-ReleaseAssets -Assets $Release.assets -ArchiveName $archiveName | Out-Null
        return $true
    }
    catch {
        # Notes-only, partial, or malformed releases are not a shipped app baseline.
        return $false
    }
}

function Assert-Checksum {
    param([string] $Text, [string] $ArchiveName, [string] $ExpectedHash)
    $pattern = '^([a-fA-F0-9]{64})[ \t]+\*?' + [regex]::Escape($ArchiveName) + '\s*$'
    if ($Text -notmatch $pattern -or $Matches[1].ToLowerInvariant() -cne $ExpectedHash.ToLowerInvariant()) {
        throw 'The SHA-256 file does not match the ZIP name and digest.'
    }
}

function Assert-ExistingRelease {
    param([object] $Release, [string] $ExpectedCommit, [string] $ResolvedTagCommit)
    if ($ResolvedTagCommit) {
        if ($ResolvedTagCommit -cne $ExpectedCommit) { throw 'The existing tag points to a different source commit.' }
    }
    elseif ($Release.target_commitish -cne $ExpectedCommit) {
        throw 'The draft release does not target the exact source commit.'
    }
    if ($Release.draft -and $Release.body -notlike "*<!-- morupixel-source:$ExpectedCommit -->*") {
        throw 'Refusing to modify a draft not created for this source by release automation.'
    }
}
