# SPDX-License-Identifier: AGPL-3.0-or-later
# Installs the Bridge launcher for the current user (no administrator rights).
# Usage: install.ps1 [-ConfigFrom <config.json>] [-SwitchAutostart]
#   - builds launcher\ and copies it to %LOCALAPPDATA%\Programs\AIWorkbenchBridge\AIWorkbenchBridge.exe;
#   - writes launcher.json (update channel + public part of the release signing key);
#   - copies the host config to %LOCALAPPDATA%\AIWorkbenchBridge\config.json (with -SwitchAutostart it is refreshed);
#   - stages the newest signed release from the channel (launcher --check-once).
#   -SwitchAutostart: also points the logon task AIWB-BridgeAutostart at the launcher (old definition saved next to it).
#     Run it only when no job is running: the old Bridge is stopped and the launcher started.
param(
    [string]$ConfigFrom = 'C:\BridgeAgent-v0.6\config.json',
    [string]$Channel = 'C:\AIWorkbench\releases\bridge-updates',   # local since 1.7.1: Google Drive flags exe files as malware
    [switch]$SwitchAutostart
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
$KeyName = 'AIWorkbench.BridgeRelease'
$app = Join-Path $env:LOCALAPPDATA 'Programs\AIWorkbenchBridge'
$state = Join-Path $env:LOCALAPPDATA 'AIWorkbenchBridge'
$launcher = Join-Path $app 'AIWorkbenchBridge.exe'
New-Item -ItemType Directory -Force $app, $state | Out-Null

# Release key: public part of the CNG key release.ps1 signs with.
$provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
if (-not [Security.Cryptography.CngKey]::Exists($KeyName, $provider)) { throw "Signing key $KeyName not found: run release.ps1 -CreateKey first." }
$key = [Security.Cryptography.CngKey]::Open($KeyName, $provider)
$pub = [Convert]::ToBase64String($key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))

# Launcher binary
$build = Join-Path $env:LOCALAPPDATA 'AIWorkbenchBuild\launcher'
& cmd /c "`"$PSScriptRoot\launcher\build.cmd`" `"$build`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }
if (Get-Process AIWorkbenchBridge -ErrorAction SilentlyContinue | ? { $_.Path -eq $launcher }) {
    throw 'The launcher is running; stop it first (Bridge keeps running).'
}
Copy-Item "$build\AIWorkbenchBridge.exe" $launcher -Force

[ordered]@{ updatesDir = $Channel; releaseKey = $pub; stateRoot = '%LOCALAPPDATA%\AIWorkbenchBridge' } |
    ConvertTo-Json | Set-Content (Join-Path $app 'launcher.json') -Encoding ASCII

# Host config
$cfg = Join-Path $state 'config.json'
if ((Test-Path $ConfigFrom) -and ($SwitchAutostart -or -not (Test-Path $cfg))) {
    Copy-Item $ConfigFrom $cfg -Force
    "config.json copied from $ConfigFrom"
} elseif (-not (Test-Path $cfg)) {
    throw "No host config: $ConfigFrom not found and $cfg missing."
}

# Stage the newest release
$p = Start-Process $launcher -ArgumentList '--check-once' -PassThru -Wait
Get-Content (Join-Path $state 'logs\launcher.log') -Tail 4
Get-Content (Join-Path $app 'state.json') -ErrorAction SilentlyContinue

if (-not $SwitchAutostart) { 'Installed. Autostart not changed (use -SwitchAutostart).'; return }

# Autostart: same logon task, now running the launcher.
$task = 'AIWB-BridgeAutostart'
function OldBridge { Get-CimInstance Win32_Process -Filter "Name='BridgeAgent.exe'" | ? { $_.CommandLine -notmatch '--mcp-device' } }

# Wait until the running Bridge has no job (agent CLIs are its children). Run this from a one-shot scheduled task,
# not from a Bridge job: stopping Bridge would kill the job that runs the script.
$deadline = (Get-Date).AddHours(6)
while ($true) {
    $ids = @(@(OldBridge) | % { $_.ProcessId })
    $jobs = @(Get-CimInstance Win32_Process | ? { $ids -contains $_.ParentProcessId -and $_.Name -in @('claude.exe', 'codex.exe', 'node.exe') })
    if ($jobs.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) { throw 'Jobs still running after 6 h; autostart not changed.' }
    Start-Sleep -Seconds 20
}
if (Test-Path $ConfigFrom) { Copy-Item $ConfigFrom $cfg -Force }   # pick up edits made while waiting

$xmlOld = (schtasks /query /tn $task /xml) -join "`n"
$backup = Join-Path $app "$task.before-launcher.xml"
$xmlOld | Set-Content $backup -Encoding Unicode
$xml = $xmlOld
$xml = $xml -replace '<Command>[^<]*</Command>', "<Command>$launcher</Command>" `
            -replace '<WorkingDirectory>[^<]*</WorkingDirectory>', "<WorkingDirectory>$app</WorkingDirectory>"
$tmp = Join-Path $env:TEMP "$task.xml"
$xml | Set-Content $tmp -Encoding Unicode
schtasks /create /tn $task /xml $tmp /f | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'schtasks /create failed' }

OldBridge | % { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 3
schtasks /run /tn $task | Out-Null

# Health: the launcher must start a version and Bridge must log its start; otherwise restore the old autostart.
$healthy = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    $cur = (Get-Content (Join-Path $app 'state.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).current
    $lf = Join-Path $state ('logs\' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd') + '.log')
    if ($cur -and (Test-Path $lf) -and (Select-String -Path $lf -SimpleMatch "v$cur started" -Quiet)) { $healthy = $true; break }
}
if (-not $healthy) {
    Get-Process AIWorkbenchBridge -ErrorAction SilentlyContinue | Stop-Process -Force
    OldBridge | % { Stop-Process -Id $_.ProcessId -Force }
    schtasks /create /tn $task /xml $backup /f | Out-Null
    schtasks /run /tn $task | Out-Null
    throw 'Launcher start not confirmed in 120 s; old autostart restored.'
}
"Autostart now runs $launcher (Bridge $cur)"
