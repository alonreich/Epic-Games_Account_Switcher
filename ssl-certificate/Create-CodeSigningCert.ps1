<#
.SYNOPSIS
  Creates the project's FREE self-signed code-signing certificate (one-time setup on the release PC).

.DESCRIPTION
  - Creates an RSA-3072 / SHA-256 code-signing certificate in Cert:\CurrentUser\My (private key stays in your Windows profile).
  - Exports a password-protected BACKUP of the private key to ssl-certificate\private\codesign.pfx  (git-ignored, never commit).
  - Exports the PUBLIC certificate to ssl-certificate\EpicGamesAccountSwitcher-CodeSigning.cer      (safe to commit and publish).
  - Writes the PUBLIC thumbprint to ssl-certificate\thumbprint.txt                                    (safe to commit).
    Builds embed this thumbprint so the in-app updater only accepts installers signed with THIS certificate.

  IMPORTANT: back up private\codesign.pfx and its password somewhere safe (password manager / offline USB).
  If you lose it, users must reinstall manually once, because older versions will refuse updates signed with a new key.

.PARAMETER Years          Validity in years (default 5).
.PARAMETER TrustOnThisPC  Also trust the certificate on THIS PC only (CurrentUser Root + TrustedPublisher), so Windows shows it as valid here.
.PARAMETER Force          Replace an existing certificate configuration.
#>
param(
    [int]$Years = 5,
    [string]$Subject = 'CN=Epic Games Account Switcher (Self-Signed Code Signing), O=Epic-Games_Account_Switcher Team',
    [switch]$TrustOnThisPC,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$privateDir = Join-Path $dir 'private'
$thumbFile = Join-Path $dir 'thumbprint.txt'
$cerFile = Join-Path $dir 'EpicGamesAccountSwitcher-CodeSigning.cer'
$pfxFile = Join-Path $privateDir 'codesign.pfx'

if ((Test-Path $thumbFile) -and -not $Force) {
    Write-Host "[CERT] A certificate is already configured (thumbprint.txt exists). Use -Force to replace it." -ForegroundColor Yellow
    Write-Host "[CERT] WARNING: replacing the certificate breaks in-app updates for users on older versions." -ForegroundColor Yellow
    exit 1
}

Write-Host "[CERT] Creating self-signed code-signing certificate ($Years years)..."
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears($Years)

New-Item -ItemType Directory -Path $privateDir -Force | Out-Null
$password = Read-Host 'Choose a strong password to protect the private-key backup (codesign.pfx)' -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath $pfxFile -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerFile -Type CERT | Out-Null
[System.IO.File]::WriteAllText($thumbFile, $cert.Thumbprint, (New-Object System.Text.ASCIIEncoding))

if ($TrustOnThisPC) {
    Import-Certificate -FilePath $cerFile -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    Import-Certificate -FilePath $cerFile -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
    Write-Host "[CERT] Certificate trusted on this PC only."
}

Write-Host ''
Write-Host "[CERT] Done. Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "[CERT] Public files (commit these): thumbprint.txt, EpicGamesAccountSwitcher-CodeSigning.cer"
Write-Host "[CERT] PRIVATE backup (NEVER commit, back it up offline): $pfxFile" -ForegroundColor Yellow
Write-Host "[CERT] Next: run Build.cmd - binaries are now signed automatically."
