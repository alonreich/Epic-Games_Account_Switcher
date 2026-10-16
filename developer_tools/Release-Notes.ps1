<#
.SYNOPSIS
  Turns the internal technical log (docs\changes.md) into consumer-facing release notes and keeps the public history.

.DESCRIPTION
  docs\changes.md  - internal, technical, written by agents/developers for EVERY change (see the rules at the top of that file).
                     Each entry may carry a "Public:" line: one short plain-English sentence for users.
  docs\CHANGELOG.md - public "What's new" history. New versions are inserted at the top; older versions are NEVER removed.

  -Check    Validates the [Unreleased] section (entries exist, user-facing categories have a Public line, no jargon).
  -Promote  Moves [Unreleased] to [Version] - date in changes.md, prepends the public notes to CHANGELOG.md,
            and writes the GitHub release body to -NotesOut.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Check,
    [switch]$Promote,
    [string]$NotesOut = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$changesPath = Join-Path $root 'docs\changes.md'
$changelogPath = Join-Path $root 'docs\CHANGELOG.md'
$utf8 = New-Object System.Text.UTF8Encoding $false
$Version = $Version.Trim().TrimStart('v', 'V')
$MinimumPublicHistory = 5

function Fail([string]$msg) { Write-Host "[NOTES] ERROR: $msg" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $changesPath)) { Fail 'docs\changes.md not found.' }
if (-not (Test-Path $changelogPath)) { Fail 'docs\CHANGELOG.md not found.' }
$changes = [System.IO.File]::ReadAllText($changesPath, $utf8) -replace "`r`n", "`n"

$unrel = [regex]::Match($changes, '(?ms)^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\z)')
if (-not $unrel.Success) { Fail 'No "## [Unreleased]" section in docs\changes.md.' }
$body = $unrel.Groups[1].Value

# ---- parse entries ----------------------------------------------------------------------------
$sectionMap = @{ 'security' = 'Security'; 'added' = 'New'; 'changed' = 'Improved'; 'fixed' = 'Fixed'; 'removed' = 'Removed'; 'internal' = $null }
$order = @('Security', 'New', 'Improved', 'Fixed', 'Removed')
$entries = New-Object System.Collections.ArrayList
$category = $null
$current = $null
foreach ($line in $body -split "`n") {
    if ($line -match '^###\s+(.+?)\s*$') { $category = $Matches[1].Trim().ToLowerInvariant(); continue }
    if ($line -match '^- (.+)$') {
        $current = [pscustomobject]@{ Category = $category; Text = $Matches[1].Trim(); Public = $null }
        [void]$entries.Add($current); continue
    }
    if ($current -and $line -match '^\s+- Public:\s*(.*)$') { $current.Public = $Matches[1].Trim(); continue }
}
if ($entries.Count -eq 0) { Fail 'The [Unreleased] section has no entries. Every release needs at least one change.' }

$jargon = '(?i)(\.(cs|xaml|cmd|ps1|json|dll|csproj)\b|\b(refactor\w*|null|nullref\w*|exception|registry|HKLM|HKCU|mutex|semaphore|async|await|regex|stack ?trace|P/Invoke|DPAPI|HMAC|WinVerifyTrust|Authenticode|thumbprint|msbuild|csproj|namespace|ViewModel|XAML|WPF|dispatcher|null-check)\b|\w+\(\)|`)'
$problems = @()
foreach ($e in $entries) {
    if (-not $sectionMap.ContainsKey([string]$e.Category)) { $problems += "Unknown category '### $($e.Category)' for: $($e.Text)"; continue }
    $isInternal = $e.Category -eq 'internal'
    if (-not $isInternal -and [string]::IsNullOrWhiteSpace($e.Public)) { $problems += "Missing 'Public:' line (or 'Public: -' for internal-only) for: $($e.Text)" }
    if ($e.Public -and $e.Public -ne '-' -and $e.Public -match $jargon) { $problems += "Public line contains technical jargon ('$($Matches[0])'): $($e.Public)" }
    if ($e.Public -and $e.Public -ne '-' -and $e.Public.Length -gt 160) { $problems += "Public line is too long (max 160 chars): $($e.Public)" }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host "[NOTES] $_" -ForegroundColor Red }; Fail 'Fix docs\changes.md and try again.' }

