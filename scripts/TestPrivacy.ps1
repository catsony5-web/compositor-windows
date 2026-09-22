[CmdletBinding()]
param(
    [string] $RepositoryPath = (Split-Path $PSScriptRoot -Parent),
    # A distribution directory is scanned in full, including otherwise untracked files.
    [string] $PackagePath,
    # Inspect the complete Git index, including staged files deleted from disk.
    [switch] $Staged
)

$ErrorActionPreference = 'Stop'
$RepositoryPath = (Resolve-Path -LiteralPath $RepositoryPath).Path
if ($PackagePath -and $Staged) { throw 'PackagePath and Staged cannot be combined.' }
if ($PackagePath) { $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path }

function Invoke-PrivacyGit([string[]] $GitArguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.WorkingDirectory = $RepositoryPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell 5.1.
    $all = @('-c', "safe.directory=$RepositoryPath") + $GitArguments
    $start.Arguments = ($all | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }) -join ' '
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    $buffer = New-Object System.IO.MemoryStream
    try {
        if (-not $process.Start()) { throw 'Cannot start Git for privacy validation.' }
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $process.WaitForExit()
        $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Git could not enumerate/read tracked source for privacy validation.' }
        return ,$buffer.ToArray()
    }
    finally { $buffer.Dispose(); $process.Dispose() }
}

function Test-PrivatePath([string] $Path) {
    $parts = $Path -split '/'
    $name = $parts[-1]
    if ($parts | Where-Object { $_ -match '^(?:\.claude|\.codex|\.cursor|\.aws|\.azure|\.ssh|\.gnupg|\.direnv|\.env)$' }) {
        # Public templates are files, never files inside a private configuration folder.
        return $true
    }
    if ($name -match '^(?:\.env(?:\..*)?|.+\.env(?:\..*)?)$') {
        return $name -notmatch '\.(?:example|sample)$'
    }
    return $name -match '^(?:CLAUDE(?:\.local)?\.md|\.cursorrules|\.mcp\.json|\.envrc|\.netrc|_netrc|\.npmrc|\.pypirc|\.git-credentials|credentials(?:\..*)?\.json|(?:auth|tokens|secrets(?:\..*)?)\.json|client_secret.*\.json|service-account.*\.json|id_(?:rsa|dsa|ecdsa|ed25519)|.*\.local\.json|.*\.(?:pem|key|p8|p12|pfx|jks|keystore|kdbx))$'
}

# Only known token formats and explicit literal credential assignments are inspected.
# Avoid entropy-only checks: source fixtures contain large legitimate encoded images.
$rules = [ordered]@{
    'private-key' = '-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----'
    'github-token' = '\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{70,255})\b'
    'cloud-access-key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'google-api-key' = '\bAIza[0-9A-Za-z_-]{35}\b'
    'slack-token' = '\bxox[baprs]-[A-Za-z0-9-]{16,}\b'
    'ai-api-key' = '\bsk-(?:proj-|svcacct-|ant-)?[A-Za-z0-9_-]{32,}\b'
}
$literalCredential = '(?im)(?:^|[\s"''])(?:api[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token|password)\s*["'']?\s*[:=]\s*["''](?<value>[A-Za-z0-9_./+=-]{16,})["'']'
# dotenv templates often omit quotes. Restrict this rule to explicitly public
# environment templates so code identifiers are not mistaken for literal values.
$environmentCredential = '(?i)^\s*(?:export\s+)?(?:[A-Z0-9_]+_)?(?:API_?KEY|CLIENT_?SECRET|ACCESS_?TOKEN|AUTH_?TOKEN|ACCESS_?KEY|SECRET_?KEY|PASSWORD|SECRET|TOKEN)\s*=\s*(?<value>[^\s"''`$<>{}#]{16,})(?:\s*(?:#.*)?)?$'
$placeholderValue = '^(?i:example|sample|test|fake|dummy|placeholder|replace[_-]?me|your[_-])|^%[A-Za-z_][A-Za-z0-9_]*%$'
$homePath = '(?i)(?:[A-Z]:[\\/]+Users[\\/]+|/(?:Users|home)/)(?<name>[^\\/\s"''<>$`*?:|{}\[\]()]+)'
$placeholderHome = '^(?:user|username|your[-_]?name|runneradmin|runner|public|default|defaultuser0|shared|example|testuser)$'
$findings = New-Object 'System.Collections.Generic.List[object]'
function Add-Finding([string] $Path, [int] $Line, [string] $Rule) {
    # Never include matched values, source excerpts, or Git stderr in the report.
    $safePath = [regex]::Replace($Path, '[\x00-\x1F\x7F]', '?')
    foreach ($pattern in $rules.Values) { $safePath = [regex]::Replace($safePath, $pattern, '[redacted]') }
    $findings.Add([pscustomobject]@{ Path = $safePath; Line = $Line; Rule = $Rule })
}

