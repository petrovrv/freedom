# SPDX-License-Identifier: AGPL-3.0-or-later
# Authenticode signature for CO Desktop files (CO.Desktop.exe after publish, CO-Desktop-Setup-x.y.z.exe after makensis).
# Separate from the ECDSA update signature that Bridge adds in desktop-updates: this one is what Windows SmartScreen
# and "Unknown publisher" look at.
#
#   powershell -File co-desktop\tools\co-sign.ps1 <file> [<file> ...]
#
# Certificate, in this order:
#   1. $env:AIWB_SIGN_THUMBPRINT or %LOCALAPPDATA%\AIWorkbenchBuild\codesign.json {"thumbprint": "..."}:
#      a code signing certificate in Cert:\CurrentUser\My (a hardware token or an imported PFX puts it there);
#   2. codesign.json {"pfx": "C:\\path\\cert.pfx"}: the PFX password comes from keyring service aiwb/codesign,
#      account default (never from a file).
# Without a configured certificate the script says so and exits 0: unsigned builds keep working as before.
param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'

$timestamp = 'http://timestamp.digicert.com'
$cfgPath = Join-Path $env:LOCALAPPDATA 'AIWorkbenchBuild\codesign.json'
$cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { $null }
$thumb = if ($env:AIWB_SIGN_THUMBPRINT) { $env:AIWB_SIGN_THUMBPRINT } elseif ($cfg -and $cfg.thumbprint) { $cfg.thumbprint } else { $null }

$cert = $null
if ($thumb) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Thumbprint -eq $thumb.Replace(' ', '').ToUpper() }
    if (-not $cert) { throw "Code signing certificate $thumb not found in Cert:\CurrentUser\My" }
} elseif ($cfg -and $cfg.pfx) {
    $pw = & python -c "import keyring,sys; v=keyring.get_password('aiwb/codesign','default'); sys.stdout.write(v or '')"
    if (-not $pw) { throw 'PFX password not in keyring aiwb/codesign (account default); use the secret-prompt tool to store it' }
    $cert = Get-PfxCertificate -FilePath $cfg.pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
    $pw = $null
} else {
    Write-Host "co-sign: no certificate configured ($cfgPath or AIWB_SIGN_THUMBPRINT); files left unsigned"
    exit 0
}
if ($cert.NotAfter -lt (Get-Date)) { throw "Certificate expired on $($cert.NotAfter)" }

foreach ($f in $Files) {
    $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $timestamp -IncludeChain NotRoot
    if ($r.Status -ne 'Valid') { throw "Signing $f failed: $($r.Status) $($r.StatusMessage)" }
    Write-Host "signed $f ($($cert.Subject), until $($cert.NotAfter.ToString('yyyy-MM-dd')))"
}
