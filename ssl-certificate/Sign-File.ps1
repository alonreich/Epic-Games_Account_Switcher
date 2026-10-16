<#
.SYNOPSIS
  Authenticode-signs files with the project's self-signed certificate. Called by Build.cmd.

.DESCRIPTION
  Certificate lookup order:
    1. Cert:\CurrentUser\My, matched by ssl-certificate\thumbprint.txt (no password needed).
    2. ssl-certificate\private\codesign.pfx, password from $env:EGAS_SIGN_PFX_PASSWORD or an interactive prompt.
  A timestamp is added when a timestamp server is reachable, so signatures stay valid after the certificate expires.

  Exit codes: 0 = signed, 2 = no certificate configured (skipped), 1 = error.
  With -Required, "not configured" is an error (used when thumbprint.txt exists, because builds pin that thumbprint).
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [switch]$Required
)
$ErrorActionPreference = 'Stop'
# powershell.exe -File passes "a","b" as one string 'a,b'; normalize to a real list.
$Path = @($Path | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
$dir = $PSScriptRoot
$thumbFile = Join-Path $dir 'thumbprint.txt'

if (-not (Test-Path $thumbFile)) {
    if ($Required) { Write-Host '[SIGN] ERROR: signing is required but no certificate is configured.' -ForegroundColor Red; exit 1 }
    Write-Host '[SIGN] No code-signing certificate configured (run ssl-certificate\Create-CodeSigningCert.ps1). Skipping signing.' -ForegroundColor Yellow
    exit 2
}
$thumb = ((Get-Content -Raw $thumbFile) -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()

$cert = Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { $_.Thumbprint -eq $thumb -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
    $pfx = Join-Path $dir 'private\codesign.pfx'
    if (-not (Test-Path $pfx)) {
        Write-Host "[SIGN] ERROR: certificate $thumb is not in Cert:\CurrentUser\My and private\codesign.pfx is missing." -ForegroundColor Red
        exit 1
    }
    if ([string]::IsNullOrEmpty($env:EGAS_SIGN_PFX_PASSWORD)) { $secure = Read-Host 'Password for ssl-certificate\private\codesign.pfx' -AsSecureString }
    else { $secure = ConvertTo-SecureString $env:EGAS_SIGN_PFX_PASSWORD -AsPlainText -Force }
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $secure, $flags)
    if ($cert.Thumbprint -ne $thumb) { Write-Host "[SIGN] ERROR: codesign.pfx thumbprint $($cert.Thumbprint) does not match thumbprint.txt $thumb." -ForegroundColor Red; exit 1 }
}
if ($cert.NotAfter -lt (Get-Date)) { Write-Host "[SIGN] ERROR: the certificate expired on $($cert.NotAfter)." -ForegroundColor Red; exit 1 }

$timestampServers = @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com', 'http://timestamp.globalsign.com/tsa/r6advanced1')
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file)) { Write-Host "[SIGN] ERROR: file not found: $file" -ForegroundColor Red; exit 1 }
    $stamped = $false
    foreach ($ts in $timestampServers) {
        try {
            Set-AuthenticodeSignature -LiteralPath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $ts -ErrorAction Stop | Out-Null
            $sig = Get-AuthenticodeSignature -LiteralPath $file
            if ($sig.SignerCertificate -and $sig.SignerCertificate.Thumbprint -eq $thumb -and $sig.TimeStamperCertificate) { $stamped = $true; break }
        } catch { }
    }
    if (-not $stamped) {
        Write-Host "[SIGN] WARN: no timestamp server reachable; signing without timestamp (valid until the certificate expires)." -ForegroundColor Yellow
        Set-AuthenticodeSignature -LiteralPath $file -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop | Out-Null
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $file
    if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $thumb -or $sig.Status -eq 'HashMismatch' -or $sig.Status -eq 'NotSigned') {
        Write-Host "[SIGN] ERROR: signature verification failed for $file (status: $($sig.Status))." -ForegroundColor Red
        exit 1
    }
    Write-Host "[SIGN] Signed $file (status on this PC: $($sig.Status); 'UnknownError' just means the self-signed root is not trusted here)."
}
exit 0