if ($PackagePath) {
    $packageEntries = New-Object 'System.Collections.Generic.List[string]'
    $folders = New-Object 'System.Collections.Generic.Queue[string]'
    $folders.Enqueue($PackagePath)
    while ($folders.Count -gt 0) {
        foreach ($item in (Get-ChildItem -LiteralPath $folders.Dequeue() -Force)) {
            $relative = $item.FullName.Substring($PackagePath.TrimEnd([char[]]@('\', '/')).Length + 1).Replace('\', '/')
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { Add-Finding $relative 0 'package-link'; continue }
            if ($item.PSIsContainer) { $folders.Enqueue($item.FullName); continue }
            $packageEntries.Add(('100644 ' + ('0' * 40) + " 0`t" + $relative))
        }
    }
    $entries = $packageEntries
}
else { $entries = [System.Text.Encoding]::UTF8.GetString((Invoke-PrivacyGit @('ls-files', '--stage', '-z'))) -split "`0" }
$count = 0
foreach ($entry in $entries) {
    if (-not $entry) { continue }
    if ($entry -notmatch '^(?<mode>\d{6}) (?<object>[a-f0-9]{40,64}) (?<stage>\d)\t(?<path>[\s\S]+)$') { throw 'Invalid Git index entry.' }
    $path = $Matches.path
    $object = $Matches.object
    $mode = $Matches.mode
    if ($Matches.stage -ne '0') { Add-Finding $path 0 'unmerged-index'; continue }
    $isEnvironmentTemplate = ($path -split '/')[-1] -match '^(?:\.env|.+\.env)(?:\..*)?\.(?:example|sample)$'
    if (Test-PrivatePath $path) { Add-Finding $path 0 'private-file' }
    if ($PackagePath -and $path -match '\.pdb$') { Add-Finding $path 0 'debug-symbols-in-package' }
    if ($mode -eq '160000') { Add-Finding $path 0 'submodule-requires-separate-audit'; continue }
    if ($Staged) { $bytes = Invoke-PrivacyGit @('cat-file', 'blob', $object) }
    else {
        $fullPath = Join-Path $(if ($PackagePath) { $PackagePath } else { $RepositoryPath }) $path
        if ($mode -eq '120000') {
            # Read the tracked link target, never follow a link into local personal files.
            $bytes = Invoke-PrivacyGit @('cat-file', 'blob', $object)
        }
        else {
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
            $bytes = [System.IO.File]::ReadAllBytes($fullPath)
        }
    }
    $count++
    if ($bytes.Length -eq 0) { continue }
    # Inspect our own binaries for embedded paths/keys, but do not entropy-scan vendor DLLs.
    if ($PackagePath -and $path -match '^Morupixel\.(?:dll|exe)$') {
        $content = [System.Text.Encoding]::UTF8.GetString($bytes) + "`n" + [System.Text.Encoding]::Unicode.GetString($bytes)
        if ($bytes.Length -gt 1) { $content += "`n" + [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) }
    }
    # Decode UTF-16 BOM files before binary detection, so Windows text is not skipped.
    elseif ($bytes.Length -gt 1 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) { $content = [System.Text.Encoding]::Unicode.GetString($bytes) }
    elseif ($bytes.Length -gt 1 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) { $content = [System.Text.Encoding]::BigEndianUnicode.GetString($bytes) }
    else {
        if ($bytes -contains 0) { continue }
        try { $content = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes) }
        catch [System.Text.DecoderFallbackException] { continue }
    }
    $content = $content.TrimStart([char]0xFEFF)
    $lineNumber = 0
    foreach ($line in ($content -split "`n")) {
        $lineNumber++
        foreach ($rule in $rules.Keys) {
            if ([regex]::IsMatch($line, $rules[$rule])) { Add-Finding $path $lineNumber $rule }
        }
        foreach ($match in [regex]::Matches($line, $literalCredential)) {
            $value = $match.Groups['value'].Value
            if ($value -notmatch $placeholderValue -and $value -notmatch '^(.)\1+$') {
                Add-Finding $path $lineNumber 'literal-credential'
            }
        }
        if ($isEnvironmentTemplate) {
            foreach ($match in [regex]::Matches($line, $environmentCredential)) {
                $value = $match.Groups['value'].Value
                if ($value -notmatch $placeholderValue -and $value -notmatch '^(.)\1+$') {
                    Add-Finding $path $lineNumber 'environment-credential'
                }
            }
        }
        foreach ($match in [regex]::Matches($line, $homePath)) {
            if ($match.Groups['name'].Value -notmatch $placeholderHome) { Add-Finding $path $lineNumber 'personal-home-path' }
        }
    }
}

if ($findings.Count -gt 0) {
    foreach ($finding in ($findings | Sort-Object Path, Line, Rule -Unique)) {
        Write-Host ('{0}:{1}: {2}' -f $finding.Path, $finding.Line, $finding.Rule)
    }
    throw "Privacy validation failed: $($findings.Count) findings. Remove private content from tracked source; no matching values are printed."
}
$scope = if ($PackagePath) { 'package' } elseif ($Staged) { 'index' } else { 'tracked working tree' }
Write-Host "Privacy validation passed: $count files checked ($scope)."
