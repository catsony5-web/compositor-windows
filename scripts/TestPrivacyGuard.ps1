[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$guard = Join-Path $PSScriptRoot 'TestPrivacy.ps1'
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('morupixel-privacy-' + [guid]::NewGuid().ToString('N'))
$script:checks = 0

function Invoke-FixtureGit([string[]] $GitArguments) {
    $result = & git -c "safe.directory=$fixtureRoot" -C $fixtureRoot @GitArguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Privacy fixture Git operation failed.' }
    return $result
}
function Write-Fixture([string] $Path, [string] $Content) {
    $target = Join-Path $fixtureRoot $Path
    [System.IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
    [System.IO.File]::WriteAllText($target, $Content, (New-Object System.Text.UTF8Encoding($false)))
    Invoke-FixtureGit @('-c', 'core.autocrlf=false', 'add', '-f', '--', $Path) | Out-Null
}
function Clear-Index {
    Invoke-FixtureGit @('rm', '-r', '-f', '--cached', '--ignore-unmatch', '--', '.') | Out-Null
}
function Run-Guard([bool] $Index = $false) {
    $log = New-Object 'System.Collections.Generic.List[string]'
    $success = $true
    try { & $guard -RepositoryPath $fixtureRoot -Staged:$Index 6>&1 | ForEach-Object { $log.Add([string]$_) } }
    catch { $success = $false; $log.Add($_.Exception.Message) }
    return [pscustomobject]@{ Passed = $success; Output = ($log -join "`n") }
}
function Assert-Check([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw "Privacy guard fixture failed: $Name" }
    $script:checks++
}

New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    Invoke-FixtureGit @('init', '--quiet') | Out-Null
    Write-Fixture 'AGENTS.md' 'Public contributor instructions.'
    Write-Fixture '.env.example' 'API_KEY="your_public_placeholder"'
    Write-Fixture 'docs/.env.production.sample' 'API_KEY="replace_me_before_use"'
    Write-Fixture 'docs/setup.md' 'Use C:/Users/<user>/Apps or /home/username/Apps. License authors may use public@example.org.'
    Assert-Check (Run-Guard).Passed 'Public instructions, attribution and environment templates are accepted'
    Assert-Check (Run-Guard $true).Passed 'Index baseline is accepted'
    # Ignored/untracked local data must never be read by this guard.
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot '.env'), 'personal data')
    Assert-Check (Run-Guard).Passed 'Untracked local environment stays private and is not scanned'

    foreach ($privatePath in @('.env', '.env.production', 'nested/.env.local', 'production.env', 'nested/.env/variables',
        'CLAUDE.md', 'nested/CLAUDE.local.md', '.claude/settings.json', '.codex/config.toml', '.cursor/rules/personal.mdc',
        '.mcp.json', 'nested/.mcp.json', '.aws/credentials', '.ssh/config', 'credentials.json', 'client_secret_app.json',
        'service-account-dev.json', 'local.pfx', 'id_ed25519', '.npmrc', 'app.local.json', 'auth.json', 'secrets.dev.json')) {
        Clear-Index
        Write-Fixture $privatePath 'private configuration'
        $result = Run-Guard
        Assert-Check (-not $result.Passed -and $result.Output.Contains('private-file')) "Reject private filename $privatePath"
    }

    Clear-Index
    # Construct inert test credentials at runtime; never store real tokens or full token-shaped strings in source.
    $token = ('gh' + 'p_' + ('Ab12' * 9))
    Write-Fixture 'settings.txt' ('value=' + $token)
    $result = Run-Guard
    Assert-Check (-not $result.Passed -and $result.Output.Contains('github-token')) 'Recognize token in an ordinary text file'
    Assert-Check (-not $result.Output.Contains($token)) 'Token value is absent from all diagnostic output'
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'settings.txt'), 'value=removed')
    Assert-Check (Run-Guard).Passed 'Working tree scan sees the sanitized file'
    Assert-Check (-not (Run-Guard $true).Passed) 'Index scan still rejects an unsafe staged version'
    Remove-Item -LiteralPath (Join-Path $fixtureRoot 'settings.txt')
    Assert-Check (-not (Run-Guard $true).Passed) 'Index scan also checks staged files missing from disk'

    Clear-Index
    Write-Fixture '.env.example' ('API_KEY="' + $token + '"')
    Assert-Check (-not (Run-Guard).Passed) 'Public template suffix never exempts its content'

    $bareValue = ('Abc91Def82' * 3)
    foreach ($environment in @(
        @{ Path = '.env.example'; Prefix = 'API_KEY='; Suffix = '' },
        @{ Path = 'docs/.env.production.sample'; Prefix = 'export CUSTOM_API_KEY = '; Suffix = ' # setup value' },
        @{ Path = 'settings.env.example'; Prefix = 'SERVICE_CLIENT_SECRET='; Suffix = '' },
        @{ Path = 'docs/.env.sample'; Prefix = 'AWS_SECRET_ACCESS_KEY='; Suffix = '' }
    )) {
        Clear-Index
        Write-Fixture $environment.Path ($environment.Prefix + $bareValue + $environment.Suffix)
        $result = Run-Guard
        Assert-Check (-not $result.Passed -and $result.Output.Contains('environment-credential')) ('Unquoted credential is rejected in ' + $environment.Path)
        Assert-Check (-not $result.Output.Contains($bareValue)) 'Unquoted credential is absent from diagnostics'
        Assert-Check (-not (Run-Guard $true).Passed) 'Unquoted template credential is also rejected from the Git index'
    }
    Clear-Index
    Write-Fixture '.env.example' ('API_KEY=your_public_placeholder' + "`n" + 'CUSTOM_API_KEY=replace_me_before_use' + "`n" +
        'ACCESS_TOKEN=${EXTERNAL_ACCESS_TOKEN}' + "`n" + 'PASSWORD=%EXTERNAL_SECRET_VALUE%' + "`n" + 'CLIENT_SECRET=<provided-by-secret-store>')
    Assert-Check (Run-Guard).Passed 'Explicit unquoted placeholders and external environment references remain allowed'

    $samples = @(
        @{ Rule = 'private-key'; Value = ('-----' + 'BEGIN OPENSSH PRIVATE KEY' + '-----') },
        @{ Rule = 'cloud-access-key'; Value = ('AK' + 'IA' + ('A1' * 8)) },
        @{ Rule = 'google-api-key'; Value = ('AI' + 'za' + ('A' * 35)) },
        @{ Rule = 'slack-token'; Value = ('xo' + 'xb-' + ('1234-' * 6)) },
        @{ Rule = 'ai-api-key'; Value = ('s' + 'k-proj-' + ('Ab12' * 12)) },
        @{ Rule = 'literal-credential'; Value = ('api_key="' + ('Abc91Def82' * 3) + '"') },
        @{ Rule = 'personal-home-path'; Value = ('C:/' + 'Users/' + 'PrivacyFixturePerson' + '/App/settings.json') },
        @{ Rule = 'personal-home-path'; Value = ('C:\\' + 'Users\\' + 'PrivacyFixturePerson' + '\\App') },
        @{ Rule = 'personal-home-path'; Value = ('/Users/' + 'PrivacyFixturePerson' + '/App') },
        @{ Rule = 'personal-home-path'; Value = ('/home/' + 'PrivacyFixturePerson' + '/App') }
    )
    foreach ($sample in $samples) {
        Clear-Index
        Write-Fixture 'docs/fixture.txt' $sample.Value
        $result = Run-Guard
        Assert-Check (-not $result.Passed -and $result.Output.Contains($sample.Rule)) ("Detect " + $sample.Rule)
        Assert-Check (-not $result.Output.Contains($sample.Value)) ("Redact " + $sample.Rule)
    }

    Clear-Index
    Write-Fixture 'windows.txt' 'placeholder'
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'windows.txt'), $token, [System.Text.Encoding]::Unicode)
    Assert-Check (-not (Run-Guard).Passed) 'UTF-16 text is inspected before binary filtering'

    Clear-Index
    Write-Fixture 'fixture.bin' 'placeholder'
    [System.IO.File]::WriteAllBytes((Join-Path $fixtureRoot 'fixture.bin'), [byte[]]@(0, 128, 255, 1))
    Assert-Check (Run-Guard).Passed 'Binary assets do not produce false token findings'

    Clear-Index
    Write-Fixture 'large-encoded.cs' ('const string ImageData = "' + ('AbCdEF123456' * 4096) + '";')
    Assert-Check (Run-Guard).Passed 'Legitimate encoded source fixture is accepted'

    Clear-Index
    Write-Fixture 'scripts/TestPrivacy.ps1' ([System.IO.File]::ReadAllText($guard))
    Write-Fixture 'scripts/TestPrivacyGuard.ps1' ([System.IO.File]::ReadAllText($PSCommandPath))
    Assert-Check (Run-Guard).Passed 'The guard and its fixtures contain no secret-shaped literal or personal path'

    # The actual ignore policy protects local files, but permits explicitly public templates.
    Clear-Index
    Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '.gitignore') -Destination (Join-Path $fixtureRoot '.gitignore')
    foreach ($path in @('.env.local', 'docs/.env.production', 'CLAUDE.md', '.claude/settings.json', 'private.pem', '.mcp.json')) {
        $ignored = & git -c "safe.directory=$fixtureRoot" -C $fixtureRoot check-ignore -- $path
        Assert-Check ($LASTEXITCODE -eq 0) "Ignore local $path"
    }
    foreach ($path in @('.env.example', '.env.production.sample', 'docs/.env.example', 'AGENTS.md', 'licenses/license.txt')) {
        $ignored = & git -c "safe.directory=$fixtureRoot" -C $fixtureRoot check-ignore -- $path
        Assert-Check ($LASTEXITCODE -eq 1) "Allow public $path"
    }
    $package = Join-Path $fixtureRoot 'distribution'
    New-Item -ItemType Directory -Path $package | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $package 'README.txt'), 'Public package instructions')
    function Run-PackageGuard {
        $log = New-Object 'System.Collections.Generic.List[string]'
        $success = $true
        try { & $guard -PackagePath $package 6>&1 | ForEach-Object { $log.Add([string]$_) } }
        catch { $success = $false; $log.Add($_.Exception.Message) }
        return [pscustomobject]@{ Passed = $success; Output = ($log -join "`n") }
    }
    Assert-Check (Run-PackageGuard).Passed 'Package scans files without a Git index'
    [System.IO.File]::WriteAllText((Join-Path $package 'Morupixel.pdb'), 'symbols')
    Assert-Check (-not (Run-PackageGuard).Passed) 'Package excludes debug symbols'
    Remove-Item -LiteralPath (Join-Path $package 'Morupixel.pdb')
    [System.IO.File]::WriteAllText((Join-Path $package '.env'), 'private config')
    Assert-Check (-not (Run-PackageGuard).Passed) 'Untracked private file inside distribution is rejected'
    Remove-Item -LiteralPath (Join-Path $package '.env')
    $embedded = [byte[]]@(0, 128, 255) + [System.Text.Encoding]::Unicode.GetBytes($samples[-1].Value) + [byte[]]@(0, 0)
    [System.IO.File]::WriteAllBytes((Join-Path $package 'Morupixel.dll'), $embedded)
    Assert-Check (-not (Run-PackageGuard).Passed) 'Our binary is checked for embedded UTF-16 personal paths'
    Remove-Item -LiteralPath (Join-Path $package 'Morupixel.dll')
    Write-Host "$script:checks privacy guard checks passed."
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($fixtureRoot)
    $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temp, [System.StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^morupixel-privacy-[a-f0-9]{32}$') { throw 'Refusing to remove an unexpected privacy fixture path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
# Expected failing Git probes must not become the GitHub pwsh wrapper's exit code.
$global:LASTEXITCODE = 0
