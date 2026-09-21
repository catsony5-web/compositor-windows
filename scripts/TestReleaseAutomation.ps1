[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'ReleaseAutomation.ps1')
$script:passed = 0
function Assert-Equal($Expected, $Actual, [string] $Name) {
    if ($Expected -cne $Actual) { throw "$Name : expected '$Expected', got '$Actual'." }
    $script:passed++
}
function Assert-Throws([scriptblock] $Action, [string] $Name) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw "$Name : expected failure." }
    $script:passed++
}
$sha = 'a' * 40
$other = 'b' * 40
$base = @{ SourceVersion = '0.2.0-preview.14'; EventName = 'push'; Ref = 'refs/heads/main'; Commit = $sha; RunNumber = 42; ChangedPaths = @('src/App/MainWindow.cs') }
Assert-Equal $true (Test-AppChange @('src/App/MainWindow.cs')) 'Source edits trigger a release'
Assert-Equal $true (Test-AppChange @('models/u2netp.onnx')) 'Model edits trigger a release'
Assert-Equal $true (Test-AppChange @('scripts/Publish.ps1')) 'Packaging edits trigger a release'
Assert-Equal $false (Test-AppChange @('README.md', 'docs/a.png', '.github/workflows/ci.yml', 'scripts/ReleaseAutomation.ps1')) 'Docs and automation setup do not release old app code'
$plan = Get-ReleasePlan @base
Assert-Equal '0.2.0-preview.14' $plan.Version 'Unused source version is preserved'
Assert-Equal $true $plan.Publish 'Main application push publishes'
Assert-Equal $true $plan.Prerelease 'Automatic release remains a preview'
Assert-Equal '0.2.0-preview.14.build.42' (Get-ReleasePlan @base -ExistingTags @{ 'v0.2.0-preview.14' = $other }).Version 'A used version receives unique build number'
Assert-Equal '0.2.0-preview.14' (Get-ReleasePlan @base -ExistingTags @{ 'v0.2.0-preview.14' = $sha }).Version 'Retry of same commit reuses its release'
Assert-Equal '0.2.0-preview.14.build.42' (Get-ReleasePlan @base -ExistingTags @{ 'v0.2.0-preview.14' = $other; 'v0.2.0-preview.14.build.42' = $sha }).Version 'Retry keeps build suffix'
Assert-Throws { Get-ReleasePlan @base -ExistingTags @{ 'v0.2.0-preview.14' = $other; 'v0.2.0-preview.14.build.42' = $other } } 'Never reuse tag belonging to different source'
$changed = $base.Clone(); $changed.SourceVersion = '1.0.0'
Assert-Equal '1.0.0-preview.build.42' (Get-ReleasePlan @changed).Version 'Stable source push does not create a stable release'
Assert-Equal '1.0.1-preview.build.42' (Get-ReleasePlan @changed -ExistingTags @{ 'v1.0.0' = $other }).Version 'Work after a published stable version sorts above it'
Assert-Equal $true (Get-ReleasePlan @changed -ExistingTags @{ 'v1.0.0' = $other }).Prerelease 'Next patch automatic release is still a preview'
Assert-Equal '1.0.1-preview.build.42' (Get-ReleasePlan @changed -ExistingTags @{ 'v1.0.0' = $other; 'v1.0.1-preview.build.42' = $sha }).Version 'Stable-source preview retry keeps its unique next patch tag'
$changed = $base.Clone(); $changed.ChangedPaths = @('docs/RELEASE_NOTES.md')
Assert-Equal $false (Get-ReleasePlan @changed).Publish 'Docs-only main push does not release'
$changed = $base.Clone(); $changed.Ref = 'refs/heads/codex/test'
Assert-Equal $false (Get-ReleasePlan @changed).Publish 'Feature branch does not release'
$changed = $base.Clone(); $changed.EventName = 'pull_request'; $changed.Ref = 'refs/pull/2/merge'
Assert-Equal $false (Get-ReleasePlan @changed).Publish 'Pull requests do not release'
$changed = $base.Clone(); $changed.EventName = 'workflow_dispatch'
Assert-Equal $false (Get-ReleasePlan @changed).Publish 'Manual CI rerun is not an accidental release'
$changed = $base.Clone(); $changed.Ref = 'refs/tags/v0.2.0-preview.14'
Assert-Equal $true (Get-ReleasePlan @changed).Publish 'Matching manual tag releases'
$changed = $base.Clone(); $changed.Ref = 'refs/tags/v0.2.0-preview.13'
Assert-Throws { Get-ReleasePlan @changed } 'Tag must match project version'
$changed = $base.Clone(); $changed.SourceVersion = '1.0.0'; $changed.Ref = 'refs/tags/v1.0.0'
Assert-Equal $false (Get-ReleasePlan @changed).Prerelease 'Explicit stable tag remains supported'
$changed = $base.Clone(); $changed.SourceVersion = '../invalid'
Assert-Throws { Get-ReleasePlan @changed } 'Invalid version cannot enter an asset path'