# ---- build public notes -----------------------------------------------------------------------
$groups = @{}
foreach ($e in $entries) {
    $section = $sectionMap[[string]$e.Category]
    if (-not $section -or -not $e.Public -or $e.Public -eq '-') { continue }
    if (-not $groups.ContainsKey($section)) { $groups[$section] = New-Object System.Collections.ArrayList }
    $text = $e.Public.Trim()
    if ($text -notmatch '[.!?]$') { $text += '.' }
    if (-not $groups[$section].Contains($text)) { [void]$groups[$section].Add($text) }
}
$date = (Get-Date).ToString('MMMM d, yyyy', [System.Globalization.CultureInfo]::InvariantCulture)
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("## v$Version - $date")
[void]$sb.AppendLine()
if ($groups.Count -eq 0) { [void]$sb.AppendLine('- Behind-the-scenes improvements for reliability and performance.'); [void]$sb.AppendLine() }
foreach ($section in $order) {
    if (-not $groups.ContainsKey($section)) { continue }
    [void]$sb.AppendLine("### $section")
    foreach ($t in $groups[$section]) { [void]$sb.AppendLine("- $t") }
    [void]$sb.AppendLine()
}
$publicSection = $sb.ToString().TrimEnd() + "`n"

if ($Check -and -not $Promote) {
    Write-Host "[NOTES] OK: $($entries.Count) entries ready for v$Version. Preview:" -ForegroundColor Green
    Write-Host $publicSection
    exit 0
}
if (-not $Promote) { Write-Host $publicSection; exit 0 }

# ---- promote: changes.md ---------------------------------------------------------------------
if ($changes -match "(?m)^## \[$([regex]::Escape($Version))\]") { Fail "docs\changes.md already has a [$Version] section." }
$isoDate = (Get-Date).ToString('yyyy-MM-dd')
$newHeader = "## [Unreleased]`n`n## [$Version] - $isoDate`n"
$changes = $changes.Substring(0, $unrel.Index) + $newHeader + $body.TrimStart("`n") + $changes.Substring($unrel.Index + $unrel.Length)
[System.IO.File]::WriteAllText($changesPath, ($changes -replace "`n", "`r`n"), $utf8)

# ---- promote: CHANGELOG.md (prepend, never truncate) -------------------------------------------
$changelog = [System.IO.File]::ReadAllText($changelogPath, $utf8) -replace "`r`n", "`n"
$marker = '<!-- releases:start -->'
if ($changelog -notmatch [regex]::Escape($marker)) { Fail "docs\CHANGELOG.md is missing the '$marker' marker." }
if ($changelog -match "(?m)^## v$([regex]::Escape($Version)) ") { Fail "docs\CHANGELOG.md already contains v$Version." }
$before = ([regex]::Matches($changelog, '(?m)^## v')).Count
$idx = $changelog.IndexOf($marker) + $marker.Length
$updated = $changelog.Substring(0, $idx) + "`n`n" + $publicSection + $changelog.Substring($idx).TrimStart("`n").Insert(0, "`n")
$after = ([regex]::Matches($updated, '(?m)^## v')).Count
if ($after -ne $before + 1) { Fail "History check failed (had $before releases, now $after). Aborting to protect the public history." }
[System.IO.File]::WriteAllText($changelogPath, ($updated -replace "`n", "`r`n"), $utf8)
if ($after -lt $MinimumPublicHistory) { Write-Host "[NOTES] NOTE: public history has $after releases (target: keep at least $MinimumPublicHistory)." -ForegroundColor Yellow }

# ---- GitHub release body ---------------------------------------------------------------------
if ($NotesOut) {
    $repoUrl = 'https://github.com/alonreich/Epic-Games_Account_Switcher'
    $release = ($publicSection -replace '(?m)^## .*\n\n?', '') +
        "`n---`n" +
        "**Already installed?** The app offers this update automatically (Options -> Check for updates).`n`n" +
        "**Verify your download:** compare the SHA-256 in ``Epic-Games_Account_Switcher.exe.sha256`` with ``Get-FileHash .\Epic-Games_Account_Switcher.exe``.`n`n" +
        "[Full release history]($repoUrl/blob/main/docs/CHANGELOG.md)`n"
    $outPath = if ([System.IO.Path]::IsPathRooted($NotesOut)) { $NotesOut } else { Join-Path $root $NotesOut }
    New-Item -ItemType Directory -Force -Path (Split-Path $outPath -Parent) | Out-Null
    [System.IO.File]::WriteAllText($outPath, $release, $utf8)
}
Write-Host "[NOTES] Promoted [Unreleased] -> v$Version. Public history now lists $after releases." -ForegroundColor Green
exit 0
