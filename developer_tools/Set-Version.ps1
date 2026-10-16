<#
.SYNOPSIS  Sets the single product version (VersionPrefix in Directory.Build.props). Must be a higher SemVer MAJOR.MINOR.PATCH.
.EXAMPLE   powershell -File developer_tools\Set-Version.ps1 -Version 3.0.1
#>
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Write-Host "[VERSION] ERROR: '$Version' is not MAJOR.MINOR.PATCH (e.g. 3.0.1)." -ForegroundColor Red; exit 1 }
$props = Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props'
$text = [System.IO.File]::ReadAllText($props)
$m = [regex]::Match($text, '<VersionPrefix>([^<]+)</VersionPrefix>')
if (-not $m.Success) { Write-Host '[VERSION] ERROR: VersionPrefix not found in Directory.Build.props.' -ForegroundColor Red; exit 1 }
$current = $m.Groups[1].Value.Trim()
if ([version]$Version -le [version]$current) { Write-Host "[VERSION] ERROR: $Version must be higher than the current $current." -ForegroundColor Red; exit 1 }
$text = $text.Substring(0, $m.Index) + "<VersionPrefix>$Version</VersionPrefix>" + $text.Substring($m.Index + $m.Length)
[System.IO.File]::WriteAllText($props, $text, (New-Object System.Text.UTF8Encoding $false))
Write-Host "[VERSION] $current -> $Version"
exit 0
