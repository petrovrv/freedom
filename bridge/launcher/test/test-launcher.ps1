# SPDX-License-Identifier: AGPL-3.0-or-later
# End-to-end test of the launcher against FakeBridge in a scratch folder (nothing installed, real Bridge untouched).
# Needs the release signing key (release.ps1 -CreateKey). Usage: test-launcher.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
$here = $PSScriptRoot
$T = Join-Path $env:TEMP ('aiwb-launcher-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$root = "$T\app"; $state = "$T\state"; $chan = "$T\channel"
New-Item -ItemType Directory -Force $root, $state, $chan | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /out:"$T\fake.exe" "$here\FakeBridge.cs" | Out-Null
& cmd /c "`"$here\..\build.cmd`" `"$root`"" | Out-Null
'{}' | Set-Content "$state\config.json"

$key = [Security.Cryptography.CngKey]::Open('AIWorkbench.BridgeRelease', [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider)
$pub = [Convert]::ToBase64String($key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
@{ updatesDir = $chan; releaseKey = $pub; stateRoot = $state; checkSeconds = 3; probationSeconds = 15 } | ConvertTo-Json | Set-Content "$root\launcher.json"

function Publish($v, [switch]$BadSig) {
    Copy-Item "$T\fake.exe" "$chan\BridgeAgent-$v.exe"
    $sha = (Get-FileHash "$chan\BridgeAgent-$v.exe").Hash.ToLowerInvariant()
    $ec = New-Object Security.Cryptography.ECDsaCng($key); $ec.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
    $sig = [Convert]::ToBase64String($ec.SignData([Text.Encoding]::UTF8.GetBytes("AIWB-BRIDGE-RELEASE`n$v`n$sha`n")))
    if ($BadSig) { $sig = [Convert]::ToBase64String($ec.SignData([Text.Encoding]::UTF8.GetBytes('other'))) }
    @{ version = $v; sha256 = $sha; signature = $sig; commit = 'test' } | ConvertTo-Json | Set-Content "$chan\BridgeAgent-$v.sig.json"
}
function St { Get-Content "$root\state.json" -Raw | ConvertFrom-Json }
$fails = 0
function Expect($what, [scriptblock]$cond, $sec = 40) {
    $end = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $end) { try { if (& $cond) { "PASS  $what"; return } } catch {} ; Start-Sleep -Milliseconds 500 }
    "FAIL  $what"; $script:fails++
}

Publish '1.0.0'
$L = Start-Process "$root\AIWorkbenchBridge.exe" -ArgumentList "--root `"$root`"" -PassThru
Expect 'first install runs 1.0.0' { (St).current -eq '1.0.0' -and (Get-Content "$state\fake.log") -contains 'start 1.0.0' }
Publish '1.1.0'
Expect 'update drains 1.0.0 and starts 1.1.0' { (St).current -eq '1.1.0' -and (St).previous -eq '1.0.0' -and (Get-Content "$state\fake.log") -contains 'start 1.1.0' }
New-Item "$state\crash-1.2.0" | Out-Null
Publish '1.2.0'
Expect 'crashing 1.2.0 is rolled back to 1.1.0' { (St).current -eq '1.1.0' -and (St).bad -contains '1.2.0' } 60
New-Item "$state\invalid-1.3.0" | Out-Null
Publish '1.3.0'
Expect '1.3.0 failing validate-config is rejected' { (St).bad -contains '1.3.0' -and (St).current -eq '1.1.0' }
Publish '1.4.0' -BadSig
Expect '1.4.0 with a bad signature is rejected' { (St).bad -contains '1.4.0' -and (St).current -eq '1.1.0' }
Publish '1.5.0'
Expect '1.5.0 is installed after the rejected ones' { (St).current -eq '1.5.0' }
New-Item "$state\stop" | Out-Null
Expect 'user Exit stops the launcher' { $L.HasExited }
"--- launcher.log"; Get-Content "$state\logs\launcher.log" | % { $_.Substring(28) }
"--- fake.log"; (Get-Content "$state\fake.log") -join ', '
"versions kept: " + ((Get-ChildItem "$root\versions" -Directory).Name -join ', ')
if (-not $L.HasExited) { Stop-Process -Id $L.Id -Force }
Get-Process fake, BridgeAgent -ErrorAction SilentlyContinue | ? { $_.Path -like "$T*" } | Stop-Process -Force
if ($fails) { "FAILED: $fails"; exit 1 } else { Remove-Item $T -Recurse -Force; 'ALL PASS' }
