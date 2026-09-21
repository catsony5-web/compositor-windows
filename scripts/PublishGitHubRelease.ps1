[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $Commit,
    [string] $PackageDirectory = 'release-download',
    [switch] $Automatic
)
. (Join-Path $PSScriptRoot 'ReleaseAutomation.ps1')
$repository = $env:GITHUB_REPOSITORY
if ($repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
$plan = Get-ReleasePlan -SourceVersion $Version -EventName push -Ref "refs/tags/v$Version" -Commit $Commit
if ($Automatic -and -not $plan.Prerelease) { throw 'Automatic releases must remain previews.' }
if ((& git rev-parse HEAD).Trim() -cne $Commit) { throw 'Release checkout does not match the tested source commit.' }
$tag = $plan.Tag
$archiveName = "Morupixel-$Version-win-x64.zip"
$zipPath = Join-Path $PackageDirectory $archiveName
$checksumPath = "$zipPath.sha256"
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw 'The tested ZIP and its checksum are required.'
}
$localHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Checksum -Text (Get-Content -LiteralPath $checksumPath -Raw) -ArchiveName $archiveName -ExpectedHash $localHash

# A retry may inspect or finish this release, but never replaces existing bytes.
$release = @(Get-RepositoryReleases $repository | Where-Object tag_name -CEQ $tag)
if ($release.Count -gt 1) { throw 'Multiple releases matched the tag.' }
$remoteRefs = @(Invoke-ReleaseGh @('api', "repos/$repository/git/matching-refs/tags/$tag") | ConvertFrom-Json)
$resolvedCommit = ''
if (@($remoteRefs | Where-Object ref -CEQ "refs/tags/$tag").Count -gt 0) {
    $resolvedCommit = (Invoke-ReleaseGh @('api', "repos/$repository/commits/$tag", '--jq', '.sha')).Trim()
    if ($resolvedCommit -cne $Commit) { throw 'The remote tag points to different source; refusing to release.' }
}
if ($release.Count -eq 0) {
    if (-not $Automatic -and -not $resolvedCommit) { throw 'A manual release requires an existing verified tag.' }
    $notes = @"
Windows image editing, in one workspace.

[Download for Windows x64](https://github.com/$repository/releases/download/$tag/$archiveName) · [Website](https://morupixel.arch-t.chatgpt.site/)

Extract the ZIP and run ``Morupixel.exe``. Development preview · Unsigned.

[Changes](https://github.com/$repository/commit/$Commit) · [Project notes](https://github.com/$repository/blob/$Commit/docs/PORTING.md) · [Attribution](https://github.com/$repository/blob/$Commit/NOTICE.md)

<!-- morupixel-source:$Commit -->
"@
    if (-not $plan.Prerelease) { $notes = $notes.Replace('Development preview · Unsigned.', 'Unsigned Windows build.') }
    $notesPath = Join-Path $PackageDirectory 'release-notes.generated.md'
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($notesPath), $notes)
    $arguments = @('release', 'create', $tag, '--repo', $repository, '--draft', '--target', $Commit,
        '--title', "Morupixel $Version", '--notes-file', $notesPath)
    if ($plan.Prerelease) { $arguments += '--prerelease' }
    Invoke-ReleaseGh $arguments | Write-Host
    $release = @(Get-RepositoryReleases $repository | Where-Object tag_name -CEQ $tag)
}
if ($release.Count -ne 1) { throw 'Could not find the draft release.' }
$release = $release[0]
Assert-ExistingRelease -Release $release -ExpectedCommit $Commit -ResolvedTagCommit $resolvedCommit
if ($release.draft) {
    foreach ($file in @($zipPath, $checksumPath)) {
        $name = [IO.Path]::GetFileName($file)
        $existing = @($release.assets | Where-Object name -CEQ $name)
        if ($existing.Count -eq 0) {
            # If one half exists, a retry must not attach a checksum for different ZIP bytes.
            if ($name.EndsWith('.sha256') -and @($release.assets | Where-Object name -CEQ $archiveName).Count -eq 1) {
                $oldZip = @($release.assets | Where-Object name -CEQ $archiveName)[0]
                if ($oldZip.digest -cne "sha256:$localHash") { throw 'An incomplete draft has different ZIP bytes; manual review is required.' }
            }
            Invoke-ReleaseGh @('release', 'upload', $tag, '--repo', $repository, $file) | Write-Host
        }
    }
}
$release = Invoke-ReleaseGh @('api', "repos/$repository/releases/$($release.id)") | ConvertFrom-Json
$remoteHash = Assert-ReleaseAssets -Assets $release.assets -ArchiveName $archiveName
$verifyDirectory = Join-Path $PackageDirectory ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verifyDirectory | Out-Null
Invoke-ReleaseGh @('release', 'download', $tag, '--repo', $repository, '--pattern', "$archiveName.sha256", '--dir', $verifyDirectory) | Out-Null
Assert-Checksum -Text (Get-Content -LiteralPath (Join-Path $verifyDirectory "$archiveName.sha256") -Raw) `
    -ArchiveName $archiveName -ExpectedHash $remoteHash
if ($release.draft) {
    Invoke-ReleaseGh @('release', 'edit', $tag, '--repo', $repository, '--draft=false') | Write-Host
}
$published = Invoke-ReleaseGh @('api', "repos/$repository/releases/$($release.id)") | ConvertFrom-Json
$actualCommit = (Invoke-ReleaseGh @('api', "repos/$repository/commits/$tag", '--jq', '.sha')).Trim()
if ($published.draft -or $actualCommit -cne $Commit) { throw 'Final publication or source verification failed.' }
Assert-ReleaseAssets -Assets $published.assets -ArchiveName $archiveName | Out-Null
Write-Host "Verified release: $($published.html_url) ($Commit). Existing assets were never replaced."