$archive = 'Morupixel-0.2.0-preview.14-win-x64.zip'
$hash = 'c' * 64
$assets = @(
    [pscustomobject]@{ name = $archive; size = 123; state = 'uploaded'; digest = "sha256:$hash" },
    [pscustomobject]@{ name = "$archive.sha256"; size = 106; state = 'uploaded'; digest = 'unused' }
)
Assert-Equal $hash (Assert-ReleaseAssets $assets $archive) 'Complete uploaded asset pair is accepted'
Assert-Throws { Assert-ReleaseAssets @($assets[0]) $archive } 'Missing checksum prevents publication'
Assert-Throws { Assert-ReleaseAssets @($assets[0], $assets[0], $assets[1]) $archive } 'Duplicate ZIP prevents publication'
$baseline = [pscustomobject]@{ draft = $false; tag_name = 'v0.2.0-preview.14'; assets = $assets }
Assert-Equal $true (Test-PublishedWindowsRelease $baseline) 'Complete published Windows package can be a baseline'
$baseline.draft = $true
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Draft packages cannot suppress unpublished app changes'
$baseline.draft = $false; $baseline.assets = @()
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Notes-only release cannot be a baseline'
$baseline.assets = @($assets[0])
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'ZIP without checksum cannot be a baseline'
$baseline.assets = @($assets[1])
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Checksum without ZIP cannot be a baseline'
$baseline.assets = $assets; $baseline.tag_name = 'v0.2.0-preview.15'
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Package names must match baseline release version'
$baseline.tag_name = 'v0.2.0-preview.14'; $assets[0].state = 'starter'
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Incomplete upload cannot be a baseline'
$assets[0].state = 'uploaded'; $assets[0].size = 0
Assert-Equal $false (Test-PublishedWindowsRelease $baseline) 'Empty ZIP cannot be a baseline'
$assets[0].size = 123
Assert-Equal 1 (@($baseline, [pscustomobject]@{draft = $false; tag_name = 'v0.2.0-preview.15'; assets = @()} |
    Where-Object { Test-PublishedWindowsRelease $_ }).Count) 'Newer notes-only release does not displace a complete candidate'
Assert-Checksum "$hash  $archive`n" $archive $hash; $script:passed++
Assert-Throws { Assert-Checksum "$hash  other.zip`n" $archive $hash } 'Checksum filename must match'
Assert-Throws { Assert-Checksum "$hash  $archive`n" $archive ('d' * 64) } 'Hash mismatch prevents publication'
$draft = [pscustomobject]@{ draft = $true; body = "<!-- morupixel-source:$sha -->"; target_commitish = $sha }
Assert-ExistingRelease $draft $sha ''; $script:passed++
Assert-Throws { Assert-ExistingRelease $draft $sha $other } 'Existing tag cannot target another commit'
$draft.body = 'Unrelated manually created draft'
Assert-Throws { Assert-ExistingRelease $draft $sha '' } 'Foreign draft cannot be overwritten'
$published = [pscustomobject]@{ draft = $false; body = 'Existing release'; target_commitish = 'main' }
Assert-ExistingRelease $published $sha $sha; $script:passed++
Write-Host "$script:passed release automation checks passed."
& (Join-Path $PSScriptRoot 'TestReleasePublishing.ps1')
