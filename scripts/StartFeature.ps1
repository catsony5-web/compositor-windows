[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{1,55}$')][string] $Name)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$trustedRepository = 'safe.directory=' + $repository.Replace('\', '/')
$worktreeParent = Join-Path ([IO.Path]::GetDirectoryName($repository)) 'morupixel-features'
$target = Join-Path $worktreeParent $Name
if (Test-Path -LiteralPath $target) { throw "Worktree path already exists: $target" }
& git -c $trustedRepository -C $repository fetch origin main
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed; do not start from a stale local branch.' }
& git -c $trustedRepository -C $repository worktree add -b "codex/$Name" $target origin/main
if ($LASTEXITCODE -ne 0) { throw 'Could not create the feature worktree.' }
Write-Host "Feature branch: codex/$Name"
Write-Host "Workspace: $target"
Write-Host 'Merge through a checked pull request into main. Public releases come from main only.'
