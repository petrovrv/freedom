# SPDX-License-Identifier: AGPL-3.0-or-later
# Builds Bridge from this folder, signs it and publishes it to the update channel the launcher watches.
# Usage: release.ps1 [-Channel <dir>] [-CreateKey] [-AllowDirty]
#   -CreateKey  create the signing key on first use (CNG, current user, not exportable) and print its public part
# Output: <Channel>\BridgeAgent-<v>.exe + BridgeAgent-<v>.sig.json. Refuses to overwrite a published version.
param(
    [string]$Channel = 'C:\AIWorkbench\releases\bridge-updates',   # local since 1.7.1: Google Drive flags exe files as malware
    [switch]$CreateKey,
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
$KeyName = if ($env:AIWB_BRIDGE_RELEASE_KEY) { $env:AIWB_BRIDGE_RELEASE_KEY } else { 'AIWorkbench.BridgeRelease' }
Add-Type -AssemblyName System.Core

$src = $PSScriptRoot
$m = Select-String -Path "$src\BridgeAgent.cs" -Pattern 'BridgeVersion = "(\d+\.\d+\.\d+)"' | Select-Object -First 1
if (-not $m) { throw 'BridgeVersion not found in BridgeAgent.cs' }
$version = $m.Matches[0].Groups[1].Value

# A release is a commit: refuse uncommitted Bridge sources unless told otherwise.
# Freedom 0.4.0: a source tree without git (the release tarball, install.ps1 on a new PC) is published as "src".
$inGit = (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $src '..\.git'))
if ($inGit) {
    $dirty = git -C $src status --porcelain -- . 2>$null
    if ($dirty -and -not $AllowDirty) { throw "Uncommitted changes in bridge\ - commit first:`n$($dirty -join "`n")" }
    $commit = "$(git -C $src rev-parse --short HEAD 2>$null)".Trim()
    if (-not $commit) { $commit = 'src' }
    if ($dirty) { $commit += '-dirty' }
} else {
    $commit = 'src'
}

New-Item -ItemType Directory -Force $Channel | Out-Null
$exeOut = Join-Path $Channel "BridgeAgent-$version.exe"
$sigOut = Join-Path $Channel "BridgeAgent-$version.sig.json"
if (Test-Path $sigOut) { throw "Bridge $version is already published ($sigOut). Raise BridgeVersion." }

$build = Join-Path $env:LOCALAPPDATA "AIWorkbenchBuild\bridge-release\$version"
& cmd /c "`"$src\build.cmd`" `"$build`""
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Join-Path $build 'BridgeAgent.exe'

# Gate: the new build must accept the live host config.
$cfg = Join-Path $env:LOCALAPPDATA 'AIWorkbenchBridge\config.json'
if (-not (Test-Path $cfg)) { $cfg = 'C:\BridgeAgent-v0.6\config.json' }
$p = Start-Process -FilePath $exe -ArgumentList "--validate-config --config `"$cfg`"" -PassThru -Wait -WindowStyle Hidden
$check = Get-Content (Join-Path (Split-Path $cfg) 'config-check.txt') -Raw -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0) { throw "validate-config failed against ${cfg}: $check" }

$provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
if ([Security.Cryptography.CngKey]::Exists($KeyName, $provider)) {
    $key = [Security.Cryptography.CngKey]::Open($KeyName, $provider)
} elseif ($CreateKey) {
    $kp = New-Object Security.Cryptography.CngKeyCreationParameters
    $kp.Provider = $provider
    $kp.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $kp.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $KeyName, $kp)
    'Created signing key ' + $KeyName
} else {
    throw "Signing key $KeyName not found. Run with -CreateKey once (then reinstall the launcher with the new public key)."
}
$pub = [Convert]::ToBase64String($key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))

$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$text = "AIWB-BRIDGE-RELEASE`n$version`n$sha`n"
$ec = New-Object Security.Cryptography.ECDsaCng($key)
$ec.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
$sig = [Convert]::ToBase64String($ec.SignData([Text.Encoding]::UTF8.GetBytes($text)))

Copy-Item $exe $exeOut -Force
[ordered]@{
    version   = $version
    file      = "BridgeAgent-$version.exe"
    sha256    = $sha
    commit    = $commit
    builtUtc  = (Get-Date).ToUniversalTime().ToString('o')
    signature = $sig
} | ConvertTo-Json | Set-Content $sigOut -Encoding ASCII
"Published Bridge $version (commit $commit) to $Channel"
"Release public key: $pub"
