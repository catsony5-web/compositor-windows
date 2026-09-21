[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'ReleaseAutomation.ps1')

[xml] $project = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Compositor.Windows.csproj') -Raw
$version = [string] $project.Project.PropertyGroup.Version
$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cne $env:GITHUB_SHA) { throw 'Checkout does not match the workflow source SHA.' }
$paths = @()
$releases = @()
$tags = @{}
if ($env:GITHUB_EVENT_NAME -eq 'push' -and
    ($env:GITHUB_REF -ceq 'refs/heads/main' -or $env:GITHUB_REF.StartsWith('refs/tags/v'))) {
    foreach ($tag in @(& git tag --list)) {
        $target = (& git rev-parse "$tag^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve existing tag $tag." }
        $tags[$tag] = $target
    }
    $releases = @(Get-RepositoryReleases $env:GITHUB_REPOSITORY)
    foreach ($release in $releases) {
        if (-not $tags.ContainsKey($release.tag_name)) { $tags[$release.tag_name] = $release.target_commitish }
    }
}
if ($env:GITHUB_EVENT_NAME -eq 'push' -and $env:GITHUB_REF -ceq 'refs/heads/main') {
    $event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
    $baseline = [string] $event.before
    $nearestDistance = [long]::MaxValue
    # Include unpublished app changes even if this particular push edits only docs.
    foreach ($release in @($releases | Where-Object { Test-PublishedWindowsRelease $_ })) {
        $target = [string] $tags[$release.tag_name]
        if ($target -cnotmatch '^[a-f0-9]{40}$') { continue }
        & git merge-base --is-ancestor $target $commit
        if ($LASTEXITCODE -eq 1) { continue }
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect release ancestry.' }
        $distance = & git rev-list --count "$target..$commit"
        if ($LASTEXITCODE -ne 0) { throw 'Could not compare released source.' }
        if ([long] $distance -lt $nearestDistance) {
            $nearestDistance = [long] $distance
            $baseline = $target
        }
    }
    if ($baseline -cmatch '^0{40}$') {
        $paths = @(& git ls-tree -r --name-only $commit)
    }
    else {
        if ($baseline -cnotmatch '^[a-f0-9]{40}$') { throw 'Invalid push base SHA.' }
        $paths = @(& git diff --name-only $baseline $commit)
    }
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the pushed application changes.' }
}
$plan = Get-ReleasePlan -SourceVersion $version -EventName $env:GITHUB_EVENT_NAME -Ref $env:GITHUB_REF `
    -Commit $commit -RunNumber $env:GITHUB_RUN_NUMBER -ChangedPaths $paths -ExistingTags $tags
foreach ($property in @('Version', 'Tag', 'Publish', 'Automatic', 'Prerelease', 'Commit')) {
    $value = $plan.$property
    if ($value -is [bool]) { $value = $value.ToString().ToLowerInvariant() }
    "$($property.ToLowerInvariant())=$value" |
        Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
Write-Host "Package $($plan.Version); publish=$($plan.Publish); automatic=$($plan.Automatic)"
