# SPDX-License-Identifier: AGPL-3.0-or-later
# Silent per-user install of the current setup, then start CO Desktop (runs via Task Scheduler in the user session).
$setup = Get-ChildItem 'C:\BridgeAgent-v0.6\CO-Desktop\setup\CO-Desktop-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1
Start-Process $setup.FullName -ArgumentList '/S' -Wait
Start-Process (Join-Path $env:LOCALAPPDATA 'Programs\CO Desktop\CO.Desktop.exe')
