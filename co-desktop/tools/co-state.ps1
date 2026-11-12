# SPDX-License-Identifier: AGPL-3.0-or-later
# Copies the real CO state (outside the app package view) to a readable report.
$dir = Join-Path $env:LOCALAPPDATA 'CO'
$out = 'C:\BridgeAgent-v0.6\CO-Desktop\tools\state.txt'
$lines = @("time: $(Get-Date -Format o)")
foreach ($f in 'device.json','desktop.json','folders.json','grants.json') {
  $p = Join-Path $dir $f
  $lines += "=== $f"
  if (Test-Path $p) { $lines += (Get-Content $p -Raw) } else { $lines += '(missing)' }
}
$lines += '=== co-desktop.log (tail)'
$log = Join-Path $dir 'co-desktop.log'
if (Test-Path $log) { $lines += Get-Content $log -Tail 30 }
$lines += '=== audit (tail)'
$a = Join-Path $dir 'audit\audit.jsonl'
if (Test-Path $a) { $lines += Get-Content $a -Tail 10 }
[IO.File]::WriteAllLines($out, $lines)
