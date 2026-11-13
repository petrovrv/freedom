# SPDX-License-Identifier: AGPL-3.0-or-later
# Installs the Bridge launcher for the current user (no administrator rights).
# Usage: install.ps1 [-ConfigFrom <config.json>] [-Channel <dir>] [-SwitchAutostart] [-NoStart]
#   - builds launcher\ and copies it to %LOCALAPPDATA%\Programs\AIWorkbenchBridge\AIWorkbenchBridge.exe;
#   - writes launcher.json (update channel + public part of the release signing key);
#   - host config %LOCALAPPDATA%\AIWorkbenchBridge\config.json: copied from -ConfigFrom (with -SwitchAutostart it is
#     refreshed), else kept, else made from install.json (install/aiwb_install.py bridge-config);
#   - stages the newest signed release from the channel (launcher --check-once).
# Freedom 0.4.0, a new computer (setup wizard: freedom_setup.py bridge): without a release signing key on this computer
#   the key is created here (CNG, current user, not exportable) and Bridge is built from this source tree and signed
#   into the channel (default %LOCALAPPDATA%\AIWorkbenchBridge\updates) by release.ps1; without the logon task
#   AIWB-BridgeAutostart a new one is registered and started (-NoStart: registered only).
#   -SwitchAutostart: points an existing logon task AIWB-BridgeAutostart at the launcher (old definition saved next to it).
#     Run it only when no job is running: the old Bridge is stopped and the launcher started.
param(
    [string]$ConfigFrom = '',
    [string]$Channel = '',
    [switch]$SwitchAutostart,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
# AIWB_BRIDGE_RELEASE_KEY / AIWB_BRIDGE_TASK: other names, for a rehearsal on a computer that already runs a Bridge
$KeyName = if ($env:AIWB_BRIDGE_RELEASE_KEY) { $env:AIWB_BRIDGE_RELEASE_KEY } else { 'AIWorkbench.BridgeRelease' }
$task = if ($env:AIWB_BRIDGE_TASK) { $env:AIWB_BRIDGE_TASK } else { 'AIWB-BridgeAutostart' }
$app = Join-Path $env:LOCALAPPDATA 'Programs\AIWorkbenchBridge'
$state = Join-Path $env:LOCALAPPDATA 'AIWorkbenchBridge'
$launcher = Join-Path $app 'AIWorkbenchBridge.exe'
$cfg = Join-Path $state 'config.json'
New-Item -ItemType Directory -Force $app, $state | Out-Null

# Update channel: the parameter, else the one of an earlier install, else a local folder of this user.
$lj = Join-Path $app 'launcher.json'
if (-not $Channel -and (Test-Path $lj)) { $Channel = (Get-Content $lj -Raw | ConvertFrom-Json).updatesDir }
if (-not $Channel) { $Channel = Join-Path $state 'updates' }
New-Item -ItemType Directory -Force $Channel | Out-Null

# Host config (before a local release: release.ps1 checks the new build against it).
if ($ConfigFrom -and (Test-Path $ConfigFrom) -and ($SwitchAutostart -or -not (Test-Path $cfg))) {
    Copy-Item $ConfigFrom $cfg -Force
    "config.json copied from $ConfigFrom"
} elseif ($ConfigFrom -and -not (Test-Path $ConfigFrom) -and -not (Test-Path $cfg)) {
    throw "No host config: $ConfigFrom not found and $cfg missing."
} elseif (-not (Test-Path $cfg)) {
    $py = (Get-Command python -ErrorAction SilentlyContinue).Source
    if (-not $py) { throw 'No host config and no Python to make one from install.json (python install/freedom_setup.py preflight).' }
    & $py (Join-Path $PSScriptRoot '..\install\aiwb_install.py') bridge-config $cfg
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $cfg)) { throw 'config.json from install.json failed (python install/freedom_setup.py init first).' }
}

# Release key: public part of the CNG key release.ps1 signs with. A new computer gets its own key and a local release.
$provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
$haveRelease = @(Get-ChildItem $Channel -Filter 'BridgeAgent-*.sig.json' -ErrorAction SilentlyContinue).Count -gt 0
if (-not [Security.Cryptography.CngKey]::Exists($KeyName, $provider) -or -not $haveRelease) {
    if ($haveRelease) { throw "Signing key $KeyName not found but $Channel has releases: run release.ps1 -CreateKey on the computer that made them." }
    & (Join-Path $PSScriptRoot 'release.ps1') -Channel $Channel -CreateKey -AllowDirty
}
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
    ConvertTo-Json | Set-Content $lj -Encoding ASCII

# Stage the newest release
$p = Start-Process $launcher -ArgumentList '--check-once' -PassThru -Wait
Get-Content (Join-Path $state 'logs\launcher.log') -Tail 4
Get-Content (Join-Path $app 'state.json') -ErrorAction SilentlyContinue

$exists = [bool](Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)

if (-not $exists) {
    # New computer: logon task of this user, restarts the launcher if it exits with an error.
    $action = New-ScheduledTaskAction -Execute $launcher -WorkingDirectory $app
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $trigger.Delay = 'PT45S'
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
        -Description 'Starts the AI Workbench Bridge launcher at logon (Freedom).' | Out-Null
    "Logon task $task registered."
    if ($NoStart) { 'Not started (-NoStart): it starts at the next logon, or: schtasks /run /tn ' + $task; return }
    schtasks /run /tn $task | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        $cur = (Get-Content (Join-Path $app 'state.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).current
        $lf = Join-Path $state ('logs\' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd') + '.log')
        if ($cur -and (Test-Path $lf) -and (Select-String -Path $lf -SimpleMatch "v$cur started" -Quiet)) { "Bridge $cur started."; return }
    }
    throw "Bridge start not confirmed in 120 s: see $state\logs (launcher.log and the newest day log)."
}

if (-not $SwitchAutostart) { 'Installed. Autostart not changed (use -SwitchAutostart).'; return }

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
if ($ConfigFrom -and (Test-Path $ConfigFrom)) { Copy-Item $ConfigFrom $cfg -Force }   # pick up edits made while waiting

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
