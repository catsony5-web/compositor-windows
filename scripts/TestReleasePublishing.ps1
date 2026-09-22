# Offline integration checks: every gh call is intercepted; no release is created.
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'ReleaseAutomation.ps1')
$commit = (& git rev-parse HEAD).Trim()
$version = '9.0.0-preview.42'
$archive = "Morupixel-$version-win-x64.zip"
$testRoot = Join-Path (Join-Path $PSScriptRoot '../artifacts') ('release-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$zipPath = Join-Path $testRoot $archive
[IO.File]::WriteAllText($zipPath, 'Small offline release fixture')
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = "$hash  $archive`n"
[IO.File]::WriteAllText("$zipPath.sha256", $checksumText)
$oldRepository = $env:GITHUB_REPOSITORY
$env:GITHUB_REPOSITORY = 'example/morupixel-test'
$global:MorupixelReleaseTestMock = $null
$script:flowPassed = 0

function gh {
    $a = @($args)
    $global:LASTEXITCODE = 0
    $global:MorupixelReleaseTestMock.Calls.Add(($a -join ' '))
    if ($a[0] -eq 'api') {
        $endpoint = @($a | Where-Object { $_ -like 'repos/*' })[0]
        if ($endpoint -like '*/compare/main...*') { return $global:MorupixelReleaseTestMock.MainlineStatus }
        if ($endpoint -like '*/releases?per_page=*') {
            $items = @(); if ($null -ne $global:MorupixelReleaseTestMock.Release) { $items += $global:MorupixelReleaseTestMock.Release }
            return ConvertTo-Json -InputObject @($items) -Depth 10 -Compress
        }
        if ($endpoint -like '*/git/matching-refs/*') {
            if ($global:MorupixelReleaseTestMock.TagCommit) {
                return ConvertTo-Json -InputObject @(@{ ref = "refs/tags/v$version" }) -Compress
            }
            return '[]'
        }
        if ($endpoint -like '*/commits/*') { return $global:MorupixelReleaseTestMock.TagCommit }
        if ($endpoint -like '*/releases/123') { return $global:MorupixelReleaseTestMock.Release | ConvertTo-Json -Depth 10 -Compress }
    }
    if ($a[0] -eq 'release') {
        switch ($a[1]) {
            'create' {
                if ('--draft' -notin $a -or '--prerelease' -notin $a) { throw 'Expected a draft preview.' }
                $global:MorupixelReleaseTestMock.Release = [pscustomobject]@{
                    id = 123; tag_name = "v$version"; target_commitish = $commit; draft = $true
                    body = "<!-- morupixel-source:$commit -->"; assets = @(); html_url = 'https://example.invalid/release'
                }
                return ''
            }
            'upload' {
                if ('--clobber' -in $a) { throw 'Assets must never be overwritten.' }
                $file = [string] $a[-1]
                $name = [IO.Path]::GetFileName($file)
                if (@($global:MorupixelReleaseTestMock.Release.assets | Where-Object name -EQ $name).Count -gt 0) { throw 'Duplicate upload attempted.' }
                if (-not $global:MorupixelReleaseTestMock.Release.draft) { throw 'Must upload only while draft.' }
                $global:MorupixelReleaseTestMock.Release.assets += [pscustomobject]@{
                    name = $name; state = 'uploaded'; size = (Get-Item -LiteralPath $file).Length
                    digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
                }
                return ''
            }
            'download' {
                $directory = [string] $a[([array]::IndexOf($a, '--dir') + 1)]
                [IO.File]::WriteAllText((Join-Path $directory "$archive.sha256"), $global:MorupixelReleaseTestMock.Checksum)
                $global:MorupixelReleaseTestMock.VerifiedDownload = $true
                return ''
            }
            'edit' {
                if (-not $global:MorupixelReleaseTestMock.VerifiedDownload -or $global:MorupixelReleaseTestMock.Release.assets.Count -ne 2) {
                    throw 'Publication occurred before checksum verification and both uploads.'
                }
                $global:MorupixelReleaseTestMock.Release.draft = $false
                $global:MorupixelReleaseTestMock.TagCommit = $commit
                return ''
            }
        }
    }
    throw "Unexpected mocked gh operation: $($a -join ' ')"
}

function New-Mock {
    return [pscustomobject]@{
        Release = $null; TagCommit = ''; Checksum = $checksumText; VerifiedDownload = $false; MainlineStatus = 'identical'
        Calls = [Collections.Generic.List[string]]::new()
    }
}
function Invoke-PublishFixture {
    & (Join-Path $PSScriptRoot 'PublishGitHubRelease.ps1') -Version $version -Commit $commit -PackageDirectory $testRoot -Automatic
}
function Expect-Failure([scriptblock] $Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected this publication attempt to fail.' }
    if (@($global:MorupixelReleaseTestMock.Calls | Where-Object { $_ -like 'release edit *' }).Count -gt 0) { throw 'A rejected fixture was published.' }
    $script:flowPassed++
}
try {
    foreach ($status in @('ahead', 'diverged')) {
        $global:MorupixelReleaseTestMock = New-Mock
        $global:MorupixelReleaseTestMock.MainlineStatus = $status
        Expect-Failure { Invoke-PublishFixture }
        if (@($global:MorupixelReleaseTestMock.Calls | Where-Object { $_ -like 'release *' }).Count -ne 0) { throw 'Unintegrated source reached a release mutation.' }
    }
    $global:MorupixelReleaseTestMock = New-Mock
    Invoke-PublishFixture
    if ($global:MorupixelReleaseTestMock.Release.draft -or $global:MorupixelReleaseTestMock.Release.assets.Count -ne 2) { throw 'New release was not completed.' }
    $script:flowPassed++

    $global:MorupixelReleaseTestMock.Calls.Clear()
    Invoke-PublishFixture
    if (@($global:MorupixelReleaseTestMock.Calls | Where-Object { $_ -match '^release (create|edit|upload) ' }).Count -ne 0) {
        throw 'A completed retry must not mutate the existing release.'
    }
    $script:flowPassed++

    $global:MorupixelReleaseTestMock.Release.draft = $true
    $global:MorupixelReleaseTestMock.Calls.Clear()
    Invoke-PublishFixture
    if (@($global:MorupixelReleaseTestMock.Calls | Where-Object { $_ -like 'release upload *' }).Count -ne 0) { throw 'Complete draft retry replaced assets.' }
    $script:flowPassed++

    $global:MorupixelReleaseTestMock.Calls.Clear(); $global:MorupixelReleaseTestMock.Release.draft = $true
    $global:MorupixelReleaseTestMock.Checksum = ('d' * 64) + "  $archive`n"
    Expect-Failure { Invoke-PublishFixture }

    $global:MorupixelReleaseTestMock.Calls.Clear(); $global:MorupixelReleaseTestMock.Checksum = $checksumText
    $global:MorupixelReleaseTestMock.TagCommit = 'b' * 40
    Expect-Failure { Invoke-PublishFixture }

    $global:MorupixelReleaseTestMock.Calls.Clear(); $global:MorupixelReleaseTestMock.TagCommit = $commit
    $global:MorupixelReleaseTestMock.Release.body = 'A manually authored draft'
    Expect-Failure { Invoke-PublishFixture }

    $global:MorupixelReleaseTestMock.Calls.Clear(); $global:MorupixelReleaseTestMock.Release.body = "<!-- morupixel-source:$commit -->"
    $global:MorupixelReleaseTestMock.Release.assets = @($global:MorupixelReleaseTestMock.Release.assets | Where-Object name -EQ $archive)
    $global:MorupixelReleaseTestMock.Release.assets[0].digest = 'sha256:' + ('e' * 64)
    Expect-Failure { Invoke-PublishFixture }
    Write-Host "$script:flowPassed offline publication scenarios passed."
}
finally {
    $env:GITHUB_REPOSITORY = $oldRepository
    Remove-Variable -Name MorupixelReleaseTestMock -Scope Global -ErrorAction SilentlyContinue
    # Fixtures stay under ignored artifacts/ for inspecting a failed local check.
}
